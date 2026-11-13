using System.Drawing;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>Toggle state as stored on the plain snapshot node (FlaUI-independent).</summary>
public enum SnapToggleState
{
    Off = 0,
    On = 1,
    Indeterminate = 2
}

/// <summary>Expand/collapse state as stored on the plain snapshot node (FlaUI-independent).</summary>
public enum SnapExpandState
{
    Collapsed = 0,
    Expanded = 1,
    PartiallyExpanded = 2,
    LeafNode = 3
}

/// <summary>
/// One node of a fetched UIA subtree in a plain, framework-independent form so the pruning,
/// formatting and diffing logic can run (and be unit-tested) without a live desktop.
/// </summary>
public sealed class SnapNode
{
    public required string Role { get; init; }
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public Rectangle Bounds { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Offscreen { get; init; }
    public bool Password { get; init; }
    public bool KeyboardFocusable { get; init; }
    public bool HasKeyboardFocus { get; init; }
    public string? ClassName { get; init; }
    public int ProcessId { get; init; }
    public string? LocalizedControlType { get; init; }

    public bool Invoke { get; init; }
    public bool Toggle { get; init; }
    public bool SelectionItem { get; init; }
    public bool ExpandCollapse { get; init; }
    public bool Value { get; init; }
    public bool RangeValue { get; init; }
    public bool Scroll { get; init; }
    public bool Text { get; init; }
    public bool Selection { get; init; }

    public SnapToggleState? ToggleStateV { get; init; }
    public string? ValueText { get; init; }
    public bool ReadOnly { get; init; }
    public bool Selected { get; init; }
    public SnapExpandState? ExpandState { get; init; }
    public double? RangeVal { get; init; }

    /// <summary>Live element for ref registration; null in unit-test models.</summary>
    public AutomationElement? Element { get; init; }

    /// <summary>UIA RuntimeId; refs are minted against it (see <see cref="ElementRegistry.RuntimeKey"/>).</summary>
    public int[]? RuntimeId { get; init; }

    public List<SnapNode> Children { get; } = new();
}

/// <summary>Knobs of a single snapshot emission.</summary>
public sealed record SnapshotOptions(
    bool InteractiveOnly = true,
    bool VisibleOnly = true,
    int MaxDepth = 14,
    string? Filter = null,
    int MaxNodes = 600,
    int MaxChars = 24000,
    bool IncludeBounds = true);

/// <summary>One emitted node: the source node, its kept depth, assigned ref and formatted line.</summary>
public sealed record Emitted(SnapNode Node, int Depth, string Ref, string Line);

/// <summary>Result of <see cref="SnapshotBuilder.Emit"/>: ordered lines plus truncation bookkeeping.</summary>
public sealed record SnapshotOutput(
    IReadOnlyList<Emitted> Nodes,
    bool Truncated,
    int NodesOmitted,
    int InteractiveNodes)
{
    public string Text => string.Join('\n', Nodes.Select(n => n.Line));
}

/// <summary>A node from the previous snapshot, keyed for diffing.</summary>
public sealed record PrevNode(string Key, string Line);

/// <summary>Outcome of <see cref="SnapshotBuilder.Diff"/>.</summary>
public sealed record DiffResult(IReadOnlyList<string> Lines, int Added, int Changed, int Removed);

/// <summary>
/// Builds agent-friendly accessibility snapshot lines from plain <see cref="SnapNode"/> trees:
/// pruning/flattening, filtering, line formatting, budgets and generation diffing.
/// All logic is static and testable without a desktop.
/// </summary>
public static class SnapshotBuilder
{
    /// <summary>Roles dropped outright in both modes; their children are flattened.</summary>
    public static readonly HashSet<string> DecorativeRoles = new()
    { "image", "separator", "scrollbar", "thumb", "titlebar" };

    /// <summary>Container roles kept only when named; unnamed ones are flattened.</summary>
    public static readonly HashSet<string> ContainerRoles = new()
    { "window", "group", "list", "tree", "table", "grid", "tablist", "menu", "menubar", "toolbar", "document", "custom" };

