using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Safety;

/// <summary>
/// Global kill-switch hotkey: registers a system hotkey on the pump window and fires <paramref name="onFire"/>
/// on the thread pool whenever it is pressed (also from <see cref="Trigger"/>). Runs the gate's kill-switch path
/// without ever blocking the UI message pump.
/// </summary>
public sealed class KillSwitch : IDisposable
{
    private const int HotkeyId = 0x5E1F; // arbitrary but unique within the pump window
    private const long DebounceMs = 300;

    private readonly UiThread _ui;
    private readonly Action _onFire;
    private readonly uint _mods;
    private readonly uint _vk;
    private long _lastFireTicks = long.MinValue;
    private bool _registered;
    private bool _disposed;

    public KillSwitch(UiThread ui, string hotkey, Action onFire)
    {
        _ui = ui;
        _onFire = onFire;

        uint mods, vk;
        try
        {
            (mods, vk) = HotkeyParser.Parse(hotkey);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"kill switch disabled: {ex.Message}");
            return;
        }
        _mods = mods;
        _vk = vk;

        ui.MessageReceived += OnMessage;
        _registered = ui.Invoke(() =>
        {
            var ok = Native.RegisterHotKey(ui.Handle, HotkeyId, _mods, _vk);
            if (ok) return true;
            var err = new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message;
            Console.Error.WriteLine(
                $"kill switch disabled: RegisterHotKey failed for \"{hotkey}\" ({err}). " +
                "Another application likely owns this hotkey; set a different killSwitchHotkey in config.json " +
                "(e.g. ctrl+alt+f9) and restart the server.");
            return false;
        });
        if (!_registered)
        {
            ui.MessageReceived -= OnMessage;
        }
    }

    /// <summary>True when the hotkey is registered with the system.</summary>
    public bool Registered => _registered;

    /// <summary>Programmatic fire path; runs the same logic as the hotkey message.</summary>
    public void Trigger() => Fire();

    private void OnMessage(Message m)
    {
        if (m.Msg != (int)Native.WM_HOTKEY || m.WParam.ToInt64() != HotkeyId) return;
        Fire();
    }

    private void Fire()
    {
        var now = Environment.TickCount64;
        var last = System.Threading.Interlocked.Read(ref _lastFireTicks);
        if (last != long.MinValue && now - last < DebounceMs) return;
        System.Threading.Interlocked.Exchange(ref _lastFireTicks, now);
        // Never run safety logic on the message pump: it must stay responsive.
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _onFire();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"kill switch handler failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered)
        {
            try
            {
                _ui.Invoke(() => Native.UnregisterHotKey(_ui.Handle, HotkeyId));
            }
            catch (ObjectDisposedException)
            {
                // UI thread already gone; the hotkey dies with the process.
            }
            _ui.MessageReceived -= OnMessage;
        }
    }
}
