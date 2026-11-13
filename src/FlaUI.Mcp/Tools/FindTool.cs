using System.Text.Json;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>Searches a window's UIA tree for nodes matching a query/role and returns refs for them.</summary>
public sealed class FindTool : ToolBase
{
    private readonly Services _s;

    public FindTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_find";
    public override ToolKind Kind => ToolKind.Perception;

    public override string Description =>
        "Finds nodes in a window's accessibility tree by query (case-insensitive substring of name, " +
        "automation id or role; multi-token queries match when every token appears in the name) and/or by " +
        "role (button, textbox, listitem, ...). Returns the same line format as windows_snapshot with refs, " +
        "best matches first (exact name, then starts-with, then contains), max 20 by default.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new { type = "string", description = "Window handle (wN), title substring, process name or hwnd; default = foreground window" },
            query = new { type = "string", description = "Text to find; required unless role= is given" },
            role = new { type = "string", description = "Role name filter, e.g. \"button\"" },
            max_results = new { type = "integer", description = "Maximum matches returned (default 20)" },
            interactive_only = new { type = "boolean", description = "Only actionable controls (default false)" }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
        => Task.FromResult(Run(arguments, ctx));

    private McpToolResult Run(JsonElement? arguments, ToolContext ctx)
    {
        var query = GetStringArgument(arguments, "query");
        var roleArg = GetStringArgument(arguments, "role");
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(roleArg))
        {
            throw new ToolException("bad_request", "No query or role given.",
                "Pass query= (text to find in name/automation id/role) or role= (e.g. \"button\").");
        }

        var window = _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));
        ctx.Gate.EnsureView(window);
        ctx.TargetApp = window.ProcessName;
        if (window.Minimized)
        {
            throw new ToolException("window_minimized", $"{window.Handle} ('{window.Title}') is minimized.",
                "Restore the window first (windows_launch with the same app, or ask the user), then call windows_find again.");
        }

        var (root, _) = TreeFetch.Fetch(_s, window, ctx);
        var interactiveOnly = GetBoolArgument(arguments, "interactive_only", false);
        var maxResults = Math.Clamp(GetIntArgument(arguments, "max_results", 20), 1, 100);

        var candidates = new List<SnapNode>();
        Collect(root, visibleOnly: true, candidates);
        var matches = new List<(SnapNode Node, int Rank)>();
        foreach (var node in candidates)
        {
            if (roleArg != null && !string.Equals(node.Role, roleArg.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!SnapshotBuilder.Matches(node, query)) continue;
            if (interactiveOnly && !SnapshotBuilder.IsInteractive(node)) continue;
            matches.Add((node, Rank(node, query)));
        }

        var gen = _s.Elements.CurrentGeneration(window.Handle);
        if (gen == 0) gen = _s.Elements.BeginGeneration(window.Handle);

        var lines = new List<string>();
        foreach (var (node, _, _) in matches
            .Select((m, i) => (m.Node, m.Rank, i))
            .OrderBy(x => x.Rank).ThenBy(x => x.i)
            .Take(maxResults))
        {
            var refId = TreeFetch.Register(_s, window, gen, node);
            lines.Add(SnapshotBuilder.FormatLine(node, refId, 0, includeBounds: true));
        }

        var header = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["window"] = window.ToJson(),
            ["generation"] = gen,
            ["matches"] = lines.Count
        };
        if (lines.Count == 0)
        {
            header["note"] = "no matches; call windows_snapshot to see the full tree";
        }

        return ScreenTextResult(header, lines.Count > 0 ? string.Join('\n', lines) : "(no matches)");
    }

    private static int Rank(SnapNode node, string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || node.Name == null) return 2;
        var q = query.Trim();
        if (string.Equals(node.Name, q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (node.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    /// <summary>Traversal-order collection with the visible-only rule (offscreen nodes with bounds are skipped, children visited).</summary>
    private static void Collect(SnapNode node, bool visibleOnly, List<SnapNode> into)
    {
        if (visibleOnly && node.Bounds.IsEmpty) return;
        var emit = !(visibleOnly && node.Offscreen);
        if (emit) into.Add(node);
        foreach (var child in node.Children)
        {
            Collect(child, visibleOnly, into);
        }
    }
}
