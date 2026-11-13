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
    /// Restores a minimized window, then brings <paramref name="window"/> to the foreground: AllowSetForegroundWindow,
    /// AttachThreadInput between this thread and the foreground window's thread, SetForegroundWindow + BringWindowToTop,
    /// detach, then poll up to 1 s. False when the window still is not in the foreground.
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
        Native.AllowSetForegroundWindow((uint)Environment.ProcessId);

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
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                Native.AttachThreadInput(ourThread, fgThread, false);
            }
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000)
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