    /// <summary>Control set: these roles are interactive regardless of patterns.</summary>
    public static readonly HashSet<string> InteractiveRoles = new()
    {
        "button", "textbox", "checkbox", "radio", "combobox", "listitem", "menuitem",
        "tab", "treeitem", "link", "slider", "spinbutton", "row", "splitbutton"
    };

    /// <summary>Read-only state roles kept when named (readable state, not actionable).</summary>
    public static readonly HashSet<string> NamedStateRoles = new()
    { "text", "status", "header", "columnheader", "progressbar" };

    /// <summary>Pattern codes the compact suffix never repeats because the role implies them.</summary>
    private static readonly Dictionary<string, string[]> ImpliedPatterns = new()
    {
        ["button"] = new[] { "inv" },
        ["splitbutton"] = new[] { "inv" },
        ["checkbox"] = new[] { "tog" },
        ["radio"] = new[] { "tog", "sel" },
        ["combobox"] = new[] { "exp", "val" },
        ["listitem"] = new[] { "sel" },
        ["menuitem"] = new[] { "inv", "exp" },
        ["tab"] = new[] { "sel" },
        ["treeitem"] = new[] { "exp", "sel" },
        ["link"] = new[] { "inv" },
        ["textbox"] = new[] { "val" },
        ["slider"] = new[] { "rng" },
        ["spinbutton"] = new[] { "rng" }
    };

    /// <summary>Maps a UIA ControlType to the compact role name used in snapshot lines.</summary>
    public static string RoleName(ControlType? controlType)
    {
        if (controlType == null) return "element";
        return controlType switch
        {
            ControlType.Button => "button",
            ControlType.SplitButton => "splitbutton",
            ControlType.Edit => "textbox",
            ControlType.Text => "text",
            ControlType.CheckBox => "checkbox",
            ControlType.RadioButton => "radio",
            ControlType.ComboBox => "combobox",
            ControlType.List => "list",
            ControlType.ListItem => "listitem",
            ControlType.Menu => "menu",
            ControlType.MenuItem => "menuitem",
            ControlType.MenuBar => "menubar",
            ControlType.Tree => "tree",
            ControlType.TreeItem => "treeitem",
            ControlType.Tab => "tablist",
            ControlType.TabItem => "tab",
            ControlType.Table => "table",
            ControlType.DataItem => "row",
            ControlType.Header => "header",
            ControlType.HeaderItem => "columnheader",
            ControlType.Slider => "slider",
            ControlType.Spinner => "spinbutton",
            ControlType.ProgressBar => "progressbar",
            ControlType.Hyperlink => "link",
            ControlType.Image => "image",
            ControlType.Pane => "group",
            ControlType.Group => "group",
            ControlType.Window => "window",
            ControlType.Document => "document",
            ControlType.ToolBar => "toolbar",
            ControlType.ToolTip => "tooltip",
            ControlType.ScrollBar => "scrollbar",
            ControlType.StatusBar => "status",
            ControlType.Separator => "separator",
            ControlType.Thumb => "thumb",
            ControlType.TitleBar => "titlebar",
            ControlType.DataGrid => "grid",
            ControlType.Custom => "custom",
            _ => "element"
        };
    }

    /// <summary>The control set, used for <c>ElementMeta.Interactive</c> and the set-of-marks layer.</summary>
    public static bool IsInteractive(SnapNode n) =>
        InteractiveRoles.Contains(n.Role)
        || n.Invoke || n.Toggle || n.SelectionItem || n.ExpandCollapse
        || (n.Value && !n.ReadOnly) || n.RangeValue || n.Scroll
        || n.KeyboardFocusable;

