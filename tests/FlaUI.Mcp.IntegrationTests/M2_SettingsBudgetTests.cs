using System.Diagnostics;
using FlaUI.Mcp.IntegrationTests;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M2 — Settings budget: launch the Settings app, snapshot with defaults, assert the
/// untrusted text is bounded by SnapshotMaxChars and the header reports nodes/elapsed_ms.
/// Snapshot with a filter returns fewer nodes.
/// </summary>
[Collection("TestApps")]
public class M2_SettingsBudgetTests : IDisposable
{
    private readonly TestAppFixture _f;
    private Process? _settings;

    public M2_SettingsBudgetTests(TestAppFixture fixture) => _f = fixture;

    [Fact]
    public async Task Settings_SnapshotDefaults_Budget()
    {
        _settings = Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:",
            UseShellExecute = true
        }) ?? throw new Exception("Could not launch ms-settings:");

        var info = await _f.WaitForWindowAsync("Settings");
        Assert.NotNull(info);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");
        Assert.NotNull(snap.Header);
        Assert.NotNull(snap.Untrusted);

        // Assert the untrusted text is bounded.
        var maxChars = (int?)_f.FunctionalConfig.SnapshotMaxChars ?? 24000;
        Assert.True(
            snap.Untrusted!.Length <= maxChars,
            $"Snapshot text is too long: {snap.Untrusted.Length} > {maxChars}");

        var nodes = (int?)snap.Header!["nodes"];
        Assert.True(nodes > 20, $"Snapshot nodes: {nodes} is too small.");

        var elapsed = (int?)snap.Header!["elapsed_ms"];
        Assert.True(elapsed < 5000, $"Snapshot elapsed_ms: {elapsed} is too slow.");
    }

    [Fact]
    public async Task Settings_SnapshotFilteredBluetooth_FewerNodes()
    {
        _settings = Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:",
            UseShellExecute = true
        }) ?? throw new Exception("Could not launch ms-settings:");

        var info = await _f.WaitForWindowAsync("Settings");
        Assert.NotNull(info);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");
        var nodesAll = (int?)snap.Header!["nodes"];

        var filtered = await _f.Call("windows_snapshot", new Dictionary<string, object?>
        {
            ["window"] = info!.Handle,
            ["filter"] = "Bluetooth"
        });
        Assert.False(filtered.IsError, $"windows_snapshot failed: {filtered.RawText}");
        var nodesFiltered = (int?)filtered.Header!["nodes"];

        Assert.NotNull(nodesAll);
        Assert.NotNull(nodesFiltered);
        Assert.True(nodesFiltered < nodesAll, $"Filtered nodes: {nodesFiltered} is not fewer than {nodesAll}.");
    }

    public void Dispose()
    {
        try
        {
            if (_settings is { HasExited: false })
            {
                _settings.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort.
        }
    }
}
