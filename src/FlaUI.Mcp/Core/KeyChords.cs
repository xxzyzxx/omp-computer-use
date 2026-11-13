using System.Text.RegularExpressions;
using FlaUI.Core.WindowsAPI;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>One parsed keystroke: modifiers are held first, the key is tapped, then modifiers are released in reverse.</summary>
public sealed record KeyChord(IReadOnlyList<VirtualKeyShort> Modifiers, VirtualKeyShort Key, string Raw);

/// <summary>
/// Pure parser for key chords ("ctrl+s", "alt+f4", "enter", "A", "$") and sequences (["ctrl+a", "delete"]).
/// Modifiers: ctrl/control, shift, alt, win/meta/cmd. Unknown tokens fail with <c>unsupported_key</c> naming the token.
/// </summary>
public static class KeyChords
{
    private static readonly Dictionary<string, VirtualKeyShort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = VirtualKeyShort.RETURN,
        ["return"] = VirtualKeyShort.RETURN,
        ["tab"] = VirtualKeyShort.TAB,
        ["space"] = VirtualKeyShort.SPACE,
        ["spacebar"] = VirtualKeyShort.SPACE,
        ["esc"] = VirtualKeyShort.ESCAPE,
        ["escape"] = VirtualKeyShort.ESCAPE,
        ["backspace"] = VirtualKeyShort.BACK,
        ["bksp"] = VirtualKeyShort.BACK,
        ["delete"] = VirtualKeyShort.DELETE,
        ["del"] = VirtualKeyShort.DELETE,
        ["insert"] = VirtualKeyShort.INSERT,
        ["ins"] = VirtualKeyShort.INSERT,
        ["home"] = VirtualKeyShort.HOME,
        ["end"] = VirtualKeyShort.END,
        ["pageup"] = VirtualKeyShort.PRIOR,
        ["pgup"] = VirtualKeyShort.PRIOR,
        ["pagedown"] = VirtualKeyShort.NEXT,
        ["pgdn"] = VirtualKeyShort.NEXT,
        ["up"] = VirtualKeyShort.UP,
        ["down"] = VirtualKeyShort.DOWN,
        ["left"] = VirtualKeyShort.LEFT,
        ["right"] = VirtualKeyShort.RIGHT,
        ["printscreen"] = VirtualKeyShort.SNAPSHOT,
        ["pause"] = VirtualKeyShort.PAUSE,
        ["break"] = VirtualKeyShort.PAUSE,
        ["scrolllock"] = VirtualKeyShort.SCROLL,
        ["numlock"] = VirtualKeyShort.NUMLOCK,
        ["capslock"] = VirtualKeyShort.CAPITAL,
        ["apps"] = VirtualKeyShort.APPS,
        ["menu"] = VirtualKeyShort.APPS
    };

    public static bool IsModifier(string token, out VirtualKeyShort key)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                key = VirtualKeyShort.CONTROL;
                return true;
            case "shift":
                key = VirtualKeyShort.SHIFT;
                return true;
            case "alt":
                key = VirtualKeyShort.ALT;
                return true;
            case "win":
            case "meta":
            case "cmd":
                key = VirtualKeyShort.LWIN;
                return true;
            default:
                key = 0;
                return false;
        }
    }

    /// <summary>Parses a sequence of chords. A comma inside one string is also a sequence separator.</summary>
    public static IReadOnlyList<KeyChord> ParseSequence(IReadOnlyList<string> tokens)
    {
        var chords = new List<KeyChord>();
        foreach (var raw in tokens)
        {
            foreach (var part in raw.Split(','))
            {
                chords.Add(ParseChord(part));
            }
        }
        return chords;
    }

    public static KeyChord ParseChord(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw Bad(raw ?? "", "empty chord");
        }

        var parts = raw.Split('+').Select(p => p.Trim()).ToArray();
        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
        {
            throw Bad(raw, "empty chord part");
        }

        var mods = new List<VirtualKeyShort>();
        VirtualKeyShort key = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            if (IsModifier(token, out var modifier))
            {
                if (mods.Contains(modifier))
                {
                    throw Bad(raw, $"duplicate modifier '{token}'");
                }
                if (i == parts.Length - 1 && key == 0)
                {
                    key = modifier; // "win" alone: the last modifier is tapped
                    break;
                }
                mods.Add(modifier);
                continue;
            }
            if (key != 0)
            {
                throw Bad(raw, $"more than one key ('{token}' after '{key}')");
            }
            (key, var extraMods) = ResolveKey(raw, token);
            mods.AddRange(extraMods);
        }

        if (key == 0)
        {
            throw Bad(raw, "no key");
        }
        return new KeyChord(mods.Distinct().ToList(), key, raw.Trim());
    }

    private static (VirtualKeyShort Key, List<VirtualKeyShort> ExtraModifiers) ResolveKey(string raw, string token)
    {
        // Single characters go through VkKeyScan so shifted characters ("A", "$") honour the keyboard layout.
        if (token.Length == 1)
        {
            var (vk, extra) = CharKey(token[0]);
            if (vk != null) return (vk.Value, extra);
        }

        if (NamedKeys.TryGetValue(token, out var named))
        {
            return (named, new List<VirtualKeyShort>());
        }

        if (Regex.IsMatch(token, "^f([1-9]|1[0-9]|2[0-4])$", RegexOptions.IgnoreCase) &&
            Enum.TryParse(token.ToUpperInvariant(), out VirtualKeyShort fkey))
        {
            return (fkey, new List<VirtualKeyShort>());
        }

        throw Bad(raw, $"unsupported key '{token}'");
    }

    private static (VirtualKeyShort? Key, List<VirtualKeyShort> ExtraModifiers) CharKey(char c)
    {
        var scan = Native.VkKeyScanW(c);
        if (scan != -1)
        {
            var extra = new List<VirtualKeyShort>();
            var shiftState = (scan >> 8) & 0xFF;
            if ((shiftState & 1) != 0) extra.Add(VirtualKeyShort.SHIFT);
            if ((shiftState & 2) != 0) extra.Add(VirtualKeyShort.CONTROL);
            if ((shiftState & 4) != 0) extra.Add(VirtualKeyShort.ALT);
            return ((VirtualKeyShort)(scan & 0xFF), extra);
        }
        // No layout entry (tests, exotic layouts): fall back to plain ASCII letters/digits.
        var lower = char.ToLowerInvariant(c);
        if (lower is >= 'a' and <= 'z')
        {
            return ((VirtualKeyShort)((int)VirtualKeyShort.KEY_A + (lower - 'a')), new List<VirtualKeyShort>());
        }
        if (lower is >= '0' and <= '9')
        {
            return ((VirtualKeyShort)((int)VirtualKeyShort.KEY_0 + (lower - '0')), new List<VirtualKeyShort>());
        }
        return (null, new List<VirtualKeyShort>());
    }

    private static ToolException Bad(string raw, string why) => new("unsupported_key",
        $"Cannot parse key chord '{raw}': {why}.",
        "Use chords like ctrl+s, alt+f4, enter, esc, f1-f24 or a single character; pass an array for a sequence; modifiers are ctrl, shift, alt, win.");
}
