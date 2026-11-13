using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Tracks keys and mouse buttons this server is holding down (key chords, drags, hold_ms) so the kill switch
/// can release them instantly. All injection that holds state must go through <see cref="Press"/>/<see cref="Release"/>.
/// </summary>
public static class HeldInput
{
    private static readonly object Lock = new();
    private static readonly HashSet<VirtualKeyShort> Keys = new();
    private static readonly HashSet<MouseButton> Buttons = new();

    public static void Press(VirtualKeyShort key)
    {
        lock (Lock) Keys.Add(key);
        Keyboard.Press(key);
    }

    public static void Release(VirtualKeyShort key)
    {
        Keyboard.Release(key);
        lock (Lock) Keys.Remove(key);
    }

    public static void MouseDown(MouseButton button)
    {
        lock (Lock) Buttons.Add(button);
        Mouse.Down(button);
    }

    public static void MouseUp(MouseButton button)
    {
        Mouse.Up(button);
        lock (Lock) Buttons.Remove(button);
    }

    public static (int keys, int buttons) Snapshot()
    {
        lock (Lock) return (Keys.Count, Buttons.Count);
    }

    /// <summary>Releases everything we hold plus every modifier (belt and braces), swallowing injection errors.</summary>
    public static void ReleaseAll()
    {
        VirtualKeyShort[] keys;
        MouseButton[] buttons;
        lock (Lock)
        {
            keys = Keys.ToArray();
            buttons = Buttons.ToArray();
            Keys.Clear();
            Buttons.Clear();
        }
        foreach (var b in buttons)
        {
            try { Mouse.Up(b); } catch { /* best effort */ }
        }
        foreach (var k in keys)
        {
            try { Keyboard.Release(k); } catch { /* best effort */ }
        }
        foreach (var k in new[]
                 {
                     VirtualKeyShort.LCONTROL, VirtualKeyShort.RCONTROL, VirtualKeyShort.CONTROL,
                     VirtualKeyShort.LSHIFT, VirtualKeyShort.RSHIFT, VirtualKeyShort.SHIFT,
                     VirtualKeyShort.LMENU, VirtualKeyShort.RMENU, VirtualKeyShort.ALT,
                     VirtualKeyShort.LWIN, VirtualKeyShort.RWIN
                 })
        {
            try { Keyboard.Release(k); } catch { /* best effort */ }
        }
    }
}
