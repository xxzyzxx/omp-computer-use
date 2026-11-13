using System.Drawing;
using FlaUI.Core.AutomationElements;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>Everything a snapshot learned about one element (physical-pixel bounds).</summary>
public sealed record ElementMeta(
    string Ref,
    string WindowHandle,
    IntPtr WindowHwnd,
    uint Pid,
    int Generation,
    string RuntimeKey,
    string Role,
    string? Name,
    string? AutomationId,
    Rectangle Bounds,
    bool IsPassword,
    bool Interactive)
{
    public Point Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
}

/// <summary>A ref resolved against the live tree.</summary>
public sealed record ResolvedElement(AutomationElement Element, ElementMeta Meta);

/// <summary>
/// Maps refs (<c>w1e5</c>) to UIA elements. A ref is minted the first time an element's RuntimeId is seen in a
/// window and reused across later snapshots of that window; resolving re-reads the RuntimeId from the provider
/// and fails with <c>stale_ref</c> when the element is gone or has been replaced. Also stores the last
/// set-of-marks map so actions can target <c>mark</c> numbers.
/// </summary>
public class ElementRegistry
{
    private sealed class Entry
    {
        public required AutomationElement Element;
        public required ElementMeta Meta;
        public int LastSeenGeneration;
    }

    private const int KeepGenerations = 3;
    private const int MaxEntries = 6000;

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byRef = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _refByRuntime = new(StringComparer.Ordinal); // "<window>|<runtimeKey>" -> ref
    private readonly Dictionary<string, int> _windowCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _windowGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ElementMeta>> _lastSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, string> _marks = new();
    private string? _marksWindow;
    private string? _marksImageId;

    public static string RuntimeKey(int[]? runtimeId) => runtimeId == null || runtimeId.Length == 0 ? "" : string.Join('.', runtimeId);

    /// <summary>Starts a new snapshot generation for a window and returns its id.</summary>
    public int BeginGeneration(string windowHandle)
    {
        lock (_lock)
        {
            var gen = _windowGenerations.TryGetValue(windowHandle, out var g) ? g + 1 : 1;
            _windowGenerations[windowHandle] = gen;
            return gen;
        }
    }

    public int CurrentGeneration(string windowHandle)
    {
        lock (_lock)
        {
            return _windowGenerations.TryGetValue(windowHandle, out var g) ? g : 0;
        }
    }

    /// <summary>
    /// Registers an element seen in generation <paramref name="generation"/>. Returns the existing ref when the
    /// RuntimeId was seen before in this window, otherwise mints the next <c>wNeM</c>.
    /// </summary>
    public string Register(string windowHandle, int generation, AutomationElement element, Func<string, ElementMeta> buildMeta, string runtimeKey)
    {
        lock (_lock)
        {
            string refId;
            var runtimeLookup = runtimeKey.Length == 0 ? null : windowHandle + "|" + runtimeKey;
            if (runtimeLookup != null && _refByRuntime.TryGetValue(runtimeLookup, out var existing) && _byRef.ContainsKey(existing))
            {
                refId = existing;
            }
            else
            {
                _windowCounters.TryGetValue(windowHandle, out var n);
                _windowCounters[windowHandle] = ++n;
                refId = $"{windowHandle}e{n}";
                if (runtimeLookup != null)
                {
                    _refByRuntime[runtimeLookup] = refId;
                }
            }

            var meta = buildMeta(refId);
            _byRef[refId] = new Entry { Element = element, Meta = meta, LastSeenGeneration = generation };
            return refId;
        }
    }

    /// <summary>Records the final node list of a snapshot (for diff_since and marks) and evicts stale entries.</summary>
    public void CompleteGeneration(string windowHandle, int generation, List<ElementMeta> nodes)
    {
        lock (_lock)
        {
            _lastSnapshot[windowHandle] = nodes;
            var stale = _byRef.Where(kv => kv.Value.Meta.WindowHandle.Equals(windowHandle, StringComparison.OrdinalIgnoreCase)
                                           && kv.Value.LastSeenGeneration < generation - KeepGenerations)
                .Select(kv => kv.Key).ToList();
            foreach (var key in stale)
            {
                Remove(key);
            }
            if (_byRef.Count > MaxEntries)
            {
                foreach (var key in _byRef.OrderBy(kv => kv.Value.LastSeenGeneration).Take(_byRef.Count - MaxEntries).Select(kv => kv.Key).ToList())
                {
                    Remove(key);
                }
            }
        }
    }