    /// <summary>True when the node matches a case-insensitive substring/token query (null query = match all).</summary>
    public static bool Matches(SnapNode n, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        var name = n.Name ?? "";
        if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if ((n.AutomationId ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Role.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 1 && tokens.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Escapes a name or value for the snapshot line (upstream convention). Every line-break form becomes
    /// <c>\n</c>: rich-edit controls (Windows 11 Notepad) separate lines with a lone CR.
    /// </summary>
    public static string EscapeName(string name) => name
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n");

    /// <summary>Formats one snapshot line at the given kept depth.</summary>
    public static string FormatLine(SnapNode n, string refId, int depth, bool includeBounds)
    {
        var parts = new List<string>(10) { n.Role };
        if (!string.IsNullOrEmpty(n.Name))
        {
            parts.Add($"\"{EscapeName(Truncate(n.Name!, 120))}\"");
        }
        parts.Add($"[ref={refId}]");
        var id = n.AutomationId;
        if (!string.IsNullOrEmpty(id) && id.Length <= 32 && !string.Equals(id, n.Name, StringComparison.Ordinal))
        {
            parts.Add($"#{id}");
        }
        if (includeBounds && !n.Bounds.IsEmpty)
        {
            parts.Add($"@{n.Bounds.X},{n.Bounds.Y},{n.Bounds.Width}x{n.Bounds.Height}");
        }

        if (!n.Enabled) parts.Add("[disabled]");
        if (n.HasKeyboardFocus) parts.Add("[focused]");
        if (n.ToggleStateV is { } ts)
        {
            if (ts == SnapToggleState.On) parts.Add("[checked]");
            else if (n.Toggle && ts == SnapToggleState.Off) parts.Add("[unchecked]");
            else if (n.Toggle && ts == SnapToggleState.Indeterminate) parts.Add("[indeterminate]");
        }
        if (n.Selected) parts.Add("[selected]");
        if (n.ExpandState is { } es)
        {
            if (es == SnapExpandState.Expanded) parts.Add("[expanded]");
            else if (es == SnapExpandState.Collapsed) parts.Add("[collapsed]");
        }
        if (n.ReadOnly && n.Value) parts.Add("[readonly]");
        if (n.Password) parts.Add("[password]");
        if (n.Offscreen) parts.Add("[offscreen]");

        var value = n.ValueText;
        if (!string.IsNullOrEmpty(value) && !string.Equals(value.Trim(), n.Name?.Trim(), StringComparison.Ordinal))
        {
            parts.Add($"value=\"{EscapeName(Truncate(value, 80))}\"");
        }

        var suffix = PatternSuffix(n);
        if (suffix.Length > 0) parts.Add(suffix);

        return new StringBuilder().Append(' ', depth * 2).Append("- ").Append(string.Join(' ', parts)).ToString();
    }

    /// <summary>Compact pattern suffix, only for patterns the role does not already imply.</summary>
    public static string PatternSuffix(SnapNode n)
    {
        ImpliedPatterns.TryGetValue(n.Role, out var implied);
        var codes = new List<string>(7);
        void Add(string code, bool supported)
        {
            if (!supported || (implied != null && implied.Contains(code))) return;
            codes.Add(code);
        }
        Add("inv", n.Invoke);
        Add("tog", n.Toggle);
        Add("sel", n.SelectionItem);
        Add("exp", n.ExpandCollapse);
        Add("val", n.Value);
        Add("rng", n.RangeValue);
        Add("scr", n.Scroll);
        return codes.Count == 0 ? "" : "{" + string.Join(',', codes) + "}";
    }

    /// <summary>
    /// Collects, prunes and formats a snapshot. Trees are walked depth-first; nodes that fail the keep
    /// rules are flattened (their children visited at the same depth), so depth counts kept nodes only.
    /// Budgets (max nodes / max chars) cut emission deterministically and report the omitted count.
    /// </summary>
    public static SnapshotOutput Emit(IReadOnlyList<SnapNode> roots, SnapshotOptions options, Func<SnapNode, string> assignRef)
    {
        var candidates = new List<(SnapNode Node, int Depth)>();
        foreach (var root in roots)
        {
            Collect(root, 0, isRoot: true, options, candidates);
        }

        var emitted = new List<Emitted>(Math.Min(candidates.Count, options.MaxNodes));
        var chars = 0;
        var interactive = 0;
        var truncated = false;
        foreach (var (node, depth) in candidates)
        {
            if (emitted.Count >= options.MaxNodes) { truncated = true; break; }
            var refId = assignRef(node);
            var line = FormatLine(node, refId, depth, options.IncludeBounds);
            if (emitted.Count > 0 && chars + line.Length + 1 > options.MaxChars) { truncated = true; break; }
            chars += line.Length + 1;
            emitted.Add(new Emitted(node, depth, refId, line));
            if (IsInteractive(node)) interactive++;
        }

        return new SnapshotOutput(emitted, truncated, candidates.Count - emitted.Count, interactive);
    }

    /// <summary>True when the node matches the filter or any of its descendants does.</summary>
    private static bool HasMatch(SnapNode n, string? filter)
    {
        if (Matches(n, filter)) return true;
        foreach (var child in n.Children)
        {
            if (HasMatch(child, filter)) return true;
        }
        return false;
    }

    private static void Collect(SnapNode node, int depth, bool isRoot, SnapshotOptions options,
        List<(SnapNode, int)> candidates)
    {
        if (depth > options.MaxDepth) return;

        var filter = options.Filter;
        var hasMatch = filter != null && HasMatch(node, filter);
        if (filter != null && !hasMatch) return; // nothing of interest in this subtree

        var named = !string.IsNullOrWhiteSpace(node.Name);
        var emit = isRoot
            || (hasMatch && Matches(node, filter))
            || (KeepByRules(node, named, options) && (filter == null || hasMatch));

        if (emit && !(options.VisibleOnly && (node.Offscreen || node.Bounds.IsEmpty)))
        {
            // Layout-only or offscreen ancestors do not hide visible descendants (e.g. WinUI flyouts).
            candidates.Add((node, depth));
            depth++;
        }

        foreach (var child in node.Children)
        {
            Collect(child, depth, isRoot: false, options, candidates);
        }
    }

    private static bool KeepByRules(SnapNode node, bool named, SnapshotOptions options)
    {
        if (DecorativeRoles.Contains(node.Role)) return false; // always flattened
        if (!options.InteractiveOnly) return true; // all control-view nodes except decorative
        if (IsInteractive(node)) return true;
        if (named && NamedStateRoles.Contains(node.Role)) return true;
        if (named && ContainerRoles.Contains(node.Role)) return true;
        return false;
    }

    /// <summary>
    /// Diffs the current emission against the previous snapshot: <c>+</c> added, <c>~</c> changed,
    /// <c>-</c> removed (last known line). Removed lines are suppressed when the current snapshot was
    /// truncated: absence from a truncated tree is not removal.
    /// </summary>
    public static DiffResult Diff(IReadOnlyList<PrevNode> previous, IReadOnlyList<Emitted> current, bool allowRemoved)
    {
        var prevByKey = new Dictionary<string, PrevNode>(StringComparer.Ordinal);
        foreach (var p in previous) prevByKey[p.Key] = p;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        var added = 0;
        var changed = 0;
        foreach (var e in current)
        {
            var key = NodeKey(e.Node, e.Ref);
            seen.Add(key);
            if (prevByKey.TryGetValue(key, out var prev))
            {
                if (!string.Equals(prev.Line, e.Line, StringComparison.Ordinal))
                {
                    lines.Add("~ " + e.Line);
                    changed++;
                }
            }
            else
            {
                lines.Add("+ " + e.Line);
                added++;
            }
        }

        var removed = 0;
        if (allowRemoved)
        {
            foreach (var p in previous)
            {
                if (!seen.Contains(p.Key))
                {
                    lines.Add("- " + p.Line);
                    removed++;
                }
            }
        }
        return new DiffResult(lines, added, changed, removed);
    }

    /// <summary>Diff key of a node: RuntimeId when available, else its ref.</summary>
    public static string NodeKey(SnapNode node, string refId)
    {
        var key = ElementRegistry.RuntimeKey(node.RuntimeId);
        return key.Length > 0 ? key : refId;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
