using System.Diagnostics;
using System.Text.Json;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Game mode: an alternate control path that only applies to windows matching a <c>gameProfiles</c> entry.
/// Launchers, dialogs and every other application keep the UI Automation tools; once the game window exists
/// the model switches to windows_game_input + windows_screenshot, and back again when the game ends.
/// </summary>
public static class GameTools
{
    public static void Register(ToolRegistry registry, Services services)
    {
        var tracker = new GameProcessTracker();
        registry.RegisterTool(new GameStatusTool(services, tracker));
        registry.RegisterTool(new GameInputTool(services, tracker));
    }

    /// <summary>Open game windows, front to back, with the profile each one matched.</summary>
    internal static List<(WindowInfo Window, GameProfile Profile)> OpenGames(Services s)
    {
        var result = new List<(WindowInfo, GameProfile)>();
        if (s.Config.GameProfiles.Count == 0) return result;
        foreach (var window in s.Session.ListTopLevelWindows())
        {
            var profile = s.Config.MatchGame(window);
            if (profile != null) result.Add((window, profile));
        }
        return result;
    }

    internal const string FixProfile =
        "If one of these is the game, do NOT drive it with windows_click/windows_key: fix gameProfiles in config.json " +
        "(set windowClass/title to match the actual values shown, or add a profile for them), restart the MCP server " +
        "(config is read at startup only), then call windows_game_status again. If none is the game, ignore them.";

    /// <summary>
    /// Open windows of a profiled process that no profile accepts, with the constraints that failed.
    /// Informational only: nothing here is a game window, so game input and game capture still refuse them.
    /// </summary>
    internal static List<object> Candidates(Services s)
    {
        var result = new List<object>();
        if (s.Config.GameProfiles.Count == 0) return result;
        foreach (var window in s.Session.ListTopLevelWindows())
        {
            var misses = s.Config.NearMisses(window.ProcessName, window.Title, window.ClassName);
            if (misses.Count > 0) result.Add(DescribeCandidate(window, misses));
        }
        return result;
    }

    internal static object DescribeCandidate(WindowInfo window, List<(GameProfile Profile, IReadOnlyList<string> Failed)> misses) => new
    {
        handle = window.Handle,
        title = window.Title,
        process = window.ProcessName,
        pid = window.Pid,
        windowClass = window.ClassName,
        foreground = WindowActivation.IsForeground(window),
        profiles = misses.Select(m => new { profile = m.Profile.DisplayName, failed = m.Failed })
    };

    internal static object Describe(WindowInfo window, GameProfile profile)
    {
        var client = GameCapture.ClientRectOnScreen(window.Hwnd);
        return new
        {
            handle = window.Handle,
            title = window.Title,
            process = window.ProcessName,
            pid = window.Pid,
            profile = profile.DisplayName,
            foreground = WindowActivation.IsForeground(window),
            minimized = window.Minimized,
            maximized = Native.IsZoomed(window.Hwnd),
            responding = !Native.IsHungAppWindow(window.Hwnd),
            client = client == null ? null : new { x = client.Value.X, y = client.Value.Y, width = client.Value.Width, height = client.Value.Height },
            capture = profile.Capture
        };
    }
}

/// <summary>
/// windows_game_status: which game windows are open, whether they respond, and how previously seen game
/// processes ended (exit code). Ungated like windows_list_windows — it is how the model decides which
/// control path applies right now.
/// </summary>
public sealed class GameStatusTool : ToolBase
{
    private readonly Services _s;
    private readonly GameProcessTracker _tracker;

    public GameStatusTool(Services services, GameProcessTracker tracker)
    {
        _s = services;
        _tracker = tracker;
    }

    public override string Name => "windows_game_status";
    public override ToolKind Kind => ToolKind.Control;

