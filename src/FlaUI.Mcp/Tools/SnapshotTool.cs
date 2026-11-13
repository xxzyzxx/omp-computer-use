using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Fetches a whole UIA subtree in one cross-process round trip (FlaUI cache request, control view)
/// and converts it to plain <see cref="SnapNode"/> trees. Falls back to a bounded live walk when the
/// provider cannot serve the cached fetch. Shared by the snapshot, find and get-text tools.
/// </summary>
internal static class TreeFetch
{
    public const int UncachedBudgetMs = 8000;

    public static (SnapNode Root, string Method) Fetch(Services s, WindowInfo window, ToolContext ctx)
    {
        var automation = s.Session.AutomationFor(window);
        try
        {
            return (FetchCached(automation, window.Hwnd), "cached");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Providers that hang or refuse bulk caching (some Qt/Java/legacy UIs); fall back to
            // a bounded, slow live walk instead of failing the snapshot.
            return (FetchUncached(automation, window.Hwnd, ctx), "uncached");
        }
    }

    private static SnapNode FetchCached(AutomationBase automation, IntPtr hwnd)
    {
        var lib = automation.PropertyLibrary;
        var pat = automation.PatternLibrary;
        var request = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            AutomationElementMode = AutomationElementMode.Full,
            TreeFilter = new PropertyCondition(lib.Element.IsControlElement, true)
        };
        foreach (var p in new[]
        {
            lib.Element.ControlType, lib.Element.Name, lib.Element.AutomationId, lib.Element.BoundingRectangle,
            lib.Element.IsEnabled, lib.Element.IsOffscreen, lib.Element.IsPassword, lib.Element.IsKeyboardFocusable,
            lib.Element.HasKeyboardFocus, lib.Element.ClassName, lib.Element.RuntimeId, lib.Element.NativeWindowHandle,
            lib.Element.ProcessId, lib.Element.LocalizedControlType
        })
        {
            request.Add(p);
        }
        foreach (var p in new[]
        {
            lib.PatternAvailability.IsInvokePatternAvailable, lib.PatternAvailability.IsTogglePatternAvailable,
            lib.PatternAvailability.IsSelectionItemPatternAvailable, lib.PatternAvailability.IsExpandCollapsePatternAvailable,
            lib.PatternAvailability.IsValuePatternAvailable, lib.PatternAvailability.IsRangeValuePatternAvailable,
            lib.PatternAvailability.IsScrollPatternAvailable, lib.PatternAvailability.IsTextPatternAvailable,
            lib.PatternAvailability.IsSelectionPatternAvailable,
            // Pattern property values: without these a cached pattern throws on ToggleState/Value/… reads.
            lib.Toggle.ToggleState, lib.Value.Value, lib.Value.IsReadOnly, lib.SelectionItem.IsSelected,
            lib.ExpandCollapse.ExpandCollapseState, lib.RangeValue.Value, lib.RangeValue.Minimum, lib.RangeValue.Maximum
        })
        {
            request.Add(p);
        }
        foreach (var p in new[]
        {
            pat.InvokePattern, pat.TogglePattern, pat.SelectionItemPattern, pat.ExpandCollapsePattern,
            pat.ValuePattern, pat.RangeValuePattern, pat.ScrollPattern, pat.TextPattern, pat.SelectionPattern
        })
        {
            request.Add(p);
        }

