using System.Text.Json;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>Lists top-level windows and monitors. Perception but not gated: the model needs these
/// short identifiers before it can request access to anything.</summary>
public sealed class ListWindowsTool : ToolBase
{
    private readonly Services _s;

    public ListWindowsTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_list_windows";
    public override ToolKind Kind => ToolKind.Perception;

    public override string Description =>
        "Lists top-level windows front to back: handle (wN), title, process, pid, physical-pixel bounds, " +
        "minimized/focused flags and whether the app is an Electron/Chromium host, plus every monitor " +
        "(physical bounds, work area, DPI scale). Also lists the taskbar as \"(taskbar)\" and any open " +
        "classic popup menu as \"(popup menu)\"; snapshot those handles like any window. Not access-gated. " +
        "Call it first to find the window you want.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            include_minimized = new { type = "boolean", description = "Include minimized windows (default true)" }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
        => Task.FromResult(Run(arguments, ctx));

    private McpToolResult Run(JsonElement? arguments, ToolContext ctx)
    {
        var includeMinimized = GetBoolArgument(arguments, "include_minimized", true);
        var windows = _s.Session.ListTopLevelWindows(includeMinimized: includeMinimized);
        var foreground = _s.Session.ForegroundWindow();

        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            // JSON null that survives DefaultIgnoreCondition.WhenWritingNull.
            ["foreground"] = foreground == null ? JsonSerializer.SerializeToElement((object?)null) : foreground.Handle,
            ["windows"] = windows.Select(w => ToJson(w, IsElectron(w))).ToList(),
            ["monitors"] = Monitors.All().Select(m => m.ToJson()).ToList()
        };
        return JsonResult(payload);
    }

    private bool IsElectron(WindowInfo w) =>
        _s.Config.IsElectronApp(w.ProcessName)
        || (w.ClassName?.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>WindowInfo.ToJson() plus the electron flag (no control over the record's own shape, so clone).</summary>
    private static object ToJson(WindowInfo w, bool electron)
    {
        var element = JsonSerializer.SerializeToElement(w.ToJson(), McpProtocol.JsonOptions);
        var dict = new Dictionary<string, object?>
        {
            ["electron"] = electron
        };
        foreach (var p in element.EnumerateObject())
        {
            dict[p.Name] = p.Value;
        }
        return dict;
    }
}