    public override string Description =>
        "Game mode status. Call it after launching a game and before any gameplay input. games lists open game " +
        "windows (windows matching a gameProfiles entry in config.json): handle, pid, foreground, maximized, " +
        "responding (false = hung), client size. mode is \"game\" when a game window is in front (use " +
        "windows_game_input + windows_screenshot) and \"desktop\" otherwise. detection is \"matched\", \"none\" (no " +
        "window of any profiled process is open) or \"candidate_unmatched\": candidates lists open windows whose " +
        "process matches a profile but whose title/windowClass constraint failed, with the actual Win32 windowClass " +
        "and the failed conditions — the game is probably running under a profile that does not fit it; fix the " +
        "profile and restart the server rather than playing through desktop input. Candidates are not game windows. " +
        "processes reports every game process seen this session with running and, once it has ended, exitCode. " +
        "held lists keys/buttons the game path is still holding down.";

    public override object InputSchema => new { type = "object", properties = new { } };

    public override McpToolAnnotations? Annotations => new() { ReadOnlyHint = true, DestructiveHint = false };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var profiles = _s.Config.GameProfiles;
        if (profiles.Count == 0)
        {
            return Task.FromResult(JsonResult(new
            {
                ok = true,
                mode = "desktop",
                configured = false,
                note = "No gameProfiles are configured, so game mode is off. Add an entry to config.json, e.g. " +
                       "{\"name\":\"minecraft\",\"process\":\"javaw.exe\",\"windowClass\":\"^GLFW30$\"} (use the game " +
                       "window's actual Win32 class; it varies by build), then restart the MCP server."
            }));
        }

        var games = GameTools.OpenGames(_s);
        foreach (var (window, profile) in games)
        {
            _tracker.Observe(window, profile);
        }
        var candidates = GameTools.Candidates(_s);
        var front = games.FirstOrDefault(g => WindowActivation.IsForeground(g.Window));
        var (keys, buttons) = GameInput.Held();
        var mode = front.Window != null ? "game" : "desktop";
        var detection = games.Count > 0 ? "matched" : candidates.Count > 0 ? "candidate_unmatched" : "none";
        var next = games.Count == 0
            ? candidates.Count > 0
                ? "No window matches a game profile, but candidates lists open windows of a profiled process that failed a title/windowClass constraint. " + GameTools.FixProfile
                : "No window of any profiled game process is open: use the normal windows_* tools (launcher, files), then call windows_game_status again."
            : front.Window != null
                ? "A game window is in front: drive it with windows_game_input and look with windows_screenshot window=<handle>."
                : "A game window exists but is not in front: windows_focus it (state=\"maximized\" to maximize), then use windows_game_input.";

        return Task.FromResult(JsonResult(new
        {
            ok = true,
            mode,
            detection,
            configured = true,
            profiles = profiles.Select(p => new { name = p.DisplayName, process = p.Process, title = p.Title, windowClass = p.WindowClass, usable = p.Validate() == null }),
            games = games.Select(g => GameTools.Describe(g.Window, g.Profile)),
            candidates,
            processes = _tracker.Report(),
            held = new { keys, buttons },
            next
        }));
    }
}

/// <summary>
/// windows_game_input: a timed sequence of hardware-level input events (scan-code keys, relative mouse
/// motion, buttons, wheel, Unicode text) sent to a game window. Refuses any window that is not a game window,
/// so it can never replace the UIA path for ordinary applications. Uses the same gates as every other action:
/// control grant, user-idle wait, foreground check before every event, kill switch, held-input cleanup.
/// </summary>
public sealed class GameInputTool : ToolBase
{
    private readonly Services _s;
    private readonly GameProcessTracker _tracker;

    public GameInputTool(Services services, GameProcessTracker tracker)
    {
        _s = services;
        _tracker = tracker;
    }

    public override string Name => "windows_game_input";
    public override ToolKind Kind => ToolKind.Action;
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(50);

