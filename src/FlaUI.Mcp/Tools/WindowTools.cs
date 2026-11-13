using System.Text.Json;
using System.Diagnostics;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Launches an app (path, name or AUMID with '!') and returns its window. The app is started even when
/// control is not granted; without control the result is access_denied with the request-access next step.
/// </summary>
public sealed class LaunchTool : ToolBase
{
    private readonly Services _s;

    public LaunchTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_launch";
    public override ToolKind Kind => ToolKind.Action;

    public override string Description =>
        "Launches an application: a path (\"notepad.exe\" or C:\\...\\app.exe), a name resolved through the shell, " +
        "or a Store-app AUMID containing '!' (e.g. Microsoft.WindowsCalculator_8wekyb3d8bbwe!App). Options: args " +
        "(string array), timeout_ms (default 15000). Returns the new window { handle, hwnd, title, process, ... }. " +
        "Note: the app is launched even when this server has no control grant for it — in that case you get " +
        "access_denied with the window in the message; call windows_request_access (tier=control) and continue " +
        "with the handle from the message.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            app = new { type = "string", description = "Path, executable name or AUMID (contains '!')" },
            args = new { type = "array", items = new { type = "string" }, description = "Command-line arguments" },
            timeout_ms = new { type = "integer", description = "How long to wait for a new window (default 15000)" }
        },
        required = new[] { "app" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var app = GetStringArgument(arguments, "app") ?? throw new ToolException("bad_request", "app is required.",
            "Pass an executable path, name (\"notepad.exe\") or Store-app AUMID containing '!'.");

        var args = GetArgument<string[]?>(arguments, "args") ?? Array.Empty<string>();
        var timeoutMs = ActionArgs.IntInRange(arguments, "timeout_ms", 15000, 1000, 60000, "a launch wait of 1000..60000 ms");

        ctx.ThrowIfCancelled();
        var launched = await Task.Run(() => _s.Session.LaunchApp(app, args, timeoutMs), ctx.Cancel);
        var window = launched.Window;
        ctx.ThrowIfCancelled();

        try
        {
            await ctx.Gate.EnsureControlAsync(window, ctx);
        }
        catch (ToolException ex) when (ex.Code is "access_denied" or "blocked_app")
        {
            throw new ToolException(ex.Code,
                $"{window.ProcessName} was launched (window {window.Handle}) but is not permitted for automation: {ex.Message}",
                ex.Next);
        }

        return JsonResult(new
        {
            ok = true,
            window = window.ToJson(),
            new_window = launched.NewWindow,
            note = launched.NewWindow ? null : "no new window appeared; the app activated an existing window (single-instance app or protocol handler)"
        });
    }
}

/// <summary>Brings a window to the foreground (restores it when minimized) with foreground verification.</summary>
public sealed class FocusTool : ToolBase
{
    private readonly Services _s;

    public FocusTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_focus";
    public override ToolKind Kind => ToolKind.Action;

    public override string Description =>
        "Brings a window to the foreground (restores it first when minimized). window is a handle (\"w3\"), hwnd, " +
        "\"pid:N\", title substring or process name (default foreground). Returns { ok, window, foreground } — " +
        "foreground=false fails with focus_lost. Call this before windows_type/windows_key when the target may " +
        "not be in front.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new { type = "string", description = "Window handle (\"w3\"), hwnd, pid:N, title substring or process name (default foreground)" }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var window = _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));
        await ctx.Gate.EnsureControlAsync(window, ctx);
        ctx.ThrowIfCancelled();

        if (!WindowActivation.BringToForeground(window))
        {
            throw new ToolException("focus_lost",
                $"Could not bring {window.Handle} (\"{window.Title}\") to the foreground.",
                "Retry once; if it still fails, ask the user to click the window, then windows_snapshot it to continue.");
        }

        return JsonResult(new { ok = true, window = window.ToJson(), foreground = true });
    }
}

/// <summary>Sends WM_CLOSE and waits (up to 3 s) for the window to disappear; a save prompt may appear instead.</summary>
public sealed class CloseTool : ToolBase
{
    private readonly Services _s;

    public CloseTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_close";
    public override ToolKind Kind => ToolKind.Action;

    public override string Description =>
        "Closes a window politely: sends WM_CLOSE (like clicking X) and waits up to 3 s for it to disappear. " +
        "Unsaved changes may show a save prompt instead — snapshot the window afterwards to check. Returns " +
        "{ ok, closed, note }.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new { type = "string", description = "Window handle (\"w3\"), hwnd, pid:N, title substring or process name (default foreground)" }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var window = _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));
        await ctx.Gate.EnsureControlAsync(window, ctx);
        ctx.ThrowIfCancelled();

        var hwnd = window.Hwnd;
        Native.PostMessageW(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        var sw = Stopwatch.StartNew();
        var closed = false;
        while (sw.ElapsedMilliseconds < 3000)
        {
            if (!Native.IsWindow(hwnd))
            {
                closed = true;
                break;
            }
            await Task.Delay(100, ctx.Cancel);
            ctx.ThrowIfCancelled();
        }

        return JsonResult(new
        {
            ok = true,
            window = window.Handle,
            closed,
            note = closed
                ? "Closed. If the app had unsaved work a save prompt may still exist — windows_snapshot to check."
                : "Still open after WM_CLOSE; it may be busy or asking something — windows_snapshot the window."
        });
    }
}
