using System.Windows.Forms;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// One STA thread with a Windows message loop. Everything that needs a pump lives here:
/// the kill-switch hotkey window, low-level input hooks, the control overlay, approval dialogs and the clipboard.
/// Tools run on thread-pool threads and marshal onto this thread with <see cref="Invoke"/>.
/// </summary>
public sealed class UiThread : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private PumpWindow? _pump;
    private Exception? _startupError;

    /// <summary>Raised on the UI thread for every message the hidden pump window receives (WM_HOTKEY etc.).</summary>
    public event Action<Message>? MessageReceived;

    public UiThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "windows-mcp-ui" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupError != null)
        {
            throw new InvalidOperationException("UI thread failed to start", _startupError);
        }
    }

    /// <summary>HWND of the hidden pump window (hotkey registration target).</summary>
    public IntPtr Handle => _pump?.Handle ?? IntPtr.Zero;

    public int ThreadId { get; private set; }

    public bool IsOnUiThread => Thread.CurrentThread == _thread;

    private void Run()
    {
        try
        {
            ThreadId = Environment.CurrentManagedThreadId;
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            _pump = new PumpWindow(this);
            _ = _pump.Handle; // force creation on this thread
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            return;
        }
        _ready.Set();
        Application.Run();
    }

    public void Invoke(Action action)
    {
        if (IsOnUiThread)
        {
            action();
            return;
        }
        _pump!.Invoke(action);
    }

    public T Invoke<T>(Func<T> func)
    {
        if (IsOnUiThread)
        {
            return func();
        }
        return (T)_pump!.Invoke(func)!;
    }

    public void Post(Action action)
    {
        _pump!.BeginInvoke(action);
    }

    internal void RaiseMessage(Message m) => MessageReceived?.Invoke(m);

    public void Dispose()
    {
        if (_pump == null) return;
        try
        {
            _pump.BeginInvoke(() =>
            {
                _pump.Close();
                Application.ExitThread();
            });
            _thread.Join(1500);
        }
        catch
        {
            // Process is exiting.
        }
    }

    private sealed class PumpWindow : Form
    {
        private readonly UiThread _owner;

        public PumpWindow(UiThread owner)
        {
            _owner = owner;
            Text = "windows-mcp pump";
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-32000, -32000);
            Size = new System.Drawing.Size(1, 1);
            Opacity = 0;
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            _owner.RaiseMessage(m);
        }
    }
}