        using (request.Activate())
        {
            var root = automation.FromHandle(hwnd);
            var node = Read(root);
            FillCachedChildren(node, root);
            return node;
        }
    }

    private static void FillCachedChildren(SnapNode node, AutomationElement element)
    {
        AutomationElement[] children;
        try
        {
            children = element.CachedChildren;
        }
        catch
        {
            return; // provider refuses child enumeration below this node
        }
        foreach (var child in children)
        {
            var childNode = Read(child);
            node.Children.Add(childNode);
            FillCachedChildren(childNode, child);
        }
    }

    private static SnapNode FetchUncached(AutomationBase automation, IntPtr hwnd, ToolContext ctx)
    {
        var condition = new PropertyCondition(automation.PropertyLibrary.Element.IsControlElement, true);
        var root = automation.FromHandle(hwnd);
        var node = Read(root);
        var visited = 1;
        FillLive(node, root, condition, Stopwatch.StartNew(), ctx, ref visited);
        return node;
    }

    private static void FillLive(SnapNode node, AutomationElement element, ConditionBase condition,
        Stopwatch sw, ToolContext ctx, ref int visited)
    {
        if (sw.ElapsedMilliseconds > UncachedBudgetMs) return;
        AutomationElement[] children;
        try
        {
            children = element.FindAll(TreeScope.Children, condition);
        }
        catch
        {
            return;
        }
        foreach (var child in children)
        {
            if (++visited % 32 == 0) ctx.ThrowIfCancelled();
            if (sw.ElapsedMilliseconds > UncachedBudgetMs) return;
            var childNode = Read(child);
            node.Children.Add(childNode);
            FillLive(childNode, child, condition, sw, ctx, ref visited);
        }
    }

    private static SnapNode Read(AutomationElement e)
    {
        T? Get<T>(Func<T?> f) where T : class { try { return f(); } catch { return null; } }
        bool GetBool(Func<bool> f) { try { return f(); } catch { return false; } }
        int GetPid() { try { return e.Properties.ProcessId.ValueOrDefault; } catch { return 0; } }

        ControlType? controlType = null;
        try { controlType = e.Properties.ControlType.ValueOrDefault; }
        catch { /* unknown control type -> "element" */ }

        SnapToggleState? toggle = null;
        var canToggle = false;
        try
        {
            canToggle = e.Patterns.Toggle.IsSupported;
            if (canToggle)
            {
                toggle = e.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault switch
                {
                    ToggleState.On => SnapToggleState.On,
                    ToggleState.Indeterminate => SnapToggleState.Indeterminate,
                    _ => SnapToggleState.Off
                };
            }
        }
        catch { /* pattern unreadable */ }

        string? value = null;
        var canValue = false;
        var readOnly = false;
        try
        {
            canValue = e.Patterns.Value.IsSupported;
            if (canValue)
            {
                value = e.Patterns.Value.Pattern.Value.ValueOrDefault;
                readOnly = e.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault;
            }
        }
        catch { /* pattern unreadable */ }

        var selected = false;
        try
        {
            if (e.Patterns.SelectionItem.IsSupported) selected = e.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault;
        }
        catch { /* pattern unreadable */ }

        SnapExpandState? expand = null;
        try
        {
            if (e.Patterns.ExpandCollapse.IsSupported)
            {
                expand = e.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault switch
                {
                    ExpandCollapseState.Expanded => SnapExpandState.Expanded,
                    ExpandCollapseState.PartiallyExpanded => SnapExpandState.PartiallyExpanded,
                    ExpandCollapseState.LeafNode => SnapExpandState.LeafNode,
                    _ => SnapExpandState.Collapsed
                };
            }
        }
        catch { /* pattern unreadable */ }

        double? rangeValue = null;
        try
        {
            if (e.Patterns.RangeValue.IsSupported) rangeValue = e.Patterns.RangeValue.Pattern.Value.ValueOrDefault;
        }
        catch { /* pattern unreadable */ }

        var bounds = Rectangle.Empty;
        try { bounds = e.Properties.BoundingRectangle.ValueOrDefault; }
        catch { /* no bounds */ }

        int[]? runtimeId = null;
        try { runtimeId = e.Properties.RuntimeId.ValueOrDefault; }
        catch { /* no runtime id -> refs mint per call */ }

        return new SnapNode
        {
            Role = SnapshotBuilder.RoleName(controlType),
            Name = Get(() => e.Properties.Name.ValueOrDefault),
            AutomationId = Get(() => e.Properties.AutomationId.ValueOrDefault),
            Bounds = bounds,
            Enabled = GetBool(() => e.Properties.IsEnabled.ValueOrDefault),
            Offscreen = GetBool(() => e.Properties.IsOffscreen.ValueOrDefault),
            Password = GetBool(() => e.Properties.IsPassword.ValueOrDefault),
            KeyboardFocusable = GetBool(() => e.Properties.IsKeyboardFocusable.ValueOrDefault),
            HasKeyboardFocus = GetBool(() => e.Properties.HasKeyboardFocus.ValueOrDefault),
            ClassName = Get(() => e.Properties.ClassName.ValueOrDefault),
            ProcessId = GetPid(),
            LocalizedControlType = Get(() => e.Properties.LocalizedControlType.ValueOrDefault),
            Invoke = GetBool(() => e.Patterns.Invoke.IsSupported),
            Toggle = canToggle,
            SelectionItem = GetBool(() => e.Patterns.SelectionItem.IsSupported),
            ExpandCollapse = GetBool(() => e.Patterns.ExpandCollapse.IsSupported),
            Value = canValue,
            RangeValue = GetBool(() => e.Patterns.RangeValue.IsSupported),
            Scroll = GetBool(() => e.Patterns.Scroll.IsSupported),
            Text = GetBool(() => e.Patterns.Text.IsSupported),
            Selection = GetBool(() => e.Patterns.Selection.IsSupported),
            ToggleStateV = toggle,
            ValueText = value,
            ReadOnly = readOnly,
            Selected = selected,
            ExpandState = expand,
            RangeVal = rangeValue,
            Element = e,
            RuntimeId = runtimeId
        };
    }

    /// <summary>Registers one emitted node and returns its ref (mints or reuses across generations).</summary>
    public static string Register(Services s, WindowInfo window, int generation, SnapNode node)
    {
        if (node.Element == null)
        {
            throw new InvalidOperationException("Snapshot node has no live element; the fetch layer must attach one.");
        }
        var key = ElementRegistry.RuntimeKey(node.RuntimeId);
        return s.Elements.Register(window.Handle, generation, node.Element,
            refId => new ElementMeta(refId, window.Handle, window.Hwnd, window.Pid, generation, key,
                node.Role, node.Name, node.AutomationId, node.Bounds, node.Password, SnapshotBuilder.IsInteractive(node)),
            key);
    }
}

