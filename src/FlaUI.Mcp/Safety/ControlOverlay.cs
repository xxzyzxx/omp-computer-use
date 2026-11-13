using System.Drawing;
using Microsoft.Win32;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Safety;

/// <summary>
/// Always-on-top, click-through "agent is driving" indicator: a 6 px frame along the edges of every monitor
/// plus a badge above the primary monitor. Excluded from screen captures (WDA_EXCLUDEFROMCAPTURE) and hidden
/// synchronously by <see cref="HideScope"/> while a capture runs. Rebuilt when the display layout changes.
/// </summary>
public sealed class ControlOverlay : IOverlay
{
    private const int FramePx = 6;
    private static readonly Color FrameColor = Color.FromArgb(255, 69, 0);   // OrangeRed
    private static readonly Color FrameAccent = Color.FromArgb(220, 20, 20); // red inner edge
    private static readonly Color KeyColor = Color.FromArgb(1, 2, 3);        // transparent areas

    private readonly UiThread _ui;
    private readonly ServerConfig _config;
    private readonly List<Form> _frames = new();
    private readonly object _scopeLock = new();
    private BadgeForm? _badge;
    private string? _detail;
    private int _hideCount;
    private bool _built;
    private bool _shown;
    private bool _disposed;

    public ControlOverlay(UiThread ui, ServerConfig config)
    {
        _ui = ui;
        _config = config;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public bool Visible => _shown && _hideCount == 0 && _built;

    public void Show(string? detail)
    {
        if (_disposed) return;
        _detail = detail;
        _shown = true;
        // An active capture scope wins over Show: the overlay stays hidden until it ends.
        _ui.Invoke(() => SetFormsVisible(_shown && _hideCount == 0));
    }

    public void Hide()
    {
        _shown = false;
        _ui.Invoke(() => SetFormsVisible(false));
    }

    /// <summary>
    /// Hides the overlay synchronously (a capture that follows sees no overlay) and restores it when the
    /// scope is disposed. Refcounted so nested scopes work.
    /// </summary>
    public IDisposable HideScope()
    {
        lock (_scopeLock)
        {
            _hideCount++;
            if (_hideCount == 1)
            {
                _ui.Invoke(() => SetFormsVisible(false));
            }
        }
        return new Scope(this);
    }

    private sealed class Scope(ControlOverlay owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (owner._scopeLock)
            {
                owner._hideCount--;
                if (owner._hideCount == 0 && owner._shown && !owner._disposed)
                {
                    owner._ui.Invoke(() => owner.SetFormsVisible(true));
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _shown = false;
        try
        {
            _ui.Invoke(() =>
            {
                foreach (var form in _frames) CloseForm(form);
                _frames.Clear();
                if (_badge != null) { CloseForm(_badge); _badge = null; }
                _built = false;
            });
        }
        catch (ObjectDisposedException)
        {
            // UI thread already gone.
        }
    }

    private static void CloseForm(Form form)
    {
        try
        {
            form.Close();
            form.Dispose();
        }
        catch
        {
            // Form already gone.
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ui.Post(Rebuild);
    }

    /// <summary>Recreates the per-monitor frames (display layout changed). Runs on the UI thread.</summary>
    private void Rebuild()
    {
        if (_disposed) return;
        foreach (var form in _frames) CloseForm(form);
        _frames.Clear();
        _built = false;
        SetFormsVisible(_shown && _hideCount == 0);
    }

    private void Build()
    {
        foreach (var monitor in Monitors.All())
        {
            _frames.Add(new FrameForm(monitor.Bounds));
        }
        _badge = new BadgeForm();
        _badge.SetText($"AGENT CONTROLLING {(string.IsNullOrWhiteSpace(_detail) ? "apps" : _detail)} — {_config.KillSwitchHotkey} to stop");
        PositionBadge();
        _built = true;
    }

    private void PositionBadge()
    {
        var badge = _badge;
        if (badge == null) return;
        var primary = Monitors.Primary() ?? Monitors.All().FirstOrDefault();
        if (primary == null) return;
        var x = primary.Bounds.X + (primary.Bounds.Width - badge.Width) / 2;
        badge.Location = new Point(x, primary.Bounds.Y + Math.Min(48, primary.Bounds.Height / 20));
    }

    private void SetFormsVisible(bool visible)
    {
        if (!_built && !visible) return;
        if (visible && !_built) Build();
        foreach (var frame in _frames) frame.Visible = visible;
        if (_badge != null) _badge.Visible = visible;
    }

    /// <summary>Borderless, layered, click-through frame that paints only the monitor's edges.</summary>
    private sealed class FrameForm : Form
    {
        public FrameForm(Rectangle bounds)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = KeyColor;
            TransparencyKey = KeyColor;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW |
                              Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _ = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var w = ClientSize.Width;
            var h = ClientSize.Height;
            using var brush = new SolidBrush(FrameColor);
            using var accent = new SolidBrush(FrameAccent);
            g.FillRectangle(brush, 0, 0, w, FramePx);                          // top
            g.FillRectangle(brush, 0, h - FramePx, w, FramePx);                // bottom
            g.FillRectangle(brush, 0, 0, FramePx, h);                          // left
            g.FillRectangle(brush, w - FramePx, 0, FramePx, h);                // right
            g.FillRectangle(accent, 0, FramePx - 2, w, 2);                     // red inner edges
            g.FillRectangle(accent, 0, h - FramePx, w, 2);
            g.FillRectangle(accent, FramePx - 2, 0, 2, h);
            g.FillRectangle(accent, w - FramePx, 0, 2, h);
        }
    }

    /// <summary>Non-activating badge shown near the top-centre of the primary monitor.</summary>
    private sealed class BadgeForm : Form
    {
        private readonly Label _label = new()
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(255, 140, 0), // dark orange on black: high contrast
            BackColor = Color.FromArgb(12, 12, 12),
            Font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point),
            Padding = new Padding(18, 10, 18, 10),
            UseCompatibleTextRendering = false
        };

        public BadgeForm()
        {
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = _label.BackColor;
            Controls.Add(_label);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW |
                              Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _ = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        }

        public void SetText(string text) => _label.Text = text; // AutoSize grows the form synchronously
    }
}
