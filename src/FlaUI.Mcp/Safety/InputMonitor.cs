using System.Runtime.InteropServices;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Safety;

/// <summary>
/// Low-level keyboard and mouse hooks that detect real (non-injected) user input. The gate pauses actions
/// while the user is active. Hooks are installed on the UI thread; callbacks stay allocation-free and fast
/// (Windows removes slow low-level hooks), then always call the next hook.
/// </summary>
public sealed class InputMonitor : IDisposable
{
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    private readonly UiThread _ui;
    private readonly SafetyState _state;
    private readonly int _deadZonePx;
    private readonly Native.HookProc _keyboardProc;
    private readonly Native.HookProc _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private int _lastX;
    private int _lastY;
    private bool _hasLastMove;
    private bool _disposed;

    public InputMonitor(UiThread ui, SafetyState state, ServerConfig config)
    {
        _ui = ui;
        _state = state;
        _deadZonePx = config.UserInputMouseDeadZonePx;
        // Keep the delegates in fields: the hooks hold raw function pointers to them.
        _keyboardProc = KeyboardHookProc;
        _mouseProc = MouseHookProc;
        (_keyboardHook, _mouseHook) = ui.Invoke(() =>
        {
            var module = Native.GetModuleHandleW(null);
            var kb = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
            if (kb == IntPtr.Zero)
            {
                Console.Error.WriteLine($"input monitor: WH_KEYBOARD_LL failed (error {Marshal.GetLastWin32Error()}); user-activity detection is degraded.");
            }
            var ms = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseProc, module, 0);
            if (ms == IntPtr.Zero)
            {
                Console.Error.WriteLine($"input monitor: WH_MOUSE_LL failed (error {Marshal.GetLastWin32Error()}); user-activity detection is degraded.");
            }
            return (kb, ms);
        });
    }

    /// <summary>True when both low-level hooks are installed.</summary>
    public bool Active => _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (uint)wParam.ToInt64();
            if (msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
            {
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                if ((info.flags & Native.LLKHF_INJECTED) == 0)
                {
                    _state.NoteUserInput("keyboard");
                }
            }
        }
        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & Native.LLMHF_INJECTED) == 0)
            {
                var msg = (uint)wParam.ToInt64();
                if (msg == Native.WM_MOUSEMOVE)
                {
                    if (!_hasLastMove ||
                        Math.Abs(info.pt.X - _lastX) >= _deadZonePx ||
                        Math.Abs(info.pt.Y - _lastY) >= _deadZonePx)
                    {
                        _lastX = info.pt.X;
                        _lastY = info.pt.Y;
                        _hasLastMove = true;
                        _state.NoteUserInput("mouse");
                    }
                }
                else
                {
                    _lastX = info.pt.X;
                    _lastY = info.pt.Y;
                    _hasLastMove = true;
                    _state.NoteUserInput("mouse"); // buttons and wheel always count
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var (kb, ms) = (_keyboardHook, _mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
        if (kb == IntPtr.Zero && ms == IntPtr.Zero) return;
        try
        {
            _ui.Invoke(() =>
            {
                if (kb != IntPtr.Zero) Native.UnhookWindowsHookEx(kb);
                if (ms != IntPtr.Zero) Native.UnhookWindowsHookEx(ms);
            });
        }
        catch (ObjectDisposedException)
        {
            // UI thread already gone; the hooks die with the process.
        }
    }
}