/// <summary>Full accessibility tree of a window with refs, pruning and optional diff against the last snapshot.</summary>
public sealed class SnapshotTool : ToolBase
{
    private readonly Services _s;

    /// <summary>Last emitted lines per window, keyed by RuntimeKey (fallback ref), for diff_since.</summary>
    private readonly Dictionary<string, Dictionary<string, PrevNode>> _lastLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastGens = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _windowOrder = new();
    private readonly object _storeLock = new();

    public SnapshotTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_snapshot";
    public override ToolKind Kind => ToolKind.Perception;

    public override string Description =>
        "Accessibility snapshot of a window: one line per control with role, quoted name, [ref=wNeM], " +
        "#automationId, @x,y,w,h in physical pixels, state flags and value. Refs stay valid across " +
        "snapshots of the same window. Default: interactive_only=true, visible_only=true, max_depth=14. " +
        "diff_since=<generation> returns only what changed since that snapshot. Very large trees are " +
        "truncated; narrow with filter= (substring over name/id/role), max_depth= or a child window.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new { type = "string", description = "Window handle (wN), title substring, process name or hwnd; default = foreground window" },
            interactive_only = new { type = "boolean", description = "Keep actionable controls, named state text and named containers; flatten the rest (default true)" },
            max_depth = new { type = "integer", description = "Maximum kept depth (default 14)" },
            visible_only = new { type = "boolean", description = "Drop offscreen/empty-bounds nodes (default true)" },
            filter = new { type = "string", description = "Case-insensitive substring over name/role/automation id; keeps matches and their ancestors" },
            diff_since = new { type = "integer", description = "Snapshot generation to diff against; returns only +/~/- changed lines" },
            max_nodes = new { type = "integer", description = "Node cap after pruning (default 600)" },
            include_bounds = new { type = "boolean", description = "Include @x,y,w,h physical-pixel bounds (default true)" }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
        => Task.FromResult(Run(arguments, ctx));

