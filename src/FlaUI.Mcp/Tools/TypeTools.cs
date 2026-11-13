using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Types text into the focused element (or a ref/mark target it focuses first). Password fields are
/// refused; the window is brought forward and focus-checked right before the keystrokes.
/// </summary>
public sealed class TypeTool : ToolBase
{
    private readonly Services _s;

    public TypeTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_type";
    public override ToolKind Kind => ToolKind.Action;
    public override IReadOnlyList<string> RedactedArguments => new[] { "text" };

    public override string Description =>
        "Types text into the focused element of the target window. Optional ref/mark: the element is focused " +
        "first (and its window brought forward). Options: submit (press Enter afterwards, default false), " +
        "delay_ms (per-character delay in ms, default 0). The characters are sent with synthetic keyboard input; " +
        "text is not read back or verified. The target window must be in the foreground or focus is lost with an " +
        "error. Password fields are refused in normal mode (YOLO bypasses the check) (password_field). " +
        "Returns the number of characters sent. Coordinates are physical screen " +
        "pixels; nothing here uses coordinates except ref/mark resolution.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            text = new { type = "string", description = "Text to type (required)" },
            @ref = new { type = "string", description = "Element ref from windows_snapshot to focus first" },
            mark = new { type = "integer", description = "Mark number from the latest set-of-marks screenshot" },
            window = new { type = "string", description = "Window handle/title when no ref is given (default foreground)" },
            submit = new { type = "boolean", description = "Press Enter after the text (default false)" },
            delay_ms = new { type = "integer", description = "Delay between characters in ms (default 0)" }
        },
        required = new[] { "text" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var text = GetStringArgument(arguments, "text") ?? throw new ToolException("bad_request", "text is required.",
            "Pass the text to type as a string; use windows_key for chords like ctrl+s.");
        var submit = GetBoolArgument(arguments, "submit");
        var delayMs = ActionArgs.IntInRange(arguments, "delay_ms", 0, 0, 1000, "a per-character delay of 0..1000 ms");

        var target = TargetResolver.ResolveOptional(_s, arguments);
        var element = target?.Element;
        var window = target?.Window ?? _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));

        await ctx.Gate.EnsureControlAsync(window, ctx);
        ctx.ThrowIfCancelled();

        var isPassword = IsPasswordField(_s, window, element, target);
        ctx.Gate.EnsureNotPasswordField(isPassword, target == null ? "the focused element" : target.TargetDescription);

        var wasForeground = WindowActivation.IsForeground(window);
        if (!wasForeground)
        {
            WindowActivation.BringToForeground(window);
        }
        element?.Element.Focus();
        ctx.Gate.EnsureForeground(window);
        ctx.ThrowIfCancelled();

        // Send one character at a time so cancellation and real user activity interrupt between
        // characters (bulk Keyboard.Type is uninterruptible for long text). No substring allocations;
        // the per-char overload sends each char through FlaUI's keyboard mapping — this is not a
        // guaranteed full-Unicode injection path (chars outside the keyboard layout's mapping may be
        // sent as best-effort key events rather than injected as text).
        if (text.Length > 0)
        {
            foreach (var c in text)
            {
                ctx.ThrowIfCancelled();
                ctx.Gate.ThrowIfUserActive("the typing", ctx);
                ctx.Gate.EnsureForeground(window);
                Keyboard.Type(c);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, ctx.Cancel);
                }
            }

            // Give the injected keystrokes a moment to be consumed by the target before anything
            // else happens. The synthetic input sits in the thread/input queue while the next UIA
            // call (e.g. an Invoke on a button) can be dispatched first and overtake it, so a
            // subsequent action may observe stale text. This is a best-effort grace period, not a
            // guaranteed OS queue drain.
            await Task.Delay(100, ctx.Cancel);
        }
        if (submit)
        {
            ctx.ThrowIfCancelled();
            ctx.Gate.ThrowIfUserActive("the submit key", ctx);
            ctx.Gate.EnsureForeground(window);
            try
            {
                HeldInput.Press(VirtualKeyShort.RETURN, ctx.Cancel);
                await Task.Delay(15, ctx.Cancel);
            }
            finally
            {
                HeldInput.Release(VirtualKeyShort.RETURN);
            }
            // Same best-effort grace after the Enter release so a following UIA action cannot
            // overtake the just-submitted input.
            await Task.Delay(100, ctx.Cancel);
        }

        return JsonResult(new
        {
            ok = true,
            method = "type",
            target = target?.TargetDescription ?? "focused element",
            typed = text.Length,
            submit,
            window = window.Handle
        });
    }

    /// <summary>Live IsPassword when readable; falls back to what the snapshot recorded.</summary>
    internal static bool IsPasswordField(Services services, WindowInfo window, ResolvedElement? element, ResolvedTarget? target)
    {
        try
        {
            if (element != null && element.Element.Properties.IsPassword.ValueOrDefault)
            {
                return true;
            }
            if (element == null)
            {
                var focused = services.Session.AutomationFor(window).FocusedElement();
                return focused?.Properties.IsPassword.ValueOrDefault ?? false;
            }
        }
        catch
        {
            // Fall back to what the snapshot recorded.
        }
        return target?.Element?.Meta.IsPassword ?? false;
    }
}

