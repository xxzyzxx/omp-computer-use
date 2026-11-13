using System.Diagnostics;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA2;
using FlaUI.UIA3;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>A top-level window as the model sees it. All coordinates are physical pixels.</summary>
public sealed record WindowInfo(
    string Handle,
    IntPtr Hwnd,
    string Title,
    uint Pid,
    string? ProcessName,
    string? ExePath,
    Rectangle Bounds,
    int MonitorId,
    bool Minimized,
    bool Focused,
    string ClassName)
{
    public object ToJson() => new
    {
        handle = Handle,
        hwnd = Hwnd.ToInt64(),
        title = Title,
        process = ProcessName,
        pid = Pid,
        monitor = MonitorId,
        bounds = new { x = Bounds.X, y = Bounds.Y, width = Bounds.Width, height = Bounds.Height },
        minimized = Minimized,
        focused = Focused
    };
}

/// <summary>
/// Resolves window specs to live windows, hands out stable <c>wN</c> handles (one per HWND), picks the UIA
/// flavour per process (UIA3 by default, UIA2 for configured apps) and launches applications.
/// </summary>
public class SessionManager : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly Lazy<UIA2Automation> _uia2;
    private readonly ServerConfig _config;
    private readonly Dictionary<IntPtr, string> _handlesByHwnd = new();
    private readonly Dictionary<string, IntPtr> _hwndsByHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private int _windowCounter;

    public SessionManager(ServerConfig? config = null)
    {
        _config = config ?? new ServerConfig();
        _automation = new UIA3Automation
        {
            ConnectionTimeout = TimeSpan.FromMilliseconds(_config.UiaConnectionTimeoutMs),
            TransactionTimeout = TimeSpan.FromMilliseconds(_config.UiaTransactionTimeoutMs)
        };
        _uia2 = new Lazy<UIA2Automation>(() => new UIA2Automation());
    }

    public UIA3Automation Automation => _automation;
    public ServerConfig Config => _config;

    /// <summary>UIA flavour for a process (UIA2 only when configured for that exe).</summary>
    public AutomationBase AutomationFor(string? processName)
        => _config.IsUia2App(processName) ? _uia2.Value : _automation;

    public AutomationBase AutomationFor(WindowInfo window) => AutomationFor(window.ProcessName);

    // ----- handles -----

    public string HandleFor(IntPtr hwnd)
    {
        lock (_lock)
        {
            if (_handlesByHwnd.TryGetValue(hwnd, out var existing)) return existing;
            var handle = $"w{++_windowCounter}";
            _handlesByHwnd[hwnd] = handle;
            _hwndsByHandle[handle] = hwnd;
            return handle;
        }
    }

    public string RegisterWindow(Window window)
    {
        var hwnd = HwndOf(window);
        return HandleFor(hwnd);
    }

    public IntPtr? HwndForHandle(string handle)
    {
        lock (_lock)
        {
            return _hwndsByHandle.TryGetValue(handle, out var h) ? h : null;
        }
    }

    // ----- window info -----

    public WindowInfo? TryGetWindowInfo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return null;
        var pid = Native.GetWindowProcessId(hwnd);
        var exe = Native.GetProcessImagePath(pid);
        var bounds = Native.GetVisibleWindowBounds(hwnd);
        var monitor = Monitors.ForWindow(hwnd);
        return new WindowInfo(
            HandleFor(hwnd),
            hwnd,
            Native.GetWindowText(hwnd),
            pid,
            exe != null ? Path.GetFileName(exe) : Native.GetProcessExeName(pid),
            exe,
            bounds,
            monitor?.Id ?? 0,
            Native.IsIconic(hwnd),
            Native.GetForegroundWindow() == hwnd,
            Native.GetClassName(hwnd));
    }

    public WindowInfo GetWindowInfo(IntPtr hwnd)
        => TryGetWindowInfo(hwnd) ?? throw new ToolException("window_not_found", $"Window 0x{hwnd.ToInt64():X} no longer exists.",
            "Call windows_list_windows and pick a current window.");

    public WindowInfo? ForegroundWindow()
    {
        var fg = Native.GetForegroundWindow();
        return fg == IntPtr.Zero ? null : TryGetWindowInfo(fg);
    }

    /// <summary>
    /// Enumerates top-level application windows front to back: visible, not cloaked, not tool windows, titled.
    /// </summary>
    public List<WindowInfo> ListTopLevelWindows(bool includeMinimized = true, bool includeUntitled = false)
    {
        var shell = Native.GetShellWindow();
        var result = new List<WindowInfo>();
        foreach (var hwnd in Native.TopLevelWindows())
        {
            if (hwnd == shell || !Native.IsWindowVisible(hwnd) || Native.IsCloaked(hwnd)) continue;
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) continue;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) continue;
            var minimized = Native.IsIconic(hwnd);
            if (minimized && !includeMinimized) continue;
            var info = TryGetWindowInfo(hwnd);
            if (info == null) continue;
            if (!includeUntitled && string.IsNullOrWhiteSpace(info.Title)) continue;
            if (!minimized && (info.Bounds.Width <= 0 || info.Bounds.Height <= 0)) continue;
            result.Add(info);
        }
        return result;
    }

    /// <summary>
    /// Resolves a window spec: null/empty = foreground window; "wN" = handle; integer = HWND; "pid:N";
    /// otherwise case-insensitive title substring, then process name (with or without .exe).
    /// </summary>
    public WindowInfo ResolveWindow(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return ForegroundWindow() ?? throw new ToolException("window_not_found", "No foreground window.",
                "Call windows_list_windows and pass window=<handle> explicitly.");
        }

        spec = spec.Trim();
        if (spec.StartsWith("w", StringComparison.OrdinalIgnoreCase) && spec.Length > 1 && spec[1..].All(char.IsDigit))
        {
            var hwnd = HwndForHandle(spec.ToLowerInvariant());
            if (hwnd == null)
            {
                throw new ToolException("window_not_found", $"Unknown window handle {spec}.",
                    "Call windows_list_windows to get current handles.");
            }
            return TryGetWindowInfo(hwnd.Value) ?? throw new ToolException("window_not_found", $"Window {spec} was closed.",
                "Call windows_list_windows and pick a current window.");
        }

        if (long.TryParse(spec, out var raw) && raw > 0)
        {
            var info = TryGetWindowInfo(new IntPtr(raw));
            if (info != null) return info;
        }

        if (spec.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) && uint.TryParse(spec[4..], out var pid))
        {
            var byPid = ListTopLevelWindows().FirstOrDefault(w => w.Pid == pid);
            if (byPid != null) return byPid;
            throw new ToolException("window_not_found", $"No top-level window for pid {pid}.",
                "Call windows_list_windows to see available windows.");
        }

        var windows = ListTopLevelWindows();
        var byTitle = windows.Where(w => w.Title.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byTitle.Count == 1) return byTitle[0];
        if (byTitle.Count > 1)
        {
            // Prefer the exact title, then the frontmost match.
            return byTitle.FirstOrDefault(w => string.Equals(w.Title, spec, StringComparison.OrdinalIgnoreCase)) ?? byTitle[0];
        }

        var exe = spec.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? spec : spec + ".exe";
        var byProcess = windows.Where(w => string.Equals(w.ProcessName, exe, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byProcess.Count > 0) return byProcess[0];

        throw new ToolException("window_not_found", $"No window matches '{spec}'.",
            "Call windows_list_windows and use one of the listed handles, titles or process names.");
    }

    /// <summary>Live FlaUI window for a resolved window (UIA2 or UIA3 per process).</summary>
    public Window GetWindow(WindowInfo info)
    {
        var element = AutomationFor(info).FromHandle(info.Hwnd);
        return element.AsWindow();
    }

    /// <summary>Compatibility helper for callers that only have a handle string.</summary>
    public Window? GetWindow(string handle)
    {
        var hwnd = HwndForHandle(handle);
        if (hwnd == null || !Native.IsWindow(hwnd.Value)) return null;
        var info = TryGetWindowInfo(hwnd.Value);
        return info == null ? null : GetWindow(info);
    }

    /// <summary>Top-level HWND that hosts an element (GA_ROOT of its native handle), or zero.</summary>
    public static IntPtr RootHwndOf(AutomationElement element)
    {
        try
        {
            if (element.Properties.NativeWindowHandle.TryGetValue(out var h) && h != IntPtr.Zero)
            {
                var root = Native.GetAncestor(h, Native.GA_ROOT);
                return root == IntPtr.Zero ? h : root;
            }
        }
        catch
        {
            // Fall through to the parent walk.
        }

        try
        {
            var current = element;
            for (var i = 0; i < 64 && current != null; i++)
            {
                if (current.Properties.NativeWindowHandle.TryGetValue(out var h) && h != IntPtr.Zero)
                {
                    var root = Native.GetAncestor(h, Native.GA_ROOT);
                    return root == IntPtr.Zero ? h : root;
                }
                current = current.Parent;
            }
        }
        catch
        {
            // Element vanished mid-walk.
        }
        return IntPtr.Zero;
    }

    public static IntPtr HwndOf(Window window)
    {
        if (window.Properties.NativeWindowHandle.TryGetValue(out var h)) return h;
        return RootHwndOf(window);
    }

    // ----- launching -----

    /// <summary>
    /// Launches an application by path, name (resolved through the shell) or AUMID (contains '!'), then waits
    /// for a new top-level window owned by the process or appearing after launch.
    /// </summary>
    public WindowInfo LaunchApp(string app, string[]? args = null, int timeoutMs = 15000)
    {
        var before = ListTopLevelWindows().Select(w => w.Hwnd).ToHashSet();
        var isAumid = app.Contains('!') && !File.Exists(app);
        var psi = isAumid
            ? new ProcessStartInfo { FileName = "explorer.exe", Arguments = "shell:AppsFolder\\" + app, UseShellExecute = true }
            : new ProcessStartInfo
            {
                FileName = app,
                Arguments = args != null ? string.Join(" ", args.Select(QuoteArg)) : "",
                UseShellExecute = true
            };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new ToolException("launch_failed", $"Failed to start {app}: {ex.Message}",
                "Check the path/name; for Store apps pass the AUMID (e.g. Microsoft.WindowsCalculator_8wekyb3d8bbwe!App).");
        }

        var launchedPid = process != null && !isAumid ? (uint)process.Id : 0u;
        var sw = Stopwatch.StartNew();
        var appName = Path.GetFileNameWithoutExtension(app);
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var windows = ListTopLevelWindows(includeMinimized: false);
            var byPid = launchedPid != 0 ? windows.FirstOrDefault(w => w.Pid == launchedPid) : null;
            if (byPid != null) return byPid;

            var fresh = windows.Where(w => !before.Contains(w.Hwnd)).ToList();
            if (fresh.Count > 0)
            {
                var named = fresh.FirstOrDefault(w =>
                    (w.ProcessName != null && w.ProcessName.Contains(appName, StringComparison.OrdinalIgnoreCase)) ||
                    w.Title.Contains(appName, StringComparison.OrdinalIgnoreCase));
                if (named != null) return named;
                if (sw.ElapsedMilliseconds > 1500) return fresh[0];
            }
            Thread.Sleep(150);
        }

        throw new ToolException("launch_timeout", $"Started {app} but no new window appeared within {timeoutMs}ms.",
            "Call windows_list_windows; the app may already be running (reuse its window) or still starting (call windows_wait with title).");
    }

    private static string QuoteArg(string a) => a.Contains(' ') && !a.StartsWith('"') ? $"\"{a}\"" : a;

    public void Dispose()
    {
        if (_uia2.IsValueCreated)
        {
            _uia2.Value.Dispose();
        }
        _automation.Dispose();
    }
}