    private McpToolResult Run(JsonElement? arguments, ToolContext ctx)
    {
        var window = _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));
        ctx.Gate.EnsureView(window);
        ctx.TargetApp = window.ProcessName;
        if (window.Minimized)
        {
            throw new ToolException("window_minimized", $"{window.Handle} ('{window.Title}') is minimized.",
                "Restore the window first (windows_launch with the same app, or ask the user), then call windows_snapshot again.");
        }
        if (window.Bounds.Width <= 0 || window.Bounds.Height <= 0)
        {
            throw new ToolException("window_empty", $"{window.Handle} ('{window.Title}') has no visible area.",
                "Call windows_list_windows; pick a window with a non-empty bounds rect.");
        }

        var sw = Stopwatch.StartNew();
        var filter = GetStringArgument(arguments, "filter");
        // Filtered snapshots are views: they never become the diff/marks baseline.
        var isBaseline = string.IsNullOrWhiteSpace(filter);
        IReadOnlyList<ElementMeta> previous;
        Dictionary<string, PrevNode>? previousLines;
        int previousGen;
        lock (_storeLock)
        {
            previous = _s.Elements.LastSnapshot(window.Handle);
            previousLines = _lastLines.TryGetValue(window.Handle, out var d) ? d : null;
            previousGen = _lastGens.TryGetValue(window.Handle, out var g) ? g : 0;
        }

        var gen = _s.Elements.BeginGeneration(window.Handle);
        var (root, method) = TreeFetch.Fetch(_s, window, ctx);

        var metas = new List<ElementMeta>();
        var output = SnapshotBuilder.Emit(
            new[] { root },
            new SnapshotOptions(
                InteractiveOnly: GetBoolArgument(arguments, "interactive_only", true),
                VisibleOnly: GetBoolArgument(arguments, "visible_only", true),
                MaxDepth: Math.Clamp(GetIntArgument(arguments, "max_depth", 14), 1, 64),
                Filter: filter,
                MaxNodes: Math.Max(1, GetIntArgument(arguments, "max_nodes", _s.Config.SnapshotMaxNodes)),
                MaxChars: Math.Max(1000, _s.Config.SnapshotMaxChars),
                IncludeBounds: GetBoolArgument(arguments, "include_bounds", true)),
            node => TreeFetch.Register(_s, window, gen, node));

        var interactiveCount = 0;
        foreach (var e in output.Nodes)
        {
            var meta = _s.Elements.TryGetMeta(e.Ref);
            if (meta == null) continue;
            metas.Add(meta);
            if (meta.Interactive) interactiveCount++;
        }
        if (isBaseline)
        {
            _s.Elements.CompleteGeneration(window.Handle, gen, metas);

            // Current lines for the next diff, keyed by RuntimeKey (fallback ref).
            var currentLines = new Dictionary<string, PrevNode>(output.Nodes.Count);
            foreach (var e in output.Nodes)
            {
                var key = SnapshotBuilder.NodeKey(e.Node, e.Ref);
                currentLines[key] = new PrevNode(key, e.Line);
            }
            lock (_storeLock)
            {
                _lastLines[window.Handle] = currentLines;
                _lastGens[window.Handle] = gen;
                if (!_windowOrder.Contains(window.Handle)) _windowOrder.Add(window.Handle);
                while (_windowOrder.Count > 64)
                {
                    var oldest = _windowOrder[0];
                    _windowOrder.RemoveAt(0);
                    _lastLines.Remove(oldest);
                    _lastGens.Remove(oldest);
                }
            }
        }

        var header = new Dictionary<string, object?>(10)
        {
            ["ok"] = true,
            ["window"] = window.ToJson(),
            ["generation"] = gen,
            ["nodes"] = output.Nodes.Count,
            ["interactive_nodes"] = interactiveCount,
            ["method"] = method,
            ["elapsed_ms"] = sw.ElapsedMilliseconds
        };
        if (output.Truncated)
        {
            header["truncated"] = new
            {
                nodes_omitted = output.NodesOmitted,
                hint = "use filter=, max_depth=, or snapshot a child window"
            };
        }
        if (IsThinTree(window, interactiveCount))
        {
            header["thin_tree"] = new
            {
                reason = $"{window.ClassName ?? window.ProcessName} exposed fewer than 10 interactive nodes",
                hint = "take windows_screenshot marks=true and act by mark; relaunching the app with --force-renderer-accessibility usually exposes a full tree"
            };
        }
        // A classic popup menu that this window does not own (Explorer's "Show more options", tray
        // menus) is a separate top-level window and never appears in this tree; point at it.
        var menus = _s.Session.ListTopLevelWindows(includeMinimized: false)
            .Where(w => w.ClassName == "#32768" && w.Hwnd != window.Hwnd
                        && Native.GetWindow(w.Hwnd, Native.GW_OWNER) != window.Hwnd)
            .Select(w => w.Handle).ToList();
        if (menus.Count > 0)
        {
            header["popup_menus"] = new
            {
                handles = menus,
                hint = "an open popup menu is its own window: windows_snapshot window=<handle> to get its items"
            };
        }

        string text;
        if (HasArgument(arguments, "diff_since"))
        {
            if (previous.Count > 0 && previousLines != null && previousLines.Count > 0)
            {
                var previousNodes = previous.Select(m =>
                    previousLines.TryGetValue(m.RuntimeKey.Length > 0 ? m.RuntimeKey : m.Ref, out var p)
                        ? p : PrevFromMeta(m)).ToList();
                var diff = SnapshotBuilder.Diff(previousNodes, output.Nodes, allowRemoved: !output.Truncated);
                text = diff.Lines.Count > 0 ? string.Join('\n', diff.Lines) : "(no changes since that snapshot)";
                var requested = GetIntArgument(arguments, "diff_since", 0);
                header["diff"] = new { since = previousGen, added = diff.Added, changed = diff.Changed, removed = diff.Removed };
                if (requested != previousGen)
                {
                    header["note"] = $"diffed against the latest unfiltered snapshot (generation {previousGen}), not {requested}";
                }
            }
            else
            {
                text = output.Text;
                header["diff"] = NullJson();
                header["note"] = "no earlier snapshot for this window; showing full tree";
            }
        }
        else
        {
            text = output.Text;
        }
        if (!isBaseline)
        {
            header["filtered"] = true;
        }
        if (output.Nodes.Count == 0 && !output.Truncated)
        {
            header["note"] = "no nodes matched the pruning rules; retry with interactive_only=false or visible_only=false";
        }

        return ScreenTextResult(header, text);
    }

    /// <summary>A JSON null that survives JsonIgnoreCondition.WhenWritingNull.</summary>
    private static object NullJson() => JsonSerializer.SerializeToElement((object?)null);

    private bool IsThinTree(WindowInfo window, int interactiveCount)
    {
        if (interactiveCount >= 10) return false;
        return _s.Config.IsElectronApp(window.ProcessName)
            || (window.ClassName?.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Minimal line rebuilt from a previous ElementMeta when its exact line text was not retained.</summary>
    private static PrevNode PrevFromMeta(ElementMeta m)
    {
        var id = m.AutomationId != null && m.AutomationId.Length <= 32 && m.AutomationId != m.Name ? " #" + m.AutomationId : "";
        var b = m.Bounds.IsEmpty ? "" : $" @{m.Bounds.X},{m.Bounds.Y},{m.Bounds.Width}x{m.Bounds.Height}";
        var name = m.Name == null ? "" : $" \"{SnapshotBuilder.EscapeName(m.Name.Length > 120 ? m.Name[..120] : m.Name)}\"";
        var pw = m.IsPassword ? " [password]" : "";
        return new PrevNode(m.RuntimeKey.Length > 0 ? m.RuntimeKey : m.Ref, $"- {m.Role}{name} [ref={m.Ref}]{id}{b}{pw}");
    }
}