/// <summary>
/// Fills a ref/mark element with a value: ValuePattern.SetValue when supported and writable (with read-back
/// verification), otherwise focus + select-all + type. Password fields are refused. Audit-redacted.
/// </summary>
public sealed class FillTool : ToolBase
{
    private readonly Services _s;

    public FillTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_fill";
    public override ToolKind Kind => ToolKind.Action;
    public override IReadOnlyList<string> RedactedArguments => new[] { "value" };

    public override string Description =>
        "Sets the value of an input element (ref or mark required). Prefers the UIA Value pattern (works even " +
        "when the window is not foreground, verified by reading the value back), otherwise focuses the element " +
        "and types with ctrl+a first. Password fields are refused in normal mode (YOLO bypasses the check) " +
        "(password_field). Returns method " +
        "(value|type) and verified (read-back true/false; null when typed).";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new { type = "string", description = "Element ref from windows_snapshot (required)" },
            mark = new { type = "integer", description = "Mark number from the latest set-of-marks screenshot" },
            value = new { type = "string", description = "New value (empty string clears via select-all + delete)" }
        },
        required = new[] { "value" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        if (!HasArgument(arguments, "value"))
        {
            throw new ToolException("bad_request", "value is required.",
                "Pass the new value as a string (empty string clears the field).");
        }
        var value = GetStringArgument(arguments, "value") ?? "";
        var target = TargetResolver.Resolve(_s, arguments);
        if (target.Element == null)
        {
            throw new ToolException("bad_request", "windows_fill needs ref or mark.",
                "Snapshot the window (windows_snapshot) and pass a ref, or use windows_type to type into the focused element.");
        }

        var window = target.Window;
        var element = target.Element;
        await ctx.Gate.EnsureControlAsync(window, ctx);
        ctx.ThrowIfCancelled();
        ctx.Gate.EnsureNotPasswordField(TypeTool.IsPasswordField(_s, window, element, target), target.TargetDescription);

        var method = "";
        bool? verified = null;
        try
        {
            if (element.Element.Patterns.Value.IsSupported)
            {
                var vp = element.Element.Patterns.Value.Pattern;
                if (!vp.IsReadOnly.ValueOrDefault)
                {
                    vp.SetValue(value);
                    verified = string.Equals(vp.Value.ValueOrDefault, value, StringComparison.Ordinal);
                    method = "value";
                }
            }
        }
        catch
        {
            // Value pattern advertised but failed; fall back to focus + type.
        }

        if (method.Length == 0)
        {
            var wasForeground = WindowActivation.IsForeground(window);
            if (!wasForeground)
            {
                WindowActivation.BringToForeground(window);
            }
            element.Element.Focus();
            ctx.Gate.EnsureForeground(window);
            ctx.ThrowIfCancelled();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            await Task.Delay(15, ctx.Cancel);
            if (value.Length > 0)
            {
                // Same per-character interruptibility as windows_type (see there).
                foreach (var c in value)
                {
                    ctx.ThrowIfCancelled();
                    ctx.Gate.ThrowIfUserActive("the fill typing", ctx);
                    ctx.Gate.EnsureForeground(window);
                    Keyboard.Type(c);
                }
            }
            else
            {
                ctx.ThrowIfCancelled();
                ctx.Gate.ThrowIfUserActive("the clear key", ctx);
                ctx.Gate.EnsureForeground(window);
                try
                {
                    HeldInput.Press(VirtualKeyShort.DELETE, ctx.Cancel);
                    await Task.Delay(15, ctx.Cancel);
                }
                finally
                {
                    HeldInput.Release(VirtualKeyShort.DELETE);
                }
            }
            method = "type";

            // Same best-effort grace as windows_type: injected keystrokes can still be sitting in
            // the input queue when a subsequent UIA call runs, and that call can overtake them.
            // Not a guaranteed OS queue drain — just a short window for the target to consume
            // the input before the typed value is verified/returned.
            await Task.Delay(100, ctx.Cancel);
        }

        return JsonResult(new
        {
            ok = true,
            method,
            target = target.TargetDescription,
            value_length = value.Length,
            verified,
            window = window.Handle
        });
    }
}
