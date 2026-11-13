using System.Drawing;
using System.Media;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Safety;

/// <summary>
/// Human-in-the-loop dialogs on the UI thread: a topmost access request and a risky-action confirmation,
/// each with a live countdown. The form is created and shown via <see cref="UiThread.Invoke"/>, completed
/// through a TaskCompletionSource and awaited on the caller's thread — the UI thread never blocks.
/// </summary>
public sealed class DialogPrompter : IUserPrompter
{
    private readonly UiThread _ui;
    private readonly ServerConfig _config;

    public DialogPrompter(UiThread ui, ServerConfig config)
    {
        _ui = ui;
        _config = config;
    }

    public Task<PromptAnswer> AskAccessAsync(AccessPrompt prompt, CancellationToken cancel)
    {
        var apps = string.Join(", ", prompt.Apps);
        var verb = prompt.Tier == AccessTier.Control ? "control" : "view";
        return ShowAsync("AI agent access request", $"Let the AI agent {verb} {apps}?",
            $"Reason given by the agent: {prompt.Reason}", prompt.Timeout, cancel,
            ("Allow this session", PromptAnswer.AllowSession),
            ("Allow always", PromptAnswer.AllowAlways),
            ("Deny", PromptAnswer.Denied));
    }

    public Task<PromptAnswer> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancel)
    {
        return ShowAsync("AI agent risky action", $"Let the AI agent {prompt.Action} \"{prompt.Target}\"?",
            $"App: {prompt.App}\nMatched safety rule: /{prompt.Pattern}/", prompt.Timeout, cancel,
            ("Allow once", PromptAnswer.AllowSession),
            ("Deny", PromptAnswer.Denied));
    }

    private async Task<PromptAnswer> ShowAsync(string title, string question, string details, TimeSpan timeout,
        CancellationToken cancel, params (string Label, PromptAnswer Answer)[] buttons)
    {
        var tcs = new TaskCompletionSource<PromptAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        PromptForm? form = null;
        // Cancellation closes the dialog and is answered as Denied (the agent must not proceed).
        using var reg = cancel.Register(() => _ui.Post(() => { form?.Close(); tcs.TrySetResult(PromptAnswer.Denied); }));

        _ui.Invoke(() =>
        {
            form = new PromptForm(title, question, details, timeout, buttons, tcs);
            form.Show();
            SystemSounds.Exclamation.Play();
        });
        if (cancel.IsCancellationRequested) tcs.TrySetResult(PromptAnswer.Denied);

        return await tcs.Task;
    }

    /// <summary>
    /// Topmost countdown dialog; every close path completes the TaskCompletionSource exactly once.
    /// Pixel sizes are scaled to the DPI of the monitor it opens on: point-sized fonts grow with DPI, so
    /// fixed pixel widths clip the text and the buttons on scaled displays.
    /// </summary>
    private sealed class PromptForm : Form
    {
        private readonly TaskCompletionSource<PromptAnswer> _tcs;
        private readonly Label _countdown;
        private readonly DateTime _deadline;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly MonitorInfo? _monitor;
        private PromptAnswer _answer;

        public PromptForm(string title, string question, string details, TimeSpan timeout,
            (string Label, PromptAnswer Answer)[] buttons, TaskCompletionSource<PromptAnswer> tcs)
        {
            _tcs = tcs;
            _monitor = Native.GetCursorPos(out var pt)
                ? Monitors.For(new Point(pt.X, pt.Y)) ?? Monitors.Primary()
                : Monitors.Primary();
            var scale = _monitor?.Scale ?? 1.0;
            int Px(int logical) => (int)Math.Round(logical * scale);
            var textWidth = Px(560);

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            Label Line(string text, float points, FontStyle style, Color color) => new()
            {
                Text = text,
                AutoSize = true,
                UseMnemonic = false,
                MaximumSize = new Size(textWidth, 0),
                Margin = new Padding(0, 0, 0, Px(12)),
                ForeColor = color,
                Font = new Font("Segoe UI", points, style)
            };

            _countdown = Line("", 10f, FontStyle.Regular, Color.DarkRed);
            var buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, Px(4), 0, 0)
            };
            foreach (var (label, answer) in buttons)
            {
                var button = new Button
                {
                    Text = label,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    UseMnemonic = false,
                    Padding = new Padding(Px(12), Px(5), Px(12), Px(5)),
                    Margin = new Padding(0, 0, Px(10), 0),
                    Font = new Font("Segoe UI", 10f)
                };
                button.Click += (_, _) => Complete(answer);
                buttonPanel.Controls.Add(button);
            }

            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(Px(20), Px(18), Px(20), Px(18))
            };
            root.Controls.Add(Line(question, 13f, FontStyle.Bold, SystemColors.ControlText));
            root.Controls.Add(Line(details, 10f, FontStyle.Regular, SystemColors.ControlText));
            root.Controls.Add(Line("The agent cannot answer this dialog — only you can.", 9f, FontStyle.Regular, SystemColors.GrayText));
            root.Controls.Add(_countdown);
            root.Controls.Add(buttonPanel);
            Controls.Add(root);

            _deadline = DateTime.UtcNow + timeout;
            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Tick();

            FormClosed += (_, _) =>
            {
                _timer.Stop();
                _timer.Dispose();
                _tcs.TrySetResult(_answer);
                Dispose();
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Auto-sizing is final only once the handle exists; centre on the cursor's monitor now.
            PerformLayout();
            var area = _monitor?.WorkArea ?? Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
            Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
        }

        private void Tick()
        {
            var remaining = _deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _answer = PromptAnswer.Timeout;
                Close();
                return;
            }
            _countdown.Text = $"Answer within {(int)Math.Ceiling(remaining.TotalSeconds)} s — no answer counts as Deny.";
        }

        private void Complete(PromptAnswer answer)
        {
            _answer = answer;
            Close();
        }
    }
}
