using System.Diagnostics;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>Foreground helpers used before every keystroke and by windows_focus.</summary>
public static class WindowActivation
{
    /// <summary>True when the window (or a top-level window of the same process) currently has focus.</summary>
    public static bool IsForeground(WindowInfo window)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return false;
        }
        return Native.GetAncestor(fg, Native.GA_ROOT) == window.Hwnd || fg == window.Hwnd;
    }

    /// <summary>
    /// Restores a minimized window, then brings <paramref name="window"/> to the foreground, escalating through the
    /// ways Windows allows a process to take the foreground: AllowSetForegroundWindow + AttachThreadInput to the
    /// current foreground thread + SetForegroundWindow; then a synthetic ALT tap (this process becomes the last
    /// input sender) + SetForegroundWindow; then SwitchToThisWindow. False when the window still is not in front.
    /// </summary>
    public static bool BringToForeground(WindowInfo window)
    {
        var hwnd = window.Hwnd;
        if (!Native.IsWindow(hwnd))
        {
            return false;
        }
        if (Native.IsIconic(hwnd))
        {
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
        }
        if (IsForegroundRoot(hwnd))
        {
            return true;
        }

        Native.AllowSetForegroundWindow((uint)Environment.ProcessId);
        if (TryActivateWithAttachedInput(hwnd) && WaitForeground(hwnd, 400))
        {
            return true;
        }

        // A synthetic key press makes this process the last input sender, which unlocks SetForegroundWindow.
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT);
        FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT);
        Native.SetForegroundWindow(hwnd);
        Native.BringWindowToTop(hwnd);
        if (WaitForeground(hwnd, 400))
        {
            return true;
        }

        Native.SwitchToThisWindow(hwnd, true);
        return WaitForeground(hwnd, 600);
    }

    private static bool TryActivateWithAttachedInput(IntPtr hwnd)
    {
        var fg = Native.GetForegroundWindow();
        var fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out _) : 0;
        var ourThread = Native.GetCurrentThreadId();
        var attached = false;
        try
        {
            if (fgThread != 0 && fgThread != ourThread)
            {
                attached = Native.AttachThreadInput(ourThread, fgThread, true);
            }
            Native.BringWindowToTop(hwnd);
            return Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                Native.AttachThreadInput(ourThread, fgThread, false);
            }
        }
    }

    private static bool WaitForeground(IntPtr hwnd, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (IsForegroundRoot(hwnd))
            {
                return true;
            }
            Thread.Sleep(25);
        }
        return IsForegroundRoot(hwnd);
    }

    private static bool IsForegroundRoot(IntPtr hwnd)
    {
        var fg = Native.GetForegroundWindow();
        return fg != IntPtr.Zero && Native.GetAncestor(fg, Native.GA_ROOT) == hwnd;
    }
}
