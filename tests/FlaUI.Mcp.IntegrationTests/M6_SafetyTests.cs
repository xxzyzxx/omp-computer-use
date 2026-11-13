using System.Diagnostics;
using System.Text.Json;
using FlaUI.Mcp.IntegrationTests;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Safety;
using PlaywrightWindows.Mcp.Tools;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M6 — Safety layer: access_denied, kill switch, user-input yield, risky confirmation,
/// password guard, blocked apps, and YOLO. Uses the safety-config fixture (yolo=false,
/// deny prompter, prompt timeout 1 s).
/// </summary>
[Collection("TestApps")]
public class M6_SafetyTests : IDisposable
{
    private readonly TestAppFixture _f;
    private Process? _calc;

    public M6_SafetyTests(TestAppFixture fixture) => _f = fixture;

    [Fact]
    public async Task UnGrantedApp_SnapshotReturnsAccessDenied_ThenGrant()
    {
        _calc = Process.Start(new ProcessStartInfo
        {
            FileName = "calc.exe",
            UseShellExecute = true
        }) ?? throw new Exception("Could not launch calc.exe");

        var info = await _f.WaitForWindowAsync("Calculator");
        Assert.NotNull(info);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle }, safety: true);
        Assert.True(snap.IsError, $"Expected access_denied, got: {snap.RawText}");
        Assert.NotNull(snap.Header);
        Assert.Equal("access_denied", snap.Header!["code"]?.ToString());
        var next = snap.Header!["next"]?.ToString() ?? "";
        Assert.Contains("windows_request_access", next);

        var request = await _f.Call("windows_request_access", new Dictionary<string, object?>
        {
            ["apps"] = new[] { "Calculator" },
            ["tier"] = "control",
            ["reason"] = "integration test"
        }, safety: true);
        Assert.NotNull(request.Header);
        Assert.Equal(false, (bool?)request.Header!["ok"]);

        // The deny prompter refused the request, so the test grants the tier directly.
        _f.SafetyState.AddGrant("Calculator", AccessTier.Control, false, "integration test", "test");
        var snap2 = await _f.Call("windows_snapshot", new { window = info!.Handle }, safety: true);
        Assert.False(snap2.IsError, $"windows_snapshot failed: {snap2.RawText}");
    }

    [Fact]
    public async Task KillSwitch_DuringBatch_AbortsAndRevokesControl()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");

        var steps = new System.Collections.Generic.List<Dictionary<string, object?>>();
        for (var i = 0; i < 20; i++)
        {
            steps.Add(new Dictionary<string, object?> { ["tool"] = "windows_wait", ["ms"] = 400 });
        }

        var batchTask = Task.Run(() =>
            _f.Call("windows_batch", new Dictionary<string, object?> { ["steps"] = steps }, safety: true));

        await Task.Delay(300);
        var killStart = Stopwatch.StartNew();
        _f.SafetyGateInstance.KillSwitch("test");
        var completed = await Task.WhenAny(batchTask, Task.Delay(2000));
        Assert.True(completed == batchTask, "The batch did not complete within 200 ms of the kill switch.");
        killStart.Stop();
        Assert.True(killStart.ElapsedMilliseconds < 500, $"The batch took {killStart.ElapsedMilliseconds} ms to die after the kill switch.");

        var result = await batchTask;
        Assert.True(result.IsError, $"Expected aborted, got: {result.RawText}");
        Assert.NotNull(result.Header);
        Assert.Equal("aborted", result.Header!["code"]?.ToString());

        Assert.False(_f.SafetyState.ControlActive, "The control lock is still active.");
        Assert.Equal(AccessTier.View, _f.SafetyState.TierFor("WinFormsTestApp"));
    }

    [Fact]
    public async Task UserInputPause_ClickReturnsUserActive_ThenResume()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
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

    [Fact]
    public async Task RiskyConfirmation_ClickMeButton_NeedsUserConfirmation()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        _f.SafetyConfig.RiskyPatterns.Add(@"\bclick me\b");
        _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");

        var snap = _f.TakeSnapshot(winFormsHandle);
        var clickMeRef = TestAppFixture.FindRef(snap, "Click Me", "button");
        Assert.NotNull(clickMeRef);

        var click = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = clickMeRef }, safety: true);
        Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
        Assert.NotNull(click.Header);
        Assert.Equal("needs_user_confirmation", click.Header!["status"]?.ToString());

        // The button is not invoked.
        var snap2 = _f.TakeSnapshot(winFormsHandle);
        Assert.Contains("Click count: 0", snap2);

        _f.SafetyConfig.RiskyPatterns.Remove(@"\bclick me\b");
    }

    [Fact]
    public async Task PasswordGuard_TypeIntoPassword_ReturnsPasswordField()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");

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

    [Fact]
    public async Task BlockedApp_SnapshotReturnsBlockedApp_ThenAllowBlockedApps()
    {
        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        _f.SafetyConfig.BlockedApps.Add("WinFormsTestApp.exe");
        _f.SafetyState.AddGrant("WinFormsTestApp", AccessTier.Control, false, "integration test", "test");

        var snap = await _f.Call("windows_snapshot", new { window = winFormsHandle }, safety: true);
        Assert.True(snap.IsError, $"Expected blocked_app, got: {snap.RawText}");
        Assert.NotNull(snap.Header);
        Assert.Equal("blocked_app", snap.Header!["code"]?.ToString());

        _f.SafetyConfig.AllowBlockedApps = true;
        var snap2 = await _f.Call("windows_snapshot", new { window = winFormsHandle }, safety: true);
        Assert.False(snap2.IsError, $"windows_snapshot failed: {snap2.RawText}");

        _f.SafetyConfig.AllowBlockedApps = false;
        _f.SafetyConfig.BlockedApps.Remove("WinFormsTestApp.exe");
    }

    [Fact]
    public async Task Yolo_RequestAccessGrantsWithoutPrompter()
    {
        var request = await _f.Call("windows_request_access", new Dictionary<string, object?>
        {
            ["apps"] = new[] { "Calculator" },
            ["tier"] = "control",
            ["reason"] = "integration test"
        }, safety: false);
        Assert.NotNull(request.Header);
        Assert.Equal(true, (bool?)request.Header!["ok"]);

        var winFormsHandle = _f.WinFormsHandle;
        Assert.NotEmpty(winFormsHandle);
        _f.SafetyConfig.RiskyPatterns.Add(@"\bclick me\b");
        var snap = _f.TakeSnapshot(winFormsHandle);
        var clickMeRef = TestAppFixture.FindRef(snap, "Click Me", "button");
        Assert.NotNull(clickMeRef);

        var click = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = clickMeRef }, safety: false);
        Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
        Assert.NotNull(click.Header);
        Assert.NotEqual("needs_user_confirmation", click.Header!["status"]?.ToString());

        _f.SafetyConfig.RiskyPatterns.Remove(@"\bclick me\b");
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
