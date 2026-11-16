namespace PlaywrightWindows.Mcp.Core;

/// <summary>A hardware key: PS/2 set-1 make code plus whether it carries the E0 (extended) prefix.</summary>
public readonly record struct ScanKey(ushort Code, bool Extended, string Name);

/// <summary>
/// Key names → hardware scan codes for game input. Games that read Raw Input / DirectInput, and GLFW-based
/// games such as Minecraft (which identify a key by the scan code inside WM_KEYDOWN), ignore or misread
/// virtual-key-only injection, so the game path always sends scan codes. Names are physical US-layout
/// positions: "w" is the key in the W position whatever the active keyboard layout is.
/// The Windows keys are deliberately absent: they would open the Start menu and take focus from the game.
/// </summary>
public static class ScanCodes
{
    private static readonly Dictionary<string, ScanKey> Keys = Build();

    public static IEnumerable<string> Names => Keys.Keys;

    public static bool TryGet(string? name, out ScanKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Keys.TryGetValue(name.Trim(), out key);
    }

    public static ScanKey Require(string? name)
    {
        if (TryGet(name, out var key)) return key;
        throw new ToolException("unsupported_key", $"Unknown game key '{name ?? ""}'.",
            "Use a physical key name: a-z, 0-9, f1-f12, space, enter, esc, tab, shift, ctrl, alt, up/down/left/right, " +
            "backspace, delete, minus, equals, comma, period, slash, numpad0-9. The Windows keys are not available.");
    }

    private static Dictionary<string, ScanKey> Build()
    {
        var map = new Dictionary<string, ScanKey>(StringComparer.OrdinalIgnoreCase);
        void Add(ushort code, bool extended, params string[] names)
        {
            foreach (var n in names) map[n] = new ScanKey(code, extended, names[0]);
        }

        // Letter rows (US QWERTY positions).
        const string row1 = "qwertyuiop", row2 = "asdfghjkl", row3 = "zxcvbnm";
        for (var i = 0; i < row1.Length; i++) Add((ushort)(0x10 + i), false, row1[i].ToString());
        for (var i = 0; i < row2.Length; i++) Add((ushort)(0x1E + i), false, row2[i].ToString());
        for (var i = 0; i < row3.Length; i++) Add((ushort)(0x2C + i), false, row3[i].ToString());

        // Number row: 1..9 then 0.
        for (var i = 1; i <= 9; i++) Add((ushort)(0x01 + i), false, i.ToString());
        Add(0x0B, false, "0");

        Add(0x01, false, "esc", "escape");
        Add(0x0C, false, "minus", "-");
        Add(0x0D, false, "equals", "=");
        Add(0x0E, false, "backspace", "bksp");
        Add(0x0F, false, "tab");
        Add(0x1A, false, "lbracket", "[");
        Add(0x1B, false, "rbracket", "]");
        Add(0x1C, false, "enter", "return");
        Add(0x1D, false, "ctrl", "lctrl", "control");
        Add(0x27, false, "semicolon", ";");
        Add(0x28, false, "apostrophe", "quote", "'");
        Add(0x29, false, "grave", "backtick", "`");
        Add(0x2A, false, "shift", "lshift");
        Add(0x2B, false, "backslash", "\\");
        Add(0x33, false, "comma", ",");
        Add(0x34, false, "period", "dot", ".");
        Add(0x35, false, "slash", "/");
        Add(0x36, false, "rshift");
        Add(0x38, false, "alt", "lalt");
        Add(0x39, false, "space", "spacebar");
        Add(0x3A, false, "capslock");

        for (var i = 1; i <= 10; i++) Add((ushort)(0x3A + i), false, "f" + i);
        Add(0x57, false, "f11");
        Add(0x58, false, "f12");

        // Numeric keypad.
        Add(0x52, false, "numpad0");
        Add(0x4F, false, "numpad1");
        Add(0x50, false, "numpad2");
        Add(0x51, false, "numpad3");
        Add(0x4B, false, "numpad4");
        Add(0x4C, false, "numpad5");
        Add(0x4D, false, "numpad6");
        Add(0x47, false, "numpad7");
        Add(0x48, false, "numpad8");
        Add(0x49, false, "numpad9");
        Add(0x53, false, "numpaddecimal");
        Add(0x37, false, "numpadmultiply");
        Add(0x4A, false, "numpadsubtract");
        Add(0x4E, false, "numpadadd");
        Add(0x35, true, "numpaddivide");
        Add(0x1C, true, "numpadenter");

        // Extended (E0-prefixed) keys.
        Add(0x1D, true, "rctrl");
        Add(0x38, true, "ralt");
        Add(0x48, true, "up");
        Add(0x50, true, "down");
        Add(0x4B, true, "left");
        Add(0x4D, true, "right");
        Add(0x47, true, "home");
        Add(0x4F, true, "end");
        Add(0x49, true, "pageup", "pgup");
        Add(0x51, true, "pagedown", "pgdn");
        Add(0x52, true, "insert", "ins");
        Add(0x53, true, "delete", "del");
        return map;
    }
}
