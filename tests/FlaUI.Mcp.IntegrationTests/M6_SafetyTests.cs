using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Safety;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M6 — Safety layer: access_denied, kill switch, user-input yield, risky confirmation,
/// password guard, blocked apps, drag destination gating and YOLO. Uses the safety-config
/// fixture (yolo=false, deny prompter, prompt timeout 1 s). Every test restores the safety
/// config/state it mutates in a finally block so xUnit run order cannot leak between tests.
/// </summary>
[Collection("TestApps")]
public class M6_SafetyTests : IDisposable
{
    private readonly TestAppFixture _f;
    private Process? _calc;

    public M6_SafetyTests(TestAppFixture fixture) => _f = fixture;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(short vKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string text);

    private const string WinFormsTitle = "FlaUI-MCP Test App";
    private const string WpfTitle = "FlaUI-MCP WPF Test App";
    private const short VkLButton = 0x01;
    private const short VkControl = 0x11;
    private const short VkA = 0x41;

    private static bool KeyDown(short vkey) => (GetAsyncKeyState(vkey) & 0x8000) != 0;

    /// <summary>Parses "Click count: N" from a WinForms snapshot (-1 when the label is absent).</summary>
    private static int ClickCount(string snapshot)
    {
        var m = Regex.Match(snapshot, @"Click count: (\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    /// <summary>Parses "Delete count: N" from a WinForms snapshot (-1 when the label is absent).</summary>
    private static int DeleteCount(string snapshot)
    {
        var m = Regex.Match(snapshot, @"Delete count: (\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    [Fact]
    public async Task UnGrantedApp_SnapshotReturnsAccessDenied_ThenGrant()
    {
        _calc = Process.Start(new ProcessStartInfo
        {
            FileName = "calc.exe",
            UseShellExecute = true
        }) ?? throw new Exception("Could not launch calc.exe");

        var info = await _f.WaitForWindowAsync("Calculator", processName: "CalculatorApp.exe");
        Assert.NotNull(info);
        var calcApp = info!.ProcessName ?? "CalculatorApp.exe";

        try
        {
            var snap = await _f.Call("windows_snapshot", new { window = info!.Handle }, safety: true);
            Assert.True(snap.IsError, $"Expected access_denied, got: {snap.RawText}");
            Assert.NotNull(snap.Header);
            Assert.Equal("access_denied", snap.Header!["code"]?.ToString());
            var next = snap.Header!["next"]?.ToString() ?? "";
            Assert.Contains("windows_request_access", next);

            var request = await _f.Call("windows_request_access", new Dictionary<string, object?>
            {
                ["apps"] = new[] { calcApp },
                ["tier"] = "control",
                ["reason"] = "integration test"
            }, safety: true);
            Assert.NotNull(request.Header);
            Assert.Equal(false, (bool?)request.Header!["ok"]);

            // The deny prompter refused the request, so the test grants the tier directly.
            _f.SafetyState.AddGrant(calcApp, AccessTier.Control, false, "integration test", "test");
            var snap2 = await _f.Call("windows_snapshot", new { window = info!.Handle }, safety: true);
            Assert.False(snap2.IsError, $"windows_snapshot failed: {snap2.RawText}");
        }
        finally
        {
            _f.SafetyState.RemoveGrant(calcApp);
        }
    }

    [Fact]
    public async Task KillSwitch_DuringLiveDrag_AbortsFastAndRevokesControl()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        var wpfHandle = _f.WpfHandle;
        Assert.NotEmpty(wpfHandle);

        // Two control grants across two processes so the kill switch must revoke all of them.
        _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");
        _f.SafetyState.AddGrant("WpfTestApp", AccessTier.Control, false, "integration test", "test");

        // The finally must cover every assertion after the grants so a failure can never leave
        // held input, revoked-state abort or grants behind to poison later tests.
        try
        {
            var winSnap = _f.TakeSnapshot(winFormsHandle);
            var clicksBefore = ClickCount(winSnap);
            var fromRef = TestAppFixture.FindRef(winSnap, "Option C", "radio");
            Assert.NotNull(fromRef);
            var toRef = TestAppFixture.FindRef(winSnap, "Conditional Button", "button");
            Assert.NotNull(toRef);
            var clickMeRef = TestAppFixture.FindRef(winSnap, "Click Me", "button");
            Assert.NotNull(clickMeRef);

            // 20 valid steps: ten plain waits, one long live input action (a drag that keeps the left
            // button and ctrl held for ~2 s), then nine clicks that must never run.
            var waitStep = () => new Dictionary<string, object?>
            {
                ["action"] = "wait", ["window"] = winFormsHandle, ["ms"] = 100
            };
            var steps = new System.Collections.Generic.List<object?>();
            for (var i = 0; i < 10; i++)
            {
                steps.Add(waitStep());
            }
            steps.Add(new Dictionary<string, object?>
            {
                ["tool"] = "windows_drag",
                ["args"] = new Dictionary<string, object?>
                {
                    ["from"] = new { @ref = fromRef },
                    ["to"] = new { @ref = toRef },
                    ["hold_ms"] = 2000,
                    ["steps"] = 100,
                    ["modifiers"] = new[] { "ctrl" }
                }
            });
            for (var i = 0; i < 9; i++)
            {
                steps.Add(new Dictionary<string, object?> { ["action"] = "click", ["ref"] = clickMeRef });
            }
            Assert.Equal(20, steps.Count);

            var batchTask = Task.Run(() => _f.Call("windows_batch", new Dictionary<string, object?>
            {
                ["steps"] = steps,
                ["window"] = winFormsHandle,
                ["wait_stable_between"] = false,
                ["return"] = "none"
            }, safety: true));

            // Wait until the drag has actually begun (left button physically held by the server),
            // not a blind delay: HeldInput is the production registry of held keys/buttons.
            var detect = Stopwatch.StartNew();
            while (HeldInput.Snapshot().buttons == 0)
            {
                Assert.True(detect.ElapsedMilliseconds < 15000, "The drag action never began (no held mouse button).");
                if (batchTask.IsCompleted)
                {
                    var early = await batchTask;
                    Assert.Fail($"The batch finished before the drag began: {early.RawText}");
                }
                await Task.Delay(10);
            }
            Assert.True(HeldInput.Snapshot().keys > 0, "The drag should hold its ctrl modifier alongside the button.");

            // Live native state while the action is in flight: poll until the button and modifier are
            // really down, so a tracking entry that precedes the physical down cannot race the assert.
            var nativeSw = Stopwatch.StartNew();
            while (!KeyDown(VkLButton) || !KeyDown(VkControl))
            {
                Assert.True(nativeSw.ElapsedMilliseconds < 2000, "The drag's button/modifier never became natively down.");
                await Task.Delay(5);
            }

            var killStart = Stopwatch.StartNew();
            _f.SafetyGateInstance.KillSwitch("test");
            var completed = await Task.WhenAny(batchTask, Task.Delay(5000));
            Assert.True(completed == batchTask, "The batch did not complete after the kill switch.");
            killStart.Stop();
            Assert.True(killStart.ElapsedMilliseconds < 200, $"The batch took {killStart.ElapsedMilliseconds} ms to abort after the kill switch (must be < 200 ms).");

            var result = await batchTask;
            Assert.True(result.IsError, $"Expected aborted, got: {result.RawText}");
            Assert.NotNull(result.Header);
            Assert.Equal("aborted", result.Header!["code"]?.ToString());

            // No later step ran: the abort happened inside the drag (step 10 of 20).
            var stepsRun = result.Header!["data"]?["steps_run"]?.GetValue<int>();
            Assert.True(stepsRun is > 0 and <= 11, $"Expected the batch to stop inside/after the drag step, ran {stepsRun} steps.");

            // Control grants revoked everywhere and the lock released.
            Assert.False(_f.SafetyState.ControlActive, "The control lock is still active.");
            Assert.Equal(AccessTier.View, _f.SafetyState.TierFor("WinFormsTestApp"));
            Assert.Equal(AccessTier.View, _f.SafetyState.TierFor("WpfTestApp"));

            // Held input really released: production registry empty and native keyboard/mouse state up.
            var (keys, buttons) = HeldInput.Snapshot();
            Assert.Equal(0, keys);
            Assert.Equal(0, buttons);
            Assert.False(KeyDown(VkLButton), "The left mouse button is still physically down after the kill switch.");
            Assert.False(KeyDown(VkControl), "The ctrl modifier is still physically down after the kill switch.");

            // No later action executed: none of the nine click steps touched the button, and the
            // aborted drag did not click "Click Me" either.
            Assert.Equal(clicksBefore, ClickCount(_f.TakeSnapshot(winFormsHandle)));
        }
        finally
        {
            try { HeldInput.ReleaseAll(); } catch { /* best effort */ }
            _f.SafetyState.RemoveGrant("WinFormsTestApp");
            _f.SafetyState.RemoveGrant("WpfTestApp");
            _f.SafetyState.ClearAbortIfStale(0);
        }
    }

    [Fact]
    public async Task UserInputPause_ClickReturnsUserActive_ThenResume()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        try
        {
            _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");
            _f.SafetyState.NoteUserInput("mouse");

            var click = await _f.Call("windows_click", new Dictionary<string, object?>
            {
                ["ref"] = TestAppFixture.FindRef(_f.TakeSnapshot(winFormsHandle), "Click Me", "button")
            }, safety: true);
            Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
            Assert.NotNull(click.Header);
            Assert.Equal("user_active", click.Header!["status"]?.ToString());

            var resume = await _f.Call("windows_resume", new { }, safety: true);
            Assert.False(resume.IsError, $"windows_resume failed: {resume.RawText}");

            var click2 = await _f.Call("windows_click", new Dictionary<string, object?>
            {
                ["ref"] = TestAppFixture.FindRef(_f.TakeSnapshot(winFormsHandle), "Click Me", "button")
            }, safety: true);
            Assert.False(click2.IsError, $"windows_click failed: {click2.RawText}");
        }
        finally
        {
            _f.SafetyState.RemoveGrant("WinFormsTestApp");
        }
    }

    [Fact]
    public async Task PasswordGuard_TypeIntoPassword_ReturnsPasswordField()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        try
        {
            _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");
            // The password field lives on the Forms tab; select it for real before snapshotting.
            var buttonsSnap = _f.TakeSnapshot(winFormsHandle);
            var formsTabRef = TestAppFixture.FindRef(buttonsSnap, "Forms", "tab");
            Assert.NotNull(formsTabRef);
            var selectTab = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = formsTabRef }, safety: true);
            Assert.False(selectTab.IsError, $"Selecting the Forms tab failed: {selectTab.RawText}");

            var snap = _f.TakeSnapshot(winFormsHandle);
            var passwordRef = TestAppFixture.FindRef(snap, "Password", "textbox");
            Assert.NotNull(passwordRef);
            var nameRef = TestAppFixture.FindRef(snap, "Name", "textbox");
            Assert.NotNull(nameRef);

            var type = await _f.Call("windows_type", new Dictionary<string, object?> { ["ref"] = passwordRef, ["text"] = "secret" }, safety: true);
            Assert.True(type.IsError, $"Expected password_field error, got: {type.RawText}");
            Assert.NotNull(type.Header);
            Assert.Equal("password_field", type.Header!["code"]?.ToString());

            var type2 = await _f.Call("windows_type", new Dictionary<string, object?> { ["ref"] = nameRef, ["text"] = "hello" }, safety: true);
            Assert.False(type2.IsError, $"windows_type failed: {type2.RawText}");
        }
        finally
        {
            // Leave the app on the Buttons tab so later tests see their default state.
            try
            {
                var snap = _f.TakeSnapshot(winFormsHandle);
                var buttonsTabRef = TestAppFixture.FindRef(snap, "Buttons", "tab");
                if (buttonsTabRef != null)
                {
                    await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = buttonsTabRef }, safety: true);
                }
            }
            catch
            {
                // best effort.
            }
            _f.SafetyState.RemoveGrant("WinFormsTestApp");
        }
    }

    [Fact]
    public async Task BlockedApp_SnapshotReturnsBlockedApp_ThenAllowBlockedApps()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        // AccessPolicy compiles its blocked lists at construction, so mutating the config list in a
        // test proves nothing. Instead rename the test window to a title the DEFAULT policy blocks
        // ("Windows Security") and restore the real title afterwards.
        var hwnd = _f.SafetySession.HwndForHandle(winFormsHandle)
            ?? throw new Exception("WinForms test window HWND not found.");
        try
        {
            _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");
            Assert.True(SetWindowTextW(hwnd, "Windows Security"), "SetWindowTextW failed.");

            var snap = await _f.Call("windows_snapshot", new { window = winFormsHandle }, safety: true);
            Assert.True(snap.IsError, $"Expected blocked_app, got: {snap.RawText}");
            Assert.NotNull(snap.Header);
            Assert.Equal("blocked_app", snap.Header!["code"]?.ToString());

            _f.SafetyConfig.AllowBlockedApps = true;
            var snap2 = await _f.Call("windows_snapshot", new { window = winFormsHandle }, safety: true);
            Assert.False(snap2.IsError, $"windows_snapshot failed: {snap2.RawText}");
        }
        finally
        {
            _f.SafetyConfig.AllowBlockedApps = false;
            SetWindowTextW(hwnd, WinFormsTitle);
            _f.SafetyState.RemoveGrant("WinFormsTestApp");
        }
    }

    [Fact]
    public async Task Drag_DestinationWithoutControl_IsDenied_BeforeAnyInput()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        var wpfHandle = _f.WpfHandle;
        Assert.NotEmpty(wpfHandle);

        try
        {
            // Source has control, destination only view: the drag must fail at the gate.
            _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");
            _f.SafetyState.AddGrant("WpfTestApp", AccessTier.View, false, "integration test", "test");
            var wpfSnap = _f.TakeSnapshot(wpfHandle);
            var wpfRef = TestAppFixture.FindRef(wpfSnap, "Option A");
            Assert.NotNull(wpfRef);
            var winSnap = _f.TakeSnapshot(winFormsHandle);
            var clicksBefore = ClickCount(winSnap);
            var srcRef = TestAppFixture.FindRef(winSnap, "Click Me", "button");
            Assert.NotNull(srcRef);

            var drag = await _f.Call("windows_drag", new Dictionary<string, object?>
            {
                ["from"] = new { @ref = srcRef },
                ["to"] = new { @ref = wpfRef }
            }, safety: true);
            Assert.True(drag.IsError, $"Expected access_denied for the ungranted destination, got: {drag.RawText}");
            Assert.NotNull(drag.Header);
            Assert.Equal("access_denied", drag.Header!["code"]?.ToString());
            Assert.Contains("WpfTestApp", drag.Header!["message"]?.ToString() ?? "");

            // No input was sent: nothing held, source app untouched.
            Assert.Equal((0, 0), HeldInput.Snapshot());
            Assert.Equal(clicksBefore, ClickCount(_f.TakeSnapshot(winFormsHandle)));

            // A blocked destination is refused the same way, before any input: rename the WPF
            // window to a title the DEFAULT policy blocks, grant it control so only the block
            // can refuse, and restore the title afterwards.
            var wpfHwnd = _f.SafetySession.HwndForHandle(wpfHandle)
                ?? throw new Exception("WPF test window HWND not found.");
            Assert.True(SetWindowTextW(wpfHwnd, "Windows Security"), "SetWindowTextW failed.");
            _f.SafetyState.AddGrant("WpfTestApp", AccessTier.Control, false, "integration test", "test");
            var drag2 = await _f.Call("windows_drag", new Dictionary<string, object?>
            {
                ["from"] = new { @ref = srcRef },
                ["to"] = new { @ref = wpfRef }
            }, safety: true);
            Assert.True(drag2.IsError, $"Expected blocked_app for the blocked destination, got: {drag2.RawText}");
            Assert.NotNull(drag2.Header);
            Assert.Equal("blocked_app", drag2.Header!["code"]?.ToString());
            Assert.Equal((0, 0), HeldInput.Snapshot());
            Assert.Equal(clicksBefore, ClickCount(_f.TakeSnapshot(winFormsHandle)));
        }
        finally
        {
            var wpfHwnd = _f.SafetySession.HwndForHandle(wpfHandle);
            if (wpfHwnd != null)
            {
                SetWindowTextW(wpfHwnd.Value, WpfTitle);
            }
            _f.SafetyState.RemoveGrant("WinFormsTestApp");
            _f.SafetyState.RemoveGrant("WpfTestApp");
        }
    }

    [Fact]
    public async Task Yolo_RequestAccessGrantsWithoutPrompter_AndKillStillWorks()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);

        // Run the YOLO bypass against the safety graph itself (its config is what carries the
        // default risky literal and the password guard) so the test proves the real bypasses.
        try
        {
            _f.SafetyConfig.Yolo = true;
            _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");
            // YOLO grants access without any prompter round-trip.
            var request = await _f.Call("windows_request_access", new Dictionary<string, object?>
            {
                ["apps"] = new[] { "WinFormsTestApp" },
                ["tier"] = "control",
                ["reason"] = "integration test"
            }, safety: true);
            Assert.NotNull(request.Header);
            Assert.Equal(true, (bool?)request.Header!["ok"]);

            // YOLO skips the risky-confirmation gate for the DEFAULT "delete permanently" literal —
            // and the click really executes (the test-only button only counts).
            var snap = _f.TakeSnapshot(winFormsHandle);
            var deletesBefore = DeleteCount(snap);
            var deleteRef = TestAppFixture.FindRef(snap, "Delete permanently", "button");
            Assert.NotNull(deleteRef);
            var click = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = deleteRef }, safety: true);
            Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
            Assert.NotNull(click.Header);
            Assert.NotEqual("needs_user_confirmation", click.Header!["status"]?.ToString());
            Assert.Equal(deletesBefore + 1, DeleteCount(_f.TakeSnapshot(winFormsHandle)));

            // YOLO disables the password guard.
            var formsTabRef = TestAppFixture.FindRef(_f.TakeSnapshot(winFormsHandle), "Forms", "tab");
            Assert.NotNull(formsTabRef);
            await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = formsTabRef }, safety: true);
            var passwordRef = TestAppFixture.FindRef(_f.TakeSnapshot(winFormsHandle), "Password", "textbox");
            Assert.NotNull(passwordRef);
            var type = await _f.Call("windows_type", new Dictionary<string, object?> { ["ref"] = passwordRef, ["text"] = "secret" }, safety: true);
            Assert.False(type.IsError, $"YOLO should bypass the password guard, got: {type.RawText}");

            // The kill switch still works under YOLO: abort state set, control lock dropped.
            _f.SafetyGateInstance.KillSwitch("test");
            Assert.True(_f.SafetyState.IsAborted, "The kill switch did not set the abort state.");
            Assert.Equal("test", _f.SafetyState.AbortReason);
            Assert.False(_f.SafetyState.ControlActive, "The control lock is still active after the kill switch.");
            Assert.Equal((0, 0), HeldInput.Snapshot());
        }
        finally
        {
            // Clear the abort before cleanup tool calls, then put the app back on the Buttons
            // tab so later tests see their default state regardless of xUnit run order.
            _f.SafetyState.ClearAbortIfStale(0);
            try
            {
                var snap = _f.TakeSnapshot(winFormsHandle);
                var buttonsTabRef = TestAppFixture.FindRef(snap, "Buttons", "tab");
                if (buttonsTabRef != null)
                {
                    await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = buttonsTabRef }, safety: true);
                }
            }
            catch
            {
                // best effort.
            }
            _f.SafetyConfig.Yolo = false;
            _f.SafetyState.RemoveGrant("WinFormsTestApp");
        }
    }

    [Fact]
    public async Task KillSwitch_DuringKeyHold_ReleasesMainKey()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        try
        {
            _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");
            // A single-letter chord has no modifiers, so HeldInput tracking exactly one key means
            // the main key itself is held (hold_ms keeps it down long enough to kill mid-hold).
            var keyTask = Task.Run(() => _f.Call("windows_key", new Dictionary<string, object?>
            {
                ["keys"] = "a",
                ["hold_ms"] = 2000,
                ["window"] = winFormsHandle
            }, safety: true));

            var detect = Stopwatch.StartNew();
            while (true)
            {
                var (keys, buttons) = HeldInput.Snapshot();
                if (keys == 1 && buttons == 0)
                {
                    break;
                }
                Assert.True(detect.ElapsedMilliseconds < 15000, "The key hold never began.");
                if (keyTask.IsCompleted)
                {
                    var early = await keyTask;
                    Assert.Fail($"The key call finished before the hold began: {early.RawText}");
                }
                await Task.Delay(10);
            }

            // Live native state: poll until the main chord key is really down, so a tracking entry
            // that precedes the physical down cannot race the assert.
            var nativeSw = Stopwatch.StartNew();
            while (!KeyDown(VkA))
            {
                Assert.True(nativeSw.ElapsedMilliseconds < 2000, "The main chord key never became natively down.");
                await Task.Delay(5);
            }

            var killStart = Stopwatch.StartNew();
            _f.SafetyGateInstance.KillSwitch("test");
            var completed = await Task.WhenAny(keyTask, Task.Delay(5000));
            Assert.True(completed == keyTask, "The key call did not complete after the kill switch.");
            killStart.Stop();
            Assert.True(killStart.ElapsedMilliseconds < 200, $"The key call took {killStart.ElapsedMilliseconds} ms to abort after the kill switch (must be < 200 ms).");

            var result = await keyTask;
            Assert.True(result.IsError, $"Expected aborted, got: {result.RawText}");
            Assert.NotNull(result.Header);
            Assert.Equal("aborted", result.Header!["code"]?.ToString());

            // The main key must not be left held: registry empty and native key state up.
            Assert.Equal((0, 0), HeldInput.Snapshot());
            Assert.False(KeyDown(VkA), "The main chord key is still natively down after the kill switch.");
        }
        finally
        {
            try { HeldInput.ReleaseAll(); } catch { /* best effort */ }
            _f.SafetyState.RemoveGrant("WinFormsTestApp");
            _f.SafetyState.ClearAbortIfStale(0);
        }
    }

    [Fact]
    public async Task DefaultRiskyLiteral_DeletePermanently_NeedsUserConfirmation()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        try
        {
            _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");
            var snap = _f.TakeSnapshot(winFormsHandle);
            var deletesBefore = DeleteCount(snap);
            var deleteRef = TestAppFixture.FindRef(snap, "Delete permanently", "button");
            Assert.NotNull(deleteRef);

            var click = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = deleteRef }, safety: true);
            Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
            Assert.NotNull(click.Header);
            Assert.Equal("needs_user_confirmation", click.Header!["status"]?.ToString());

            // The test-only button only counts; the default config literal must have blocked it.
            Assert.Equal(deletesBefore, DeleteCount(_f.TakeSnapshot(winFormsHandle)));
        }
        finally
        {
            _f.SafetyState.RemoveGrant("WinFormsTestApp");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_calc is { HasExited: false })
            {
                _calc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort.
        }
    }
}
