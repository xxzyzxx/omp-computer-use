using System.Diagnostics;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M2 — Settings app snapshot budget: a complex XAML app's snapshot stays within the configured
/// character budget, is fast, and filter= narrows it.
/// </summary>
[Collection("TestApps")]
public class M2_SettingsBudgetTests : IDisposable
{
    private readonly TestAppFixture _f;
    private uint _settingsPid;

    public M2_SettingsBudgetTests(TestAppFixture fixture) => _f = fixture;

    [Fact]
    public async Task Settings_SnapshotDefaults_Budget()
    {
        var handle = await LaunchSettingsAsync();

        var snap = await _f.Call("windows_snapshot", new { window = handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");
        Assert.NotNull(snap.Header);
        Assert.NotNull(snap.Untrusted);

        // The untrusted text is bounded by the configured budget (truncation is reported in the header).
        var maxChars = _f.FunctionalConfig.SnapshotMaxChars;
        Assert.True(snap.Untrusted!.Length <= maxChars, $"Snapshot text is too long: {snap.Untrusted.Length} > {maxChars}");

        var nodes = (int?)snap.Header!["nodes"];
        Assert.True(nodes > 20, $"Snapshot nodes: {nodes} is too small.");

        var elapsed = (int?)snap.Header!["elapsed_ms"];
        Assert.True(elapsed < 5000, $"Snapshot elapsed_ms: {elapsed} is too slow.");
    }

    [Fact]
    public async Task Settings_SnapshotFilteredBluetooth_FewerNodes()
    {
        var handle = await LaunchSettingsAsync();

        var snap = await _f.Call("windows_snapshot", new { window = handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");
        var nodesAll = (int?)snap.Header!["nodes"];

        var filtered = await _f.Call("windows_snapshot", new Dictionary<string, object?>
        {
            ["window"] = handle,
            ["filter"] = "Bluetooth"
        });
        Assert.False(filtered.IsError, $"windows_snapshot failed: {filtered.RawText}");
        var nodesFiltered = (int?)filtered.Header!["nodes"];
        Assert.Equal(true, (bool?)filtered.Header!["filtered"]);

        Assert.NotNull(nodesAll);
        Assert.NotNull(nodesFiltered);
        Assert.True(nodesFiltered < nodesAll, $"Filtered nodes: {nodesFiltered} is not fewer than {nodesAll}.");
    }

    /// <summary>ms-settings: is a protocol launch (no process handle), so the server's launch + window diff is used.</summary>
    private async Task<string> LaunchSettingsAsync()
    {
        var launch = await _f.Call("windows_launch", new { app = "ms-settings:" });
        Assert.False(launch.IsError, $"windows_launch failed: {launch.RawText}");
        var handle = launch.Header!["window"]!["handle"]!.GetValue<string>();
        var info = _f.FunctionalSession.ResolveWindow(handle);
        Assert.Equal("SystemSettings.exe", info.ProcessName);
        _settingsPid = info.Pid;
        // Home page content (the search box) marks the end of the splash phase.
        Assert.True(await _f.WaitForElementAsync(handle, "Find a setting"), "Settings content did not load");
        return handle;
    }

    public void Dispose()
    {
        if (_settingsPid == 0) return;
        try
        {
            var p = Process.GetProcessById((int)_settingsPid);
            p.Kill();
            p.WaitForExit(5000);
        }
        catch
        {
            // Already gone.
        }
    }
}
