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
        var body = $"Apps: {apps}\nAccess level: {prompt.Tier.ToString().ToLowerInvariant()}\nReason given by the agent: {prompt.Reason}";
        return ShowAsync("Access request from the AI agent", body, prompt.Timeout, cancel,
            ("Allow this session", PromptAnswer.AllowSession),
            ("Allow always", PromptAnswer.AllowAlways),
            ("Deny", PromptAnswer.Denied));
    }

    public Task<PromptAnswer> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancel)
    {
        var body = $"Action: {prompt.Action}\nTarget: {prompt.Target}\nApp: {prompt.App}\nMatched safety rule: /{prompt.Pattern}/";
        return ShowAsync("Risky action needs human confirmation", body, prompt.Timeout, cancel,
            ("Allow once", PromptAnswer.AllowSession),
            ("Deny", PromptAnswer.Denied));
    }

    private async Task<PromptAnswer> ShowAsync(string title, string body, TimeSpan timeout, CancellationToken cancel,
        params (string Label, PromptAnswer Answer)[] buttons)
    {
        var tcs = new TaskCompletionSource<PromptAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        PromptForm? form = null;
        // Cancellation closes the dialog and is answered as Denied (the agent must not proceed).
        using var reg = cancel.Register(() => _ui.Post(() => { form?.Close(); tcs.TrySetResult(PromptAnswer.Denied); }));

        _ui.Invoke(() =>
        {
            form = new PromptForm(title, body, timeout, buttons, tcs);
            form.Show();
            SystemSounds.Exclamation.Play();
        });
        if (cancel.IsCancellationRequested) tcs.TrySetResult(PromptAnswer.Denied);

        return await tcs.Task;
    }

    /// <summary>Topmost countdown dialog; every close path completes the TaskCompletionSource exactly once.</summary>
    private sealed class PromptForm : Form
    {
        private readonly TaskCompletionSource<PromptAnswer> _tcs;
        private readonly Label _countdown;
        private readonly DateTime _deadline;
        private readonly System.Windows.Forms.Timer _timer;
        private PromptAnswer _answer;

        public PromptForm(string title, string body, TimeSpan timeout,
            (string Label, PromptAnswer Answer)[] buttons, TaskCompletionSource<PromptAnswer> tcs)
        {
            _tcs = tcs;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowOnly;

            var notice = new Label
            {
                Text = "This request comes from the AI agent running on this computer. The agent cannot answer it — only you can.",
                Dock = DockStyle.Top,
                ForeColor = Color.FromArgb(180, 60, 0),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Padding = new Padding(14, 12, 14, 4),
                AutoSize = true,
                MaximumSize = new Size(532, 0)
            };
            var bodyLabel = new Label
            {
                Text = body,
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 10f),
                Padding = new Padding(14, 8, 14, 8),
                AutoSize = true,
                MaximumSize = new Size(532, 0)
            };
            _countdown = new Label
            {
                Dock = DockStyle.Top,
                ForeColor = Color.DarkRed,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Padding = new Padding(14, 2, 14, 10),
                AutoSize = true,
                MaximumSize = new Size(532, 0)
            };
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(10),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            foreach (var (label, answer) in buttons)
            {
                var button = new Button
                {
                    Text = label,
                    AutoSize = true,
                    Margin = new Padding(6, 3, 6, 3),
                    Font = new Font("Segoe UI", 10f)
                };
                button.Click += (_, _) => Complete(answer);
                buttonPanel.Controls.Add(button);
            }

            Controls.Add(buttonPanel);
            Controls.Add(_countdown);
            Controls.Add(bodyLabel);
            Controls.Add(notice);

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
            CenterOnCursorMonitor();
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

        private void CenterOnCursorMonitor()
        {
            var monitor = Native.GetCursorPos(out var pt)
                ? Monitors.For(new Point(pt.X, pt.Y)) ?? Monitors.Primary()
                : Monitors.Primary();
            if (monitor == null)
            {
                StartPosition = FormStartPosition.CenterScreen;
                return;
            }
            var area = monitor.WorkArea;
            Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
        }
    }
}
