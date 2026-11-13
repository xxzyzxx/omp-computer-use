using System.Diagnostics;
using FlaUI.Mcp.IntegrationTests;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M1 — Calculator flow: launch, snapshot, click refs, get_text. Also asserts stale-ref
/// behaviour after the target window is closed and relaunched.
/// </summary>
[Collection("TestApps")]
public class M1_CalculatorTests : IDisposable
{
    private readonly TestAppFixture _f;
    private Process? _calc;

    public M1_CalculatorTests(TestAppFixture fixture) => _f = fixture;

    [Fact]
    public async Task Calculator_Multiply_Three_By_Three_Equals_Nine()
    {
        _calc = LaunchCalc();

        var launch = await _f.Call("windows_launch", new { app = "calc.exe" });
        Assert.False(launch.IsError, $"windows_launch failed: {launch.RawText}");

        var info = await _f.WaitForWindowAsync("Calculator");
        Assert.NotNull(info);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");

        var three = TestAppFixture.FindRef(snap.Untrusted, "Three", "button");
        var multiply = TestAppFixture.FindRef(snap.Untrusted, "Multiply by", "button");
        Assert.NotNull(three);
        Assert.NotNull(multiply);

        foreach (var click in new[] { three, multiply, three })
        {
            var r = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = click });
            Assert.False(r.IsError, $"windows_click failed: {r.RawText}");
        }

        // Find the equals button; the Windows Calculator exposes it as "Equals" (or "=" fallback).
        var equals = TestAppFixture.FindRef(snap.Untrusted, "Equals", "button");
        if (equals == null)
        {
            var find = await _f.Call("windows_find", new { window = info!.Handle, query = "Equals" });
            Assert.False(find.IsError, $"windows_find failed: {find.RawText}");
            equals = TestAppFixture.FindRef(find.Untrusted, "Equals", "button");
        }
        Assert.NotNull(equals);

        var clickEquals = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = equals });
        Assert.False(clickEquals.IsError, $"windows_click failed: {clickEquals.RawText}");

        var wait = await _f.Call("windows_wait", new { window = info!.Handle, stable = true });
        Assert.False(wait.IsError, $"windows_wait failed: {wait.RawText}");

        // Assert the display shows 9.
        var snap2 = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap2.IsError, $"windows_snapshot failed: {snap2.RawText}");
        var displayLine = TestAppFixture.FindLine(snap2.Untrusted, "Display is");
        Assert.NotNull(displayLine);
        Assert.Contains("9", displayLine!);
    }

    [Fact]
    public async Task Calculator_StaleRef_AfterCloseAndRelaunch_ReturnsStaleRef()
    {
        _calc = LaunchCalc();
        var info = await _f.WaitForWindowAsync("Calculator");
        Assert.NotNull(info);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");
        var anyRef = TestAppFixture.FindRef(snap.Untrusted, "Seven", "button");
        Assert.NotNull(anyRef);

        KillCalc();
        _calc = LaunchCalc();
        var info2 = await _f.WaitForWindowAsync("Calculator");
        Assert.NotNull(info2);
        Assert.NotEqual(info!.Handle, info2!.Handle);

        var click = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = anyRef });
        Assert.True(click.IsError, $"Expected stale_ref error, got: {click.RawText}");
        Assert.NotNull(click.Header);
        Assert.Equal("stale_ref", click.Header!["code"]?.ToString());
    }

    private static Process LaunchCalc()
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = "calc.exe",
            UseShellExecute = true
        }) ?? throw new Exception("Could not launch calc.exe");
    }

    private void KillCalc()
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

    public void Dispose()
    {
        KillCalc();
    }
}
