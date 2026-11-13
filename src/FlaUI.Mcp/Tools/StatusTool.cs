using System.Text.Json;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>Server status: mode, grants, control lock, user activity, monitors, last image. Never gated.</summary>
public sealed class StatusTool : ToolBase
{
    private readonly Services _s;

    public StatusTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_status";
    public override ToolKind Kind => ToolKind.Control;

    public override string Description =>
        "Server status: yolo mode, kill-switch hotkey, current grants, whether the agent holds the control lock, " +
        "whether the user is active, monitors (physical bounds, DPI scale) and the id of the latest image. Call it when a result is confusing.";

    public override object InputSchema => new { type = "object", properties = new { } };

    public override McpToolAnnotations? Annotations => new() { ReadOnlyHint = true, DestructiveHint = false };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var last = _s.Frames.Last;
        var (marksWindow, marksImage, marks) = _s.Elements.CurrentMarks();
        return Task.FromResult(JsonResult(new
        {
            ok = true,
            server = new
            {
                version = new McpServerInfo().Version,
                yolo = _s.Config.Yolo,
                killSwitchHotkey = _s.Config.KillSwitchHotkey,
                prompts = _s.Config.Prompts,
                overlay = _s.Config.Overlay,
                idleResumeSeconds = _s.Config.IdleResumeSeconds,
                maxScreenshotEdge = _s.Config.MaxScreenshotEdge,
                auditDir = _s.Audit.Enabled ? _s.Audit.Directory : null,
                harnessPids = _s.Gate.Policy.HarnessPids
            },
            state = _s.State.ToJson(),
            monitors = Monitors.All().Select(m => m.ToJson()).ToList(),
            lastImage = last?.Mapping,
            marks = marks.Count == 0 ? null : new { window = marksWindow, image_id = marksImage, count = marks.Count }
        }));
    }
}
