using PlaywrightWindows.Mcp.Safety;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>Everything a tool may need, built once in Program and handed to each tool constructor.</summary>
public sealed record Services(
    ServerConfig Config,
    SessionManager Session,
    ElementRegistry Elements,
    FrameStore Frames,
    SafetyGate Gate,
    UiThread Ui,
    AuditLog Audit)
{
    public SafetyState State => Gate.State;

    /// <summary>Window that hosts a registered element (by its cached top-level HWND).</summary>
    public WindowInfo WindowOf(ElementMeta meta)
    {
        var info = Session.TryGetWindowInfo(meta.WindowHwnd);
        if (info != null) return info;
        throw new ToolException("window_not_found", $"The window that contained {meta.Ref} is gone.",
            "Call windows_list_windows, then windows_snapshot the window you need.");
    }
}
