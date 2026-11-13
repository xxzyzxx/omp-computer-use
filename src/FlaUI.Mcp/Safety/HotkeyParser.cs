using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Safety;

/// <summary>
/// Parses the config.killSwitchHotkey string into RegisterHotKey arguments.
/// Syntax: "+"-separated tokens, e.g. "ctrl+alt+pause". Modifiers: ctrl/control, alt, shift, win/meta.
/// </summary>
public static class HotkeyParser
{
    /// <summary>Parses a hotkey string. Throws ArgumentException naming the offending token.</summary>
    public static (uint Modifiers, uint VirtualKey) Parse(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            throw new ArgumentException("Hotkey is empty. Set killSwitchHotkey in config.json, e.g. \"ctrl+alt+pause\".");
        }

        uint mods = Native.MOD_NOREPEAT;
        uint? vk = null;
        foreach (var raw in hotkey.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                throw new ArgumentException($"Hotkey \"{hotkey}\" contains an empty token. Use tokens like ctrl+alt+pause.");
            }
            if (TryModifier(token, out var m))
            {
                mods |= m;
                continue;
            }
            if (vk != null)
            {
                throw new ArgumentException($"Hotkey \"{hotkey}\" has more than one key (\"{token}\"). Use modifier tokens plus one key, e.g. ctrl+alt+pause.");
            }
            vk = Key(token) ?? throw new ArgumentException(
                $"Unknown hotkey key \"{token}\" in \"{hotkey}\". Supported keys: pause/break, scrolllock, printscreen, f1-f24, esc, home, end, insert, delete, pageup, pagedown, space, letters a-z, digits 0-9.");
        }

        if (vk == null)
        {
            throw new ArgumentException($"Hotkey \"{hotkey}\" has no key token. Add one, e.g. ctrl+alt+pause.");
        }
        return (mods, vk.Value);
    }

    private static bool TryModifier(string token, out uint mod)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control": mod = Native.MOD_CONTROL; return true;
            case "alt": mod = Native.MOD_ALT; return true;
            case "shift": mod = Native.MOD_SHIFT; return true;
            case "win":
            case "meta": mod = Native.MOD_WIN; return true;
            default: mod = 0; return false;
        }
    }

    private static uint? Key(string token)
    {
        if (token.Length == 1)
        {
            var c = char.ToLowerInvariant(token[0]);
            if (c >= 'a' && c <= 'z') return (uint)(c - 'a' + 0x41);   // VK_A = 0x41
            if (c >= '0' && c <= '9') return (uint)(c - '0' + 0x30);   // VK_0 = 0x30
            return null;
        }
        if (token.Length >= 2 && (token[0] == 'f' || token[0] == 'F') &&
            uint.TryParse(token[1..], out var f) && f >= 1 && f <= 24)
        {
            return 0x6F + f;                                           // VK_F1 = 0x70
        }
        return token.ToLowerInvariant() switch
        {
            "pause" or "break" => 0x13,       // VK_PAUSE
            "scrolllock" => 0x91,             // VK_SCROLL
            "printscreen" => 0x2C,            // VK_SNAPSHOT
            "esc" => 0x1B,                    // VK_ESCAPE
            "home" => 0x24,                   // VK_HOME
            "end" => 0x23,                    // VK_END
            "insert" => 0x2D,                 // VK_INSERT
            "delete" => 0x2E,                 // VK_DELETE
            "pageup" => 0x21,                 // VK_PRIOR
            "pagedown" => 0x22,               // VK_NEXT
            "space" => 0x20,                  // VK_SPACE
            _ => null
        };
    }
}