    private void Remove(string refId)
    {
        if (_byRef.Remove(refId, out var entry) && entry.Meta.RuntimeKey.Length > 0)
        {
            _refByRuntime.Remove(entry.Meta.WindowHandle + "|" + entry.Meta.RuntimeKey);
        }
    }

    /// <summary>Nodes of the most recent snapshot of a window (empty when none was taken).</summary>
    public IReadOnlyList<ElementMeta> LastSnapshot(string windowHandle)
    {
        lock (_lock)
        {
            return _lastSnapshot.TryGetValue(windowHandle, out var list) ? list : Array.Empty<ElementMeta>();
        }
    }

    public ElementMeta? TryGetMeta(string refId)
    {
        lock (_lock)
        {
            return _byRef.TryGetValue(refId, out var e) ? e.Meta : null;
        }
    }

    public bool HasElement(string refId)
    {
        lock (_lock)
        {
            return _byRef.ContainsKey(refId);
        }
    }

    /// <summary>Raw cached element without liveness verification (prefer <see cref="Resolve"/>).</summary>
    public AutomationElement? GetElement(string refId)
    {
        lock (_lock)
        {
            return _byRef.TryGetValue(refId, out var e) ? e.Element : null;
        }
    }

    /// <summary>
    /// Resolves a ref against the live provider: the RuntimeId is re-read and must match; otherwise the ref is
    /// stale (element destroyed or replaced) and the caller must snapshot again. Never returns a different element.
    /// </summary>
    public ResolvedElement Resolve(string refId)
    {
        Entry? entry;
        lock (_lock)
        {
            _byRef.TryGetValue(refId, out entry);
        }
        if (entry == null)
        {
            throw new ToolException("unknown_ref", $"Ref {refId} is unknown.",
                "Call windows_snapshot (or windows_find) and use a ref from its output.");
        }

        string liveKey;
        try
        {
            liveKey = RuntimeKey(entry.Element.Properties.RuntimeId.ValueOrDefault);
            // Touch a second property so providers that lazily detach report the element as gone.
            _ = entry.Element.Properties.BoundingRectangle.ValueOrDefault;
        }
        catch (Exception ex)
        {
            lock (_lock) { Remove(refId); }
            throw new ToolException("stale_ref", $"Ref {refId} is stale ({ex.GetType().Name}: element no longer available).",
                "Call windows_snapshot to get fresh refs, then retry.");
        }

        if (entry.Meta.RuntimeKey.Length > 0 && liveKey.Length > 0 && !string.Equals(liveKey, entry.Meta.RuntimeKey, StringComparison.Ordinal))
        {
            lock (_lock) { Remove(refId); }
            throw new ToolException("stale_ref", $"Ref {refId} is stale (the element was replaced).",
                "Call windows_snapshot to get fresh refs, then retry.");
        }

        return new ResolvedElement(entry.Element, entry.Meta);
    }

    /// <summary>Refreshes the cached bounds of a ref from the live element (used before coordinate fallbacks).</summary>
    public ElementMeta RefreshBounds(ResolvedElement resolved)
    {
        Rectangle bounds;
        try
        {
            bounds = resolved.Element.Properties.BoundingRectangle.ValueOrDefault;
        }
        catch
        {
            return resolved.Meta;
        }
        var meta = resolved.Meta with { Bounds = bounds };
        lock (_lock)
        {
            if (_byRef.TryGetValue(meta.Ref, out var e))
            {
                e.Meta = meta;
            }
        }
        return meta;
    }

    // ----- marks -----

    public void SetMarks(string windowHandle, string imageId, Dictionary<int, string> marks)
    {
        lock (_lock)
        {
            _marks = marks;
            _marksWindow = windowHandle;
            _marksImageId = imageId;
        }
    }

    public (string? windowHandle, string? imageId, IReadOnlyDictionary<int, string> marks) CurrentMarks()
    {
        lock (_lock)
        {
            return (_marksWindow, _marksImageId, _marks);
        }
    }

    public string ResolveMark(int mark)
    {
        lock (_lock)
        {
            if (_marks.Count == 0)
            {
                throw new ToolException("no_marks", "No set-of-marks screenshot has been taken.",
                    "Call windows_screenshot with marks=true, then use the mark numbers it returns.");
            }
            if (!_marks.TryGetValue(mark, out var refId))
            {
                throw new ToolException("unknown_mark", $"Mark {mark} is not in the last marks screenshot (1..{_marks.Keys.Max()}).",
                    "Use a mark number from the latest windows_screenshot marks=true result, or take a new one.");
            }
            return refId;
        }
    }
}
