using System.Text.Json.Nodes;
using FlaUI.Mcp.IntegrationTests;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M5 — Explorer batch: launch Explorer on a temp dir, rename the folder through the
/// batch, drag a.txt into it, and assert on disk that the file moved. The batch result
/// must list every step with ok.
/// </summary>
[Collection("TestApps")]
public class M5_ExplorerBatchTests : IDisposable
{
    private readonly TestAppFixture _f;
    private string _tempDir = "";

    public M5_ExplorerBatchTests(TestAppFixture fixture) => _f = fixture;

    [Fact]
    public async Task Explorer_BatchRenameAndDrag_MovesFile()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "flaui-mcp-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "content-a");
        Directory.CreateDirectory(Path.Combine(_tempDir, "Dest"));

        var launch = await _f.Call("windows_launch", new Dictionary<string, object?>
        {
            ["app"] = "explorer.exe",
            ["args"] = new[] { _tempDir }
        });
        Assert.False(launch.IsError, $"windows_launch failed: {launch.RawText}");

        var dirName = Path.GetFileName(_tempDir);
        var info = await _f.WaitForWindowAsync(dirName);
        Assert.NotNull(info);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");

        var destRef = TestAppFixture.FindRef(snap.Untrusted, "Dest");
        Assert.NotNull(destRef);
        var aTxtRef = TestAppFixture.FindRef(snap.Untrusted, "a.txt");
        Assert.NotNull(aTxtRef);

        var steps = new[]
        {
            new Dictionary<string, object?> { ["tool"] = "windows_click", ["ref"] = destRef },
            new Dictionary<string, object?> { ["tool"] = "windows_key", ["keys"] = "f2" },
            new Dictionary<string, object?> { ["tool"] = "windows_type", ["text"] = "Dest2" },
            new Dictionary<string, object?> { ["tool"] = "windows_key", ["keys"] = "enter" },
            new Dictionary<string, object?> { ["tool"] = "windows_wait", ["window"] = info!.Handle, ["stable"] = true },
            new Dictionary<string, object?> { ["tool"] = "windows_drag", ["from"] = aTxtRef, ["to"] = destRef },
        };
        var batch = await _f.Call("windows_batch", new Dictionary<string, object?>
        {
            ["steps"] = steps,
            ["return"] = "snapshot_diff"
        });
        Assert.False(batch.IsError, $"windows_batch failed: {batch.RawText}");
        Assert.NotNull(batch.Header);

        var results = batch.Header!["results"] as JsonArray;
        Assert.NotNull(results);
        Assert.True(results!.Count == steps.Length, $"Batch results: {results.Count} is not {steps.Length}.");
        foreach (var result in results)
        {
            var ok = (bool?)result!["ok"];
            Assert.True(ok == true, $"Batch step failed: {result!.ToJsonString()}");
        }

        // Assert on disk that Dest2\a.txt exists within 5 s.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var moved = Path.Combine(_tempDir, "Dest2", "a.txt");
        while (sw.ElapsedMilliseconds < 5000 && !File.Exists(moved))
        {
            await Task.Delay(200);
        }
        Assert.True(File.Exists(moved), $"The file {moved} did not exist after 5 s.");

        // Close Explorer.
        var close = await _f.Call("windows_close", new { window = info!.Handle });
        Assert.False(close.IsError, $"windows_close failed: {close.RawText}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best effort.
        }
    }
}