    public override string Description =>
        "Game mode input: runs a timed sequence of hardware-level events against a GAME window (one matching " +
        "gameProfiles in config.json; any other window is refused with not_game_window — use windows_click/" +
        "windows_key there). events is an array of {\"do\": ...}: key_down / key_up {key}; key {key, hold_ms=50} " +
        "(tap); mouse_move {dx, dy, ms=0} (RELATIVE mouse counts — turns a mouse-look camera; +dx right, +dy down); " +
        "mouse_to {x, y, image_id?} (absolute, pixels of the latest screenshot — for menus and inventories where " +
        "the cursor is visible); button_down / button_up {button}; click {button=left, hold_ms=50}; scroll " +
        "{amount} (wheel notches, + = up); text {text} (types into a focused in-game text box); wait {ms}. Keys " +
        "are physical positions: a-z, 0-9, f1-f12, space, enter, esc, tab, shift, ctrl, alt, arrows, numpad0-9. " +
        "Up to 200 events / 30 s per call. Everything still held at the end is released unless keep_held=true " +
        "(then it stays down across calls, at most 60 s and only while the game stays in front). release_all=true " +
        "releases held input and does nothing else. screenshot=true appends a screenshot of the game taken after " +
        "the sequence. If the game window disappears mid-sequence the result has status game_exited — call " +
        "windows_game_status for the exit code.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new { type = "string", description = "Game window: handle (\"w3\"), pid:N, title substring or process name. Default: the frontmost open game window." },
            events = new
            {
                type = "array",
                description = "Sequence of input events, run in order with their own timing.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        @do = new { type = "string", @enum = new[] { "key_down", "key_up", "key", "mouse_move", "mouse_to", "button_down", "button_up", "click", "scroll", "text", "wait" } },
                        key = new { type = "string", description = "Physical key name, e.g. w, space, esc, shift, f3, up" },
                        button = new { type = "string", @enum = new[] { "left", "right", "middle" } },
                        dx = new { type = "integer", description = "mouse_move: relative counts, + = right" },
                        dy = new { type = "integer", description = "mouse_move: relative counts, + = down" },
                        x = new { type = "number", description = "mouse_to: image pixel x" },
                        y = new { type = "number", description = "mouse_to: image pixel y" },
                        image_id = new { type = "string", description = "mouse_to: frame the x,y refer to (default latest image)" },
                        ms = new { type = "integer", description = "wait: delay; mouse_move: spread the motion over this long" },
                        hold_ms = new { type = "integer", description = "key/click: how long it is held (default 50)" },
                        amount = new { type = "integer", description = "scroll: wheel notches, + = up" },
                        text = new { type = "string", description = "text: characters to type" }
                    },
                    required = new[] { "do" }
                }
            },
            keep_held = new { type = "boolean", description = "Leave keys/buttons that are still down at the end held (default false)" },
            release_all = new { type = "boolean", description = "Release everything the game path holds and return (no events needed)" },
            screenshot = new { type = "boolean", description = "Append a screenshot of the game after the sequence (default false)" }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        if (GetBoolArgument(arguments, "release_all"))
        {
            var before = GameInput.Held();
            GameInput.ReleaseAll();
            return JsonResult(new { ok = true, released = new { keys = before.Keys, buttons = before.Buttons } });
        }

        var (window, profile) = ResolveGame(GetStringArgument(arguments, "window"));
        _tracker.Observe(window, profile);
        var events = GameSequence.Parse(arguments);
        var keepHeld = GetBoolArgument(arguments, "keep_held");
        var wantScreenshot = GetBoolArgument(arguments, "screenshot");

        await ctx.Gate.EnsureControlAsync(window, ctx);
        ctx.ThrowIfCancelled();
        if (!WindowActivation.IsForeground(window))
        {
            WindowActivation.BringToForeground(window);
        }
        ctx.Gate.EnsureForeground(window);
        GameInput.Guard(window.Hwnd);

        var sw = Stopwatch.StartNew();
        var executed = 0;
        var completed = false;
        var exited = false;
        try
        {
            foreach (var ev in events)
            {
                ctx.ThrowIfCancelled();
                if (!Native.IsWindow(window.Hwnd))
                {
                    exited = true;
                    break;
                }
                if (profile.IgnoreMouseMoveYield)
                {
                    GameInput.SuppressMouseMoveYield(ev.PlannedMs + 750);
                }
                ctx.Gate.ThrowIfUserActive("the game input sequence", ctx);
                ctx.Gate.EnsureForeground(window);
                await RunAsync(ev, window, ctx);
                executed++;
            }
            completed = !exited;
        }
        finally
        {
            // Anything but a clean, complete run with keep_held releases every key and button.
            if (!completed || !keepHeld)
            {
                GameInput.ReleaseAll();
            }
        }

        var (keys, buttons) = GameInput.Held();
        if (exited)
        {
            throw new ToolException("game_exited",
                $"The game window {window.Handle} disappeared after {executed} of {events.Count} events; held input was released.",
                "Call windows_game_status for the exit code, then continue with the normal windows_* tools (launcher, files).")
            {
                IsError = false,
                Payload = new { executed, total = events.Count, pid = window.Pid, profile = profile.DisplayName }
            };
        }

        var result = JsonResult(new
        {
            ok = true,
            method = "game_input",
            window = window.Handle,
            profile = profile.DisplayName,
            executed,
            duration_ms = (int)sw.ElapsedMilliseconds,
            held = new { keys, buttons }
        });

        if (wantScreenshot && ctx.CallTool != null)
        {
            // Let the last input reach a frame before looking.
            await Task.Delay(120, ctx.Cancel);
            var shot = await ctx.CallTool("windows_screenshot",
                JsonSerializer.SerializeToElement(new { window = window.Handle }, McpProtocol.JsonOptions));
            result.Content.AddRange(shot.Content);
        }
        return result;
    }

    private (WindowInfo Window, GameProfile Profile) ResolveGame(string? spec)
    {
        if (_s.Config.GameProfiles.Count == 0)
        {
            throw new ToolException("game_mode_off", "No gameProfiles are configured, so there are no game windows.",
                "Use the normal windows_* tools, or ask the user to add a gameProfiles entry to config.json and reload the MCP server.");
        }

        if (string.IsNullOrWhiteSpace(spec))
        {
            var games = GameTools.OpenGames(_s);
            if (games.Count == 0)
            {
                var candidates = GameTools.Candidates(_s);
                if (candidates.Count > 0)
                {
                    throw new ToolException("game_profile_mismatch",
                        "No window matches a game profile, but a window of a profiled process is open and failed a title/windowClass constraint.",
                        GameTools.FixProfile)
                    {
                        Payload = new { candidates }
                    };
                }
                throw new ToolException("no_game_window", "No window of any profiled game process is open.",
                    "Start the game with the normal windows_* tools (launcher), wait for its window, then call windows_game_status.");
            }
            return games[0];
        }

        var window = _s.Session.ResolveWindow(spec);
        var profile = _s.Config.MatchGame(window);
        if (profile == null)
        {
            var misses = _s.Config.NearMisses(window.ProcessName, window.Title, window.ClassName);
            if (misses.Count > 0)
            {
                throw new ToolException("game_profile_mismatch",
                    $"{window.Handle} (\"{window.Title}\", {window.ProcessName}, class {window.ClassName}) belongs to a profiled process but failed a title/windowClass constraint; game input is only sent to game windows.",
                    GameTools.FixProfile)
                {
                    Payload = new { candidates = new[] { GameTools.DescribeCandidate(window, misses) } }
                };
            }
            throw new ToolException("not_game_window",
                $"{window.Handle} (\"{window.Title}\", {window.ProcessName}) does not match any gameProfiles entry; game input is only sent to game windows.",
                "Use windows_snapshot / windows_click / windows_key for this window, or call windows_game_status to find the game window.");
        }
        return (window, profile);
    }

    private async Task RunAsync(GameEvent ev, WindowInfo window, ToolContext ctx)
    {
        switch (ev.Kind)
        {
            case GameEventKind.KeyDown:
                GameInput.KeyDown(ev.Key, ctx.Cancel);
                break;
            case GameEventKind.KeyUp:
                GameInput.KeyUp(ev.Key);
                break;
            case GameEventKind.KeyTap:
                GameInput.KeyDown(ev.Key, ctx.Cancel);
                try
                {
                    await HoldAsync(ev.Ms, "the key hold", ctx);
                }
                finally
                {
                    GameInput.KeyUp(ev.Key);
                }
                break;
            case GameEventKind.ButtonDown:
                GameInput.ButtonDown(ev.Button, ctx.Cancel);
                break;
            case GameEventKind.ButtonUp:
                GameInput.ButtonUp(ev.Button);
                break;
            case GameEventKind.Click:
                GameInput.ButtonDown(ev.Button, ctx.Cancel);
                try
                {
                    await HoldAsync(ev.Ms, "the button hold", ctx);
                }
                finally
                {
                    GameInput.ButtonUp(ev.Button);
                }
                break;
            case GameEventKind.MouseMove:
                await MoveRelativeAsync(ev, ctx);
                break;
            case GameEventKind.MouseTo:
                MoveTo(ev, window, ctx);
                // Give the game a frame to see the new cursor position before a following click.
                await Task.Delay(30, ctx.Cancel);
                break;
            case GameEventKind.Scroll:
                for (var notch = 0; notch < Math.Abs(ev.Amount); notch++)
                {
                    ctx.ThrowIfCancelled();
                    GameInput.ScrollNotch(up: ev.Amount > 0);
                    await Task.Delay(GameSequence.ScrollNotchMs, ctx.Cancel);
                }
                break;
            case GameEventKind.Text:
                foreach (var c in ev.Text ?? "")
                {
                    ctx.ThrowIfCancelled();
                    ctx.Gate.ThrowIfUserActive("typing", ctx);
                    ctx.Gate.EnsureForeground(window);
                    GameInput.TypeChar(c);
                    await Task.Delay(GameSequence.TextCharMs, ctx.Cancel);
                }
                break;
            case GameEventKind.Wait:
                await HoldAsync(ev.Ms, "the wait", ctx);
                break;
        }
    }

    private static async Task MoveRelativeAsync(GameEvent ev, ToolContext ctx)
    {
        var steps = GameSequence.SplitDelta(ev.Dx, ev.Dy, GameSequence.StepsFor(ev.Dx, ev.Dy, ev.Ms));
        if (ev.Ms <= 0)
        {
            foreach (var (dx, dy) in steps) GameInput.MoveRelative(dx, dy);
            return;
        }
        var start = Environment.TickCount64;
        for (var i = 0; i < steps.Count; i++)
        {
            ctx.ThrowIfCancelled();
            GameInput.MoveRelative(steps[i].Dx, steps[i].Dy);
            var due = start + (long)ev.Ms * (i + 1) / steps.Count;
            var remaining = due - Environment.TickCount64;
            if (remaining > 0) await Task.Delay((int)remaining, ctx.Cancel);
        }
    }

    private void MoveTo(GameEvent ev, WindowInfo window, ToolContext ctx)
    {
        var frame = _s.Frames.Require(ev.ImageId);
        if (!frame.ContainsImagePoint(ev.X, ev.Y))
        {
            throw new ToolException("bad_coordinates",
                $"({ev.X:0},{ev.Y:0}) is outside image {frame.Id} ({frame.Width}x{frame.Height}).",
                "Take a new windows_screenshot of the game window and use x,y from that image.");
        }
        var physical = frame.ToPhysical(ev.X, ev.Y);
        ctx.Gate.EnsurePointerTarget(window, physical, activate: false);
        Native.SetCursorPos(physical.X, physical.Y);
    }

    /// <summary>Cancellable wait in short slices that yields (releasing held input in the caller) on user activity.</summary>
    private static async Task HoldAsync(int ms, string what, ToolContext ctx)
    {
        var until = Environment.TickCount64 + ms;
        while (true)
        {
            var remaining = until - Environment.TickCount64;
            if (remaining <= 0) break;
            await Task.Delay((int)Math.Min(25, remaining), ctx.Cancel);
            ctx.Gate.ThrowIfUserActive(what, ctx);
        }
    }
}
