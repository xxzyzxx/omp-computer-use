using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Tracks keys and mouse buttons this server is holding down (key chords, drags, hold_ms) so the kill switch
/// can release them instantly. All injection that holds state must go through <see cref="Press"/>/<see cref="MouseDown"/>.
/// The physical down and the tracking entry happen under the same lock, so a kill switch that fires before a
/// waiting press can never leave the input physically down after cleanup; pass a cancellation token to Press
/// so a press queued behind the lock is dropped instead of sent.
/// </summary>
public static class HeldInput
{
    private static readonly object Lock = new();
    private static readonly HashSet<VirtualKeyShort> Keys = new();
    private static readonly HashSet<MouseButton> Buttons = new();

    public static void Press(VirtualKeyShort key, CancellationToken cancel = default)
    {
        lock (Lock)
        {
            cancel.ThrowIfCancellationRequested();
            Keys.Add(key);
            Keyboard.Press(key);
        }
    }

    public static void Release(VirtualKeyShort key)
    {
        lock (Lock)
        {
            if (!Keys.Contains(key)) return;
            Keyboard.Release(key);
            Keys.Remove(key);
        }
    }

    public static void MouseDown(MouseButton button, CancellationToken cancel = default)
    {
        lock (Lock)
        {
            cancel.ThrowIfCancellationRequested();
            Buttons.Add(button);
            Mouse.Down(button);
        }
    }

    public static void MouseUp(MouseButton button)
    {
        lock (Lock)
        {
            if (!Buttons.Contains(button)) return;
            Mouse.Up(button);
            Buttons.Remove(button);
        }
    }

    public static (int keys, int buttons) Snapshot()
    {
        lock (Lock) return (Keys.Count, Buttons.Count);
    }

    /// <summary>Releases exactly the tracked keys and buttons, swallowing injection errors.</summary>
    public static void ReleaseAll()
    {
        lock (Lock)
        {
            foreach (var b in Buttons)
            {
                try { Mouse.Up(b); } catch { /* best effort */ }
            }
            foreach (var k in Keys)
            {
                try { Keyboard.Release(k); } catch { /* best effort */ }
            }
            Keys.Clear();
            Buttons.Clear();
        }
    }
}
