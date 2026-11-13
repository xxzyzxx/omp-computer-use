using System.Text.Json.Nodes;
using FlaUI.Mcp.IntegrationTests;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M5 — Explorer: create and rename a folder, open it and drag in a file from another
/// Explorer window, all in one batch. A source in a separate directory keeps its ref live
/// while Explorer recreates destination items during rename/navigation.
/// </summary>
[Collection("TestApps")]
public class M5_ExplorerBatchTests : IDisposable
{
    private readonly TestAppFixture _f;
    private string _tempDir = "";
    private readonly List<string> _windows = new();

    public M5_ExplorerBatchTests(TestAppFixture fixture) => _f = fixture;

    [Fact]
    public async Task Explorer_BatchCreateRenameAndDrag_MovesFile()
    {
        var id = Guid.NewGuid().ToString("N");
        _tempDir = Path.Combine(Path.GetTempPath(), "flaui-mcp-it-" + id);
        var source = Path.Combine(_tempDir, "Source-" + id);
        var destination = Path.Combine(_tempDir, "Target-" + id);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "a.txt"), "content-a");

        var sourceHandle = await LaunchExplorerAsync(source);
        var left = await _f.Call("windows_key", new { keys = "win+left", window = sourceHandle });
        Assert.False(left.IsError, left.RawText);
        await _f.Call("windows_key", new { keys = "escape", window = sourceHandle });

        var targetHandle = await LaunchExplorerAsync(destination);
        var right = await _f.Call("windows_key", new { keys = "win+right", window = targetHandle });
        Assert.False(right.IsError, right.RawText);
        await _f.Call("windows_key", new { keys = "escape", window = targetHandle });

        Assert.True(await _f.WaitForElementAsync(sourceHandle, "a.txt"), "source file did not appear");
        var sourceSnapshot = await _f.Call("windows_snapshot", new { window = sourceHandle });
        Assert.False(sourceSnapshot.IsError, sourceSnapshot.RawText);
        var fileRef = TestAppFixture.FindRef(sourceSnapshot.Untrusted, "a.txt", "listitem");
        Assert.NotNull(fileRef);

        var before = await _f.Call("windows_snapshot", new { window = targetHandle, interactive_only = false });
        Assert.False(before.IsError, before.RawText);
        var view = TestAppFixture.FindLine(before.Untrusted, "Items View");
        Assert.True(view != null, $"Explorer has no file-list surface: {before.Untrusted}");
        var bounds = TestAppFixture.Bounds(view!);
        Assert.NotNull(bounds);
        var shot = await _f.Call("windows_screenshot", new { window = targetHandle });
        Assert.False(shot.IsError, shot.RawText);
        var frame = _f.FunctionalServices.Frames.Last!;
        var drop = frame.FromPhysical(bounds.Value.x + bounds.Value.w * 3 / 4,
            bounds.Value.y + bounds.Value.h / 2);
        var generation = (int)before.Header!["generation"]!;

        var steps = new object[]
        {
            new { action = "key", keys = "ctrl+shift+n", window = targetHandle },
            new { action = "clipboard_write", text = "Draft", window = targetHandle },
            new { action = "key", keys = "ctrl+v", window = targetHandle },
            new { action = "key", keys = "enter", window = targetHandle },
            new { action = "key", keys = "f2", window = targetHandle },
            new { action = "type", text = "Dest2", window = targetHandle },
            new { action = "key", keys = "enter", window = targetHandle },
            new { action = "key", keys = "enter", window = targetHandle },
            new
            {
                action = "drag", from = new { @ref = fileRef },
                to = new { x = drop.X, y = drop.Y, image_id = frame.Id }
            },
            new { action = "wait", window = targetHandle, element = new { query = "a.txt" }, timeout_ms = 5000 }
        };
        var batch = await _f.Call("windows_batch", new Dictionary<string, object?>
        {
            ["window"] = targetHandle,
            ["steps"] = steps,
            ["return"] = "snapshot_diff"
        });
        Assert.False(batch.IsError, $"windows_batch failed: {batch.RawText}");
        var results = batch.Header!["results"]!.AsArray();
        Assert.All(results, result => Assert.True((bool?)result!["ok"] == true, result!.ToJsonString()));
        Assert.Equal(steps.Length, results.Count);
        Assert.Equal(generation, (int)batch.Header["final"]!["diff"]!["since"]!);
        Assert.Contains("a.txt", batch.Untrusted);

        var moved = Path.Combine(destination, "Dest2", "a.txt");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && !File.Exists(moved))
        {
            await Task.Delay(100);
        }
        Assert.True(File.Exists(moved), $"The batch did not move the file: {batch.RawText}");
        Assert.Equal("content-a", File.ReadAllText(moved));
        Assert.False(File.Exists(Path.Combine(source, "a.txt")), "drag copied instead of moving");
        Assert.False(Directory.Exists(Path.Combine(destination, "Draft")), "folder was not renamed");
    }

    private async Task<string> LaunchExplorerAsync(string path)
    {
        var launch = await _f.Call("windows_launch", new { app = "explorer.exe", args = new[] { path } });
        Assert.False(launch.IsError, launch.RawText);
        var info = await _f.WaitForWindowAsync(Path.GetFileName(path), processName: "explorer.exe");
        Assert.NotNull(info);
        _windows.Add(info.Handle);
        return info.Handle;
    }

    public void Dispose()
    {
        try
        {
            foreach (var window in _windows)
            {
                _f.Call("windows_close", new { window }).GetAwaiter().GetResult();
            }
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
