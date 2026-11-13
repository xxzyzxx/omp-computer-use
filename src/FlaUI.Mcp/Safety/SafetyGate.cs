using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Safety;

public sealed record AccessResult(bool Granted, PromptAnswer Answer, IReadOnlyList<string> Apps, AccessTier Tier, string Message);

/// <summary>
/// Every tool funnels its safety decisions through here: tiered access per target process, blocked apps,
/// the user-input yield, the control lock/overlay, risky-click confirmation, the password guard, focus safety
/// and the kill switch. YOLO mode short-circuits grants, confirmations, the yield and the password guard only.
/// </summary>
public sealed class SafetyGate
{
    public SafetyGate(SafetyState state, AccessPolicy policy, IUserPrompter prompter, IOverlay overlay, ServerConfig config)
    {
        State = state;
        Policy = policy;
        Prompter = prompter;
        Overlay = overlay;
        Config = config;
        State.ControlChanged += (active, app) =>
        {
            try
            {
                if (active) Overlay.Show(app);
                else Overlay.Hide();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"overlay failure: {ex.Message}");
            }
        };
    }

    public SafetyState State { get; }
    public AccessPolicy Policy { get; }
    public IUserPrompter Prompter { get; }
    public IOverlay Overlay { get; }
    public ServerConfig Config { get; }

    /// <summary>Server → client notification sink (kill switch, user activity). Set by Program.</summary>
    public Func<string, object?, Task>? Notify { get; set; }

    // ----- perception -----

    public void EnsureView(WindowInfo window)
    {
        if (Policy.IsBlocked(window, out var reason))
        {
            throw Blocked(window, reason);
        }
        if (State.TierFor(window.ProcessName) < AccessTier.View)
        {
            throw new ToolException("access_denied", $"No access to {Describe(window)}.",
                $"Call windows_request_access with apps=[\"{window.ProcessName}\"], tier=\"view\" (or \"control\" if you need to act) and a one-line reason.");
        }
    }

    // ----- actions -----

    /// <summary>
    /// Gate for every input-injecting tool: abort check, blocked apps, control tier, user-input yield,
    /// then the control lock (which shows the overlay). Records the target app for the audit log.
    /// </summary>
    public async Task EnsureControlAsync(WindowInfo window, ToolContext ctx)
    {
        ctx.ThrowIfCancelled();
        if (Policy.IsBlocked(window, out var reason))
        {
            throw Blocked(window, reason);
        }
        if (State.TierFor(window.ProcessName) < AccessTier.Control)
        {
            throw new ToolException("access_denied", $"No control access to {Describe(window)}.",
                $"Call windows_request_access with apps=[\"{window.ProcessName}\"], tier=\"control\" and a one-line reason, then retry.");
        }
        await WaitForUserIdleAsync(ctx);
        State.AcquireControl(window.ProcessName);
        ctx.TargetApp = window.ProcessName;
    }

    /// <summary>
    /// Focus safety: the intended window (or another top-level window of the same process, e.g. its dialog)
    /// must be in the foreground before keystrokes go out.
    /// </summary>
    public void EnsureForeground(WindowInfo target)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            throw new ToolException("focus_lost", "No window has keyboard focus.",
                $"Call windows_focus window={target.Handle}, then retry.");
        }
        var root = Native.GetAncestor(fg, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = fg;
        if (root == target.Hwnd || fg == target.Hwnd) return;
        var fgPid = Native.GetWindowProcessId(root);
        var hosted = Native.GetHostedAppProcessId(root);
        if (hosted != 0) fgPid = hosted;
        if (fgPid == target.Pid) return;
        var title = Native.GetWindowText(root);
        var exe = Native.GetProcessExeName(fgPid) ?? $"pid {fgPid}";
        throw new ToolException("focus_lost", $"Focus changed: foreground is \"{title}\" ({exe}), not {target.Handle} ({target.ProcessName}). Nothing was typed.",
            $"Call windows_focus window={target.Handle} and retry; if a dialog opened, windows_snapshot it first.");
    }

    public void EnsureNotPasswordField(bool isPassword, string targetDescription)
    {
        if (isPassword && Config.IsPasswordGuardActive)
        {
            throw new ToolException("password_field", $"{targetDescription} is a password field; the agent never types passwords.",
                "Ask the user to enter the password themselves (call windows_release first), then continue.");
        }
    }

    /// <summary>Blocks while the user is active; after the wait budget returns the user_active status (not an error).</summary>
    public async Task WaitForUserIdleAsync(ToolContext ctx)
    {
        if (!State.IsUserActive) return;
        var deadline = Environment.TickCount64 + Config.UserActiveWaitMs;
        while (State.IsUserActive && Environment.TickCount64 < deadline)
        {
            ctx.ThrowIfCancelled();
            await Task.Delay(100, ctx.Cancel);
        }
        if (State.IsUserActive)
        {
            var since = State.SinceUserInput?.TotalMilliseconds ?? 0;
            throw new ToolException("user_active",
                $"The user is active ({State.LastUserInputKind} {since:F0}ms ago); waiting. Actions resume after {Config.IdleResumeSeconds:0.#}s of idle.",
                "Wait a few seconds and retry. Call windows_resume only if the user explicitly told you to continue.")
            {
                IsError = false,
                Payload = new { sinceUserInputMs = since, idleResumeSeconds = Config.IdleResumeSeconds }
            };
        }
    }

    /// <summary>Confirmation gate for clicks on risky controls (Pay, Delete permanently, …).</summary>
    public async Task ConfirmRiskyAsync(string? elementName, string action, WindowInfo window, ToolContext ctx)
    {
        if (Config.Yolo) return;
        var pattern = Policy.MatchRisky(elementName);
        if (pattern == null) return;

        PromptAnswer answer;
        try
        {
            answer = await Prompter.ConfirmAsync(
                new ConfirmPrompt(action, elementName ?? "", window.ProcessName ?? "?", pattern, TimeSpan.FromSeconds(Config.PromptTimeoutSeconds)),
                ctx.Cancel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            answer = PromptAnswer.Unavailable;
            Console.Error.WriteLine($"confirmation prompt failed: {ex.Message}");
        }

        if (answer is PromptAnswer.AllowSession or PromptAnswer.AllowAlways) return;
        throw new ToolException("needs_user_confirmation",
            $"\"{elementName}\" in {window.ProcessName} matches risky pattern /{pattern}/ and the human did not confirm ({answer}). Nothing was clicked.",
            "Stop and ask the user to confirm this exact action. Retry only after they say yes; they will see a confirmation dialog.")
        {
            IsError = false,
            Payload = new { element = elementName, pattern, app = window.ProcessName, action, answer = answer.ToString() }
        };
    }

    // ----- control tools -----

    public async Task<AccessResult> RequestAccessAsync(IReadOnlyList<string> apps, AccessTier tier, string reason, ToolContext ctx)
    {
        var normalized = apps.Select(SafetyState.NormalizeApp).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (normalized.Count == 0)
        {
            throw new ToolException("bad_request", "No apps given.", "Pass apps=[\"notepad.exe\"] (exe names) with tier and reason.");
        }
        if (tier == AccessTier.None)
        {
            throw new ToolException("bad_request", "tier must be \"view\" or \"control\".", "Retry with tier=\"view\" or tier=\"control\".");
        }

        if (Config.Yolo)
        {
            foreach (var app in normalized)
            {
                State.AddGrant(app, AccessTier.Control, false, reason, "yolo");
            }
            return new AccessResult(true, PromptAnswer.AllowSession, normalized, AccessTier.Control, "YOLO mode: all apps are auto-granted control.");
        }

        var pending = normalized.Where(a => State.TierFor(a) < tier).ToList();
        if (pending.Count == 0)
        {
            return new AccessResult(true, PromptAnswer.AllowSession, normalized, tier, "Already granted.");
        }

        PromptAnswer answer;
        try
        {
            answer = await Prompter.AskAccessAsync(new AccessPrompt(pending, tier, reason, TimeSpan.FromSeconds(Config.PromptTimeoutSeconds)), ctx.Cancel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            answer = PromptAnswer.Unavailable;
            Console.Error.WriteLine($"access prompt failed: {ex.Message}");
        }

        switch (answer)
        {
            case PromptAnswer.AllowSession:
            case PromptAnswer.AllowAlways:
                foreach (var app in pending)
                {
                    State.AddGrant(app, tier, answer == PromptAnswer.AllowAlways, reason, "user");
                }
                return new AccessResult(true, answer, normalized, tier,
                    answer == PromptAnswer.AllowAlways ? "Granted permanently by the user." : "Granted for this session by the user.");
            case PromptAnswer.Timeout:
                return new AccessResult(false, answer, normalized, tier,
                    $"The user did not answer within {Config.PromptTimeoutSeconds}s. Ask them to answer the access dialog, then retry.");
            case PromptAnswer.Unavailable:
                return new AccessResult(false, answer, normalized, tier,
                    "No interactive prompt is available (prompts=deny). Ask the user to add the app to the allowlist in config.json or enable yolo mode.");
            default:
                return new AccessResult(false, answer, normalized, tier, "The user denied access. Do not retry without new instructions from them.");
        }
    }

    public void Release()
    {
        State.ReleaseControl();
    }

    public void Resume()
    {
        State.ExplicitResume();
    }

    /// <summary>
    /// Kill switch: abort everything in flight, release held input, revoke control grants, drop the control lock
    /// and tell the harness. Safe to call from any thread, including the hotkey message loop.
    /// </summary>
    public void KillSwitch(string reason)
    {
        State.TriggerAbort(reason);
        try
        {
            HeldInput.ReleaseAll();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"held input release failed: {ex.Message}");
        }
        var revoked = State.RevokeControlGrants();
        State.ReleaseControl();
        var payload = new { reason, revokedGrants = revoked, at = DateTime.UtcNow, message = "Kill switch engaged: agent turn must stop." };
        var notify = Notify;
        if (notify != null)
        {
            _ = Task.Run(async () =>
            {
                try { await notify("notifications/windows/kill_switch", payload); }
                catch (Exception ex) { Console.Error.WriteLine($"kill switch notification failed: {ex.Message}"); }
            });
        }
        Console.Error.WriteLine($"KILL SWITCH: {reason}; revoked {revoked} control grant(s)");
    }

    private static string Describe(WindowInfo w) => $"\"{w.Title}\" ({w.ProcessName ?? "pid " + w.Pid}, {w.Handle})";

    private static ToolException Blocked(WindowInfo w, string reason) => new("blocked_app",
        $"Refusing to automate {Describe(w)}: {reason}.",
        "Do not interact with this app. Tell the user; only they can set allowBlockedApps (or yolo) in config.json if this is intended.");
}
