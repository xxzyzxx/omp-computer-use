using System.Runtime.InteropServices;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Hardware-level input for game windows: scan-code keyboard events, relative mouse motion (what Raw Input
/// and mouse-look cameras read), mouse buttons and the wheel, all through SendInput. It is separate from the
/// FlaUI virtual-key path used for ordinary applications and is only reached through windows_game_input.
///
/// Everything pressed here is tracked so the kill switch, a tool timeout and the watchdog can release it.
/// The watchdog releases held input when the guarded game window stops being the foreground window or when
/// input has been held longer than <see cref="MaxHeldMs"/>, so a crashed or closed game can never leave a
/// key stuck down in whatever window comes next.
/// </summary>
public static class GameInput
{
    public const int MaxHeldMs = 60000;

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_SCANCODE = 0x8;
    private const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20,
        MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800;
    private const int WHEEL_DELTA = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private static readonly object Lock = new();
    private static readonly Dictionary<ScanKey, string> HeldKeys = new();
    private static readonly HashSet<GameButton> HeldButtons = new();
    private static IntPtr _guardedWindow;
    private static long _heldSince;
    private static long _mouseMoveYieldSuppressedUntil;
    private static System.Threading.Timer? _watchdog;

    /// <summary>
    /// Until this tick (Environment.TickCount64) the user-activity detector ignores mouse movement. Read from
    /// the low-level mouse hook, so it is a plain volatile read.
    /// </summary>
    public static long MouseMoveYieldSuppressedUntil => Volatile.Read(ref _mouseMoveYieldSuppressedUntil);

    public static void SuppressMouseMoveYield(int ms)
    {
        var until = Environment.TickCount64 + ms;
        if (until > Volatile.Read(ref _mouseMoveYieldSuppressedUntil))
        {
            Volatile.Write(ref _mouseMoveYieldSuppressedUntil, until);
        }
    }

    /// <summary>Names the game window whose foreground state keeps held input alive.</summary>
    public static void Guard(IntPtr gameWindow)
    {
        lock (Lock)
        {
            _guardedWindow = gameWindow;
            _watchdog ??= new System.Threading.Timer(_ => Watchdog(), null, 250, 250);
        }
    }

    public static void KeyDown(ScanKey key, CancellationToken cancel = default)
    {
        lock (Lock)
        {
            cancel.ThrowIfCancellationRequested();
            NoteHeld();
            HeldKeys[key] = key.Name;
            Send(KeyInput(key, up: false));
        }
    }

    public static void KeyUp(ScanKey key)
    {
        lock (Lock)
        {
            // Always send the up event: releasing a key that is not down is harmless, and it lets the model
            // clear a key that the game still believes is held.
            Send(KeyInput(key, up: true));
            HeldKeys.Remove(key);
        }
    }

    public static void ButtonDown(GameButton button, CancellationToken cancel = default)
    {
        lock (Lock)
        {
            cancel.ThrowIfCancellationRequested();
            NoteHeld();
            HeldButtons.Add(button);
            Send(MouseInput(ButtonFlag(button, down: true)));
        }
    }

    public static void ButtonUp(GameButton button)
    {
        lock (Lock)
        {
            Send(MouseInput(ButtonFlag(button, down: false)));
            HeldButtons.Remove(button);
        }
    }

    /// <summary>Relative mouse motion in mouse counts. This is what Raw Input and mouse-look cameras consume.</summary>
    public static void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Send(MouseInput(MOUSEEVENTF_MOVE, dx, dy));
    }

    /// <summary>Wheel notches; positive scrolls up (away from the user).</summary>
    public static void Scroll(int notches)
    {
        if (notches == 0) return;
        Send(MouseInput(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(notches * WHEEL_DELTA))));
    }

    /// <summary>Types one UTF-16 code unit as a Unicode packet (chat boxes, text fields inside the game).</summary>
    public static void TypeChar(char c)
    {
        Send(
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
    }

    public static (IReadOnlyList<string> Keys, IReadOnlyList<string> Buttons) Held()
    {
        lock (Lock)
        {
            return (HeldKeys.Values.OrderBy(k => k).ToList(), HeldButtons.Select(b => b.ToString().ToLowerInvariant()).OrderBy(b => b).ToList());
        }
    }

    public static (int keys, int buttons) Snapshot()
    {
        lock (Lock) return (HeldKeys.Count, HeldButtons.Count);
    }

    /// <summary>Releases exactly the tracked keys and buttons, swallowing injection errors.</summary>
    public static void ReleaseAll()
    {
        lock (Lock)
        {
            ReleaseAllLocked();
        }
    }

    private static void ReleaseAllLocked()
    {
        foreach (var button in HeldButtons)
        {
            try { Send(MouseInput(ButtonFlag(button, down: false))); } catch { /* best effort */ }
        }
        foreach (var key in HeldKeys.Keys)
        {
            try { Send(KeyInput(key, up: true)); } catch { /* best effort */ }
        }
        HeldButtons.Clear();
        HeldKeys.Clear();
    }

    private static void NoteHeld()
    {
        if (HeldKeys.Count == 0 && HeldButtons.Count == 0)
        {
            _heldSince = Environment.TickCount64;
        }
    }

    private static void Watchdog()
    {
        try
        {
            lock (Lock)
            {
                if (HeldKeys.Count == 0 && HeldButtons.Count == 0) return;
                var expired = Environment.TickCount64 - _heldSince > MaxHeldMs;
                var foreground = GetForegroundWindow();
                var root = foreground != IntPtr.Zero ? GetAncestor(foreground, 2 /* GA_ROOT */) : IntPtr.Zero;
                if (root == IntPtr.Zero) root = foreground;
                var lostFocus = _guardedWindow != IntPtr.Zero && root != _guardedWindow;
                if (expired || lostFocus)
                {
                    ReleaseAllLocked();
                    Console.Error.WriteLine($"game input watchdog: released held input ({(expired ? "held too long" : "game window lost focus")})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"game input watchdog failed: {ex.Message}");
        }
    }

    private static INPUT KeyInput(ScanKey key, bool up)
    {
        var flags = KEYEVENTF_SCANCODE;
        if (key.Extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (up) flags |= KEYEVENTF_KEYUP;
        return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = key.Code, dwFlags = flags } } };
    }

    private static INPUT MouseInput(uint flags, int dx = 0, int dy = 0, uint data = 0)
        => new() { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };

    private static uint ButtonFlag(GameButton button, bool down) => button switch
    {
        GameButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        GameButton.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
    };

    private static void Send(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new ToolException("input_blocked",
                $"Windows accepted {sent} of {inputs.Length} input events (error {Marshal.GetLastWin32Error()}).",
                "Another process is blocking input or the game runs elevated; run the harness at the same integrity level as the game.");
        }
    }
}
