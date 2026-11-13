using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Reads text: either from one element by ref (Value, Selection, LegacyIAccessible, Name, Text pattern)
/// or the concatenated readable text of a whole window (Name/Value of text-like nodes in reading order).
/// </summary>
public sealed class GetTextTool : ToolBase
{
    private readonly Services _s;

    public GetTextTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_get_text";
    public override ToolKind Kind => ToolKind.Perception;

    public override string Description =>
        "Reads text from the screen tree. With ref= the value/text of that element (Value pattern, " +
        "selection, legacy value, name, then document range). With window= the readable text of the whole " +
        "window (names and values of text-like nodes in reading order), capped at 20000 chars by default.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new { type = "string", description = "Element ref from windows_snapshot/windows_find (e.g. \"w1e5\")" },
            window = new { type = "string", description = "Window handle (wN), title substring, process name or hwnd; whole-window text; default = foreground" },
            max_chars = new { type = "integer", description = "Output cap in characters (default 20000)" }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
        => Task.FromResult(Run(arguments, ctx));

    private McpToolResult Run(JsonElement? arguments, ToolContext ctx)
    {
        var refId = GetStringArgument(arguments, "ref");
        var maxChars = Math.Clamp(GetIntArgument(arguments, "max_chars", 20000), 100, 200000);

        if (!string.IsNullOrEmpty(refId))
        {
            var resolved = _s.Elements.Resolve(refId);
            var window = _s.WindowOf(resolved.Meta);
            ctx.Gate.EnsureView(window);
            ctx.TargetApp = window.ProcessName;
            ctx.Gate.EnsureNotPasswordField(resolved.Meta.IsPassword, $"ref {refId} in '{window.Title}'");

            var text = ReadElementText(resolved.Element);
            var truncated = text.Length > maxChars;
            if (truncated) text = text[..maxChars];
            var header = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["ref"] = refId,
                ["chars"] = text.Length,
                ["truncated"] = truncated
            };
            return ScreenTextResult(header, text);
        }

        var win = _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));
        ctx.Gate.EnsureView(win);
        ctx.TargetApp = win.ProcessName;
        if (win.Minimized)
        {
            throw new ToolException("window_minimized", $"{win.Handle} ('{win.Title}') is minimized.",
                "Restore the window first (windows_launch with the same app, or ask the user), then call windows_get_text again.");
        }

        var (root, _) = TreeFetch.Fetch(_s, win, ctx);
        var sb = new StringBuilder();
        var cut = false;
        AppendWindowText(root, sb, maxChars, ref cut);
        var windowTruncated = cut || sb.Length > maxChars;
        if (sb.Length > maxChars) sb.Length = maxChars;

        var winHeader = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["window"] = win.ToJson(),
            ["chars"] = sb.Length,
            ["truncated"] = windowTruncated
        };
        return ScreenTextResult(winHeader, sb.ToString());
    }

    private static void AppendWindowText(SnapNode node, StringBuilder sb, int maxChars, ref bool cut)
    {
        if (cut || sb.Length >= maxChars) return;
        if (node.Bounds.IsEmpty) return; // invisible subtree
        if (!node.Password)
        {
            if (SnapshotBuilder.NamedStateRoles.Contains(node.Role) || node.Role == "document")
            {
                Append(sb, node.Name, maxChars, ref cut);
            }
            if (!string.IsNullOrEmpty(node.ValueText) && !string.Equals(node.ValueText.Trim(), node.Name?.Trim(), StringComparison.Ordinal))
            {
                Append(sb, node.ValueText, maxChars, ref cut);
            }
        }
        foreach (var child in node.Children)
        {
            if (cut || sb.Length >= maxChars) return;
            AppendWindowText(child, sb, maxChars, ref cut);
        }
    }

    private static void Append(StringBuilder sb, string? text, int maxChars, ref bool cut)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (sb.Length > 0) sb.Append('\n');
        if (sb.Length + text.Length > maxChars)
        {
            sb.Append(text.AsSpan(0, Math.Max(0, maxChars - sb.Length)));
            cut = true;
        }
        else
        {
            sb.Append(text);
        }
    }

    /// <summary>Upstream resolution order: Value, first selected item, LegacyIAccessible value, Name, document range.</summary>
    private static string ReadElementText(AutomationElement element)
    {
        string? text = null;
        try
        {
            if (element.Patterns.Value.IsSupported) text = element.Patterns.Value.Pattern.Value.ValueOrDefault;
        }
        catch { /* pattern unreadable */ }

        if (string.IsNullOrEmpty(text))
        {
            try
            {
                if (element.Patterns.Selection.IsSupported)
                {
                    var selected = element.Patterns.Selection.Pattern.Selection.ValueOrDefault;
                    if (selected is { Length: > 0 }) text = selected[0].Properties.Name.ValueOrDefault;
                }
            }
            catch { /* pattern unreadable */ }
        }

        if (string.IsNullOrEmpty(text))
        {
            try
            {
                if (element.Patterns.LegacyIAccessible.IsSupported) text = element.Patterns.LegacyIAccessible.Pattern.Value.ValueOrDefault;
            }
            catch { /* pattern unreadable */ }
        }

        if (string.IsNullOrEmpty(text))
        {
            try { text = element.Properties.Name.ValueOrDefault; }
            catch { /* unreadable */ }
        }

        if (string.IsNullOrEmpty(text))
        {
            try
            {
                if (element.Patterns.Text.IsSupported) text = element.Patterns.Text.Pattern.DocumentRange.GetText(-1);
            }
            catch { /* pattern unreadable */ }
        }

        return text ?? "";
    }
}
