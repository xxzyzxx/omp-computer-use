using System.Text.Json;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Sends key chords ("ctrl+s") or a sequence of chords. Modifiers are held through HeldInput so the kill
/// switch can release them; the target window is foreground-checked right before the keys go out.
/// Registered twice: windows_key and (upstream-compatible) windows_send_keys.
/// </summary>
public sealed class KeyTool : ToolBase
{
    private readonly Services _s;
    private readonly string _name;

    public KeyTool(Services services, string name)
    {
        _s = services;
        _name = name;
    }

    public override string Name => _name;
    public override ToolKind Kind => ToolKind.Action;

    public override string Description =>
        "Sends key chords or a chord sequence to a window. keys is one chord string like \"ctrl+s\", \"alt+f4\", " +
        "\"enter\", \"f5\", \"A\" or an array of chords for a sequence ([\"ctrl+a\",\"delete\"]; separators between " +
        "sequence items, 30 ms apart). Modifiers: ctrl, shift, alt, win. Options: hold_ms (hold the full chord " +
        "before release, default 0), ref/mark to focus an element first, window (focus-safety target; default " +
        "foreground). The target window must be in the foreground or the keys are not sent (focus_lost). " +
        "Returns the chords sent and the window.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            keys = new
            {
                oneOf = new object[]
                {
                    new { type = "string", description = "One chord like \"ctrl+s\" or \"enter\"" },
                    new { type = "array", items = new { type = "string" }, description = "Chord sequence, 30 ms apart" }
                },
                description = "Chord or chord sequence"
            },
            hold_ms = new { type = "integer", description = "Hold the full chord before release, 0..5000 (default 0)" },
            @ref = new { type = "string", description = "Element ref from windows_snapshot to focus first" },
            mark = new { type = "integer", description = "Mark number from the latest set-of-marks screenshot" },
            window = new { type = "string", description = "Focus-safety window spec (default foreground)" }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var chords = ParseChords(arguments);
        var holdMs = ActionArgs.IntInRange(arguments, "hold_ms", 0, 0, 5000, "a chord hold of 0..5000 ms");

        var target = TargetResolver.ResolveOptional(_s, arguments);
        var element = target?.Element;
        var window = target?.Window ?? _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));

        await ctx.Gate.EnsureControlAsync(window, ctx);
        ctx.ThrowIfCancelled();

        var wasForeground = WindowActivation.IsForeground(window);
        if (!wasForeground)
        {
            WindowActivation.BringToForeground(window);
        }
        element?.Element.Focus();
        ctx.Gate.EnsureForeground(window);

        for (var i = 0; i < chords.Count; i++)
        {
            ctx.ThrowIfCancelled();
            var chord = chords[i];
            foreach (var mod in chord.Modifiers)
            {
                HeldInput.Press(mod);
            }
            try
            {
                Keyboard.Press(chord.Key);
                await Task.Delay(holdMs > 0 ? holdMs : 15, ctx.Cancel);
                Keyboard.Release(chord.Key);
            }
            finally
            {
                for (var m = chord.Modifiers.Count - 1; m >= 0; m--)
                {
                    HeldInput.Release(chord.Modifiers[m]);
                }
            }
            if (i < chords.Count - 1)
            {
                await Task.Delay(30, ctx.Cancel);
            }
        }

        return JsonResult(new
        {
            ok = true,
            method = "keys",
            target = string.Join(", ", chords.Select(c => c.Raw)),
            chords = chords.Count,
            hold_ms = holdMs,
            window = window.Handle
        });
    }

    private static IReadOnlyList<KeyChord> ParseChords(JsonElement? arguments)
    {
        List<string> tokens = new();
        if (arguments is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("keys", out var keys))
        {
            if (keys.ValueKind == JsonValueKind.String)
            {
                tokens.Add(keys.GetString() ?? "");
            }
            else if (keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in keys.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw new ToolException("bad_request", "keys items must be chord strings.",
                            "Pass an array of chords like [\"ctrl+a\",\"delete\"].");
                    }
                    tokens.Add(item.GetString() ?? "");
                }
            }
            else
            {
                throw new ToolException("bad_request", "keys must be a chord string or an array of chords.",
                    "Pass \"ctrl+s\" or [\"ctrl+a\",\"delete\"].");
            }
        }
        else if (HasUpstreamChord(arguments, out var chord))
        {
            tokens.Add(chord);
        }
        else
        {
            throw new ToolException("bad_request", "keys is required.",
                "Pass a chord like \"ctrl+s\" or an array of chords like [\"ctrl+a\",\"delete\"].");
        }
        return KeyChords.ParseSequence(tokens);
    }

    private static bool HasUpstreamChord(JsonElement? arguments, out string chord)
    {
        chord = "";
        var value = GetStringArgument(arguments, "chord") ?? GetStringArgument(arguments, "key");
        if (value != null)
        {
            chord = value;
            return true;
        }
        return false;
    }
}
