using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M4 — Set-of-marks: a Settings toggle switch is flipped by mark number and the snapshot diff proves it;
/// an Electron app (VSCodium / VS Code) with a thin UIA tree is operated through marks.
/// </summary>
[Collection("TestApps")]
public class M4_MarksTests : IDisposable
{
    private readonly TestAppFixture _f;
    private uint _settingsPid;
    private Process? _editor;

    public M4_MarksTests(TestAppFixture fixture) => _f = fixture;

    [Fact]
    public async Task Settings_ClipboardHistoryToggle_ByMark_ChangesState()
    {
        // Settings > Clipboard: the "Clipboard history" switch is above the fold and harmless to flip twice.
        var launch = await _f.Call("windows_launch", new { app = "ms-settings:clipboard" });
        Assert.False(launch.IsError, $"windows_launch failed: {launch.RawText}");
        var handle = launch.Header!["window"]!["handle"]!.GetValue<string>();
        _settingsPid = _f.FunctionalSession.ResolveWindow(handle).Pid;
        Assert.True(await _f.WaitForElementAsync(handle, "Clipboard history"), "Clipboard page did not load");

        var snap = await _f.Call("windows_snapshot", new { window = handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");
        var toggleLine = TestAppFixture.FindLine(snap.Untrusted, "Clipboard history", "button");
        Assert.NotNull(toggleLine);
        Assert.Contains("{tog", toggleLine!);
        var toggleRef = TestAppFixture.FindRef(toggleLine)!;
        var wasChecked = toggleLine.Contains("[checked]");
        Assert.True(wasChecked || toggleLine.Contains("[unchecked]"), $"toggle line has no state: {toggleLine}");
        var generation = (int)snap.Header!["generation"]!;

        var shot = await _f.Call("windows_screenshot", new Dictionary<string, object?> { ["window"] = handle, ["marks"] = true });
        Assert.False(shot.IsError, $"windows_screenshot failed: {shot.RawText}");
        Assert.NotNull(shot.Png);
        var marks = shot.Header!["marks"];
        Assert.NotNull(marks);
        var count = (int)marks!["count"]!;
        Assert.True(count > 0, "no marks were drawn");
        var map = marks["map"] as JsonObject;
        Assert.NotNull(map);
        Assert.All(map!, kv => Assert.Matches(@"^w\d+e\d+$", kv.Value!.GetValue<string>()));
        var markNum = FindMarkNumber(map, toggleRef);
        Assert.NotNull(markNum);

        var click = await _f.Call("windows_click", new Dictionary<string, object?> { ["mark"] = markNum });
        Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
        await _f.Call("windows_wait", new { window = handle, stable = true, timeout_ms = 3000 });

        var diff = await _f.Call("windows_snapshot", new { window = handle, diff_since = generation });
        Assert.False(diff.IsError, $"windows_snapshot failed: {diff.RawText}");
        var changed = TestAppFixture.FindLine(diff.Untrusted, "Clipboard history", "button");
        Assert.NotNull(changed);
        Assert.StartsWith("~", changed!.TrimStart());
        Assert.Equal(!wasChecked, changed.Contains("[checked]"));

        // Restore the original state through the same mark (refs survive across snapshots).
        var restore = await _f.Call("windows_click", new Dictionary<string, object?> { ["mark"] = markNum });
        Assert.False(restore.IsError, $"windows_click failed: {restore.RawText}");
        await _f.Call("windows_wait", new { window = handle, stable = true, timeout_ms = 3000 });
        var after = await _f.Call("windows_snapshot", new { window = handle });
        var restored = TestAppFixture.FindLine(after.Untrusted, "Clipboard history", "button");
        Assert.NotNull(restored);
        Assert.Equal(wasChecked, restored!.Contains("[checked]"));
    }

    [Fact]
    public async Task Electron_ThinTree_MarksClick_ChangesScreen()
    {
        var editorPath = FindEditorPath();
        if (editorPath == null)
        {
            Assert.Fail("SKIP: neither VSCodium nor VS Code is installed; the Electron marks scenario cannot run here.");
        }
        var exe = Path.GetFileName(editorPath);
        var baseArgs = new[] { "--new-window", "--disable-workspace-trust", "--disable-extensions" };

        // Phase 1: default launch. Chromium exposes no renderer accessibility, so the tree is thin and the
        // model is told to work by marks; the marks that do exist (caption buttons) must still work.
        var handle = await LaunchEditorAsync(editorPath, exe, baseArgs);
        var snap = await _f.Call("windows_snapshot", new { window = handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");
        var interactive = (int)snap.Header!["interactive_nodes"]!;
        Assert.True(interactive >= 10 || snap.Header["thin_tree"] != null, $"thin tree ({interactive} interactive nodes) without a thin_tree hint");

        var shot = await _f.Call("windows_screenshot", new Dictionary<string, object?> { ["window"] = handle, ["marks"] = true });
        Assert.False(shot.IsError, $"windows_screenshot failed: {shot.RawText}");
        Assert.NotNull(shot.Png);
        var map = shot.Header!["marks"]?["map"] as JsonObject;
        Assert.NotNull(map);
        Assert.True(map!.Count > 0, "no marks were drawn for the Electron window");
        var maximize = MarkFor(map, snap.Untrusted, "Maximize", "button");
        Assert.NotNull(maximize);

        var click = await _f.Call("windows_click", new Dictionary<string, object?> { ["mark"] = maximize });
        Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
        await _f.Call("windows_wait", new { window = handle, stable = true, timeout_ms = 3000 });
        var shot2 = await _f.Call("windows_screenshot", new Dictionary<string, object?> { ["window"] = handle });
        Assert.False(shot2.IsError, $"windows_screenshot failed: {shot2.RawText}");
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(shot.Png!)), Convert.ToHexString(SHA256.HashData(shot2.Png!)));
        await CloseEditorAsync(handle);

        // Phase 2: the documented remedy exposes the full tree; the File menu is then reachable by mark.
        handle = await LaunchEditorAsync(editorPath, exe, baseArgs.Append("--force-renderer-accessibility").ToArray());
        var fullSnap = await _f.Call("windows_snapshot", new { window = handle });
        Assert.False(fullSnap.IsError, $"windows_snapshot failed: {fullSnap.RawText}");
        Assert.True((int)fullSnap.Header!["interactive_nodes"]! >= 10, "renderer accessibility did not expose the tree");
        Assert.Null(fullSnap.Header["thin_tree"]);

        var fullShot = await _f.Call("windows_screenshot", new Dictionary<string, object?> { ["window"] = handle, ["marks"] = true });
        Assert.False(fullShot.IsError, $"windows_screenshot failed: {fullShot.RawText}");
        var fullMap = fullShot.Header!["marks"]?["map"] as JsonObject;
        Assert.NotNull(fullMap);
        var fileMenu = MarkFor(fullMap!, fullSnap.Untrusted, "\"File\"", "menuitem");
        Assert.NotNull(fileMenu);
        var open = await _f.Call("windows_click", new Dictionary<string, object?> { ["mark"] = fileMenu });
        Assert.False(open.IsError, $"windows_click failed: {open.RawText}");
        await _f.Call("windows_wait", new { window = handle, stable = true, timeout_ms = 3000 });
        var menuSnap = await _f.Call("windows_snapshot", new { window = handle, filter = "New" });
        Assert.False(menuSnap.IsError, $"windows_snapshot failed: {menuSnap.RawText}");
        Assert.NotNull(TestAppFixture.FindLine(menuSnap.Untrusted, "New", "menuitem"));
        await _f.Call("windows_key", new { keys = "escape", window = handle });
        await CloseEditorAsync(handle);
    }

    private async Task<string> LaunchEditorAsync(string editorPath, string exe, string[] args)
    {
        var launch = await _f.Call("windows_launch", new Dictionary<string, object?>
        {
            ["app"] = editorPath,
            ["args"] = args,
            ["timeout_ms"] = 30000
        });
        Assert.False(launch.IsError, $"windows_launch failed: {launch.RawText}");
        var handle = launch.Header!["window"]!["handle"]!.GetValue<string>();
        var info = _f.FunctionalSession.ResolveWindow(handle);
        Assert.Equal(exe, info.ProcessName);
        _editor = Process.GetProcessById((int)info.Pid);
        await _f.Call("windows_wait", new { window = handle, stable = true, timeout_ms = 10000 });
        return handle;
    }

    private async Task CloseEditorAsync(string handle)
    {
        var hwnd = _f.FunctionalSession.ResolveWindow(handle).Hwnd;
        var close = await _f.Call("windows_close", new { window = handle });
        Assert.False(close.IsError, $"windows_close failed: {close.RawText}");
        Assert.True(await _f.WaitForWindowGoneAsync(hwnd), "editor window did not close");
        try { _editor?.WaitForExit(5000); } catch { /* best effort */ }
    }

    private static int? MarkFor(JsonObject map, string? tree, string nameSubstring, string role)
    {
        var line = TestAppFixture.FindLine(tree, nameSubstring, role);
        var refId = line == null ? null : TestAppFixture.FindRef(line);
        return refId == null ? null : FindMarkNumber(map, refId);
    }

    private static string? FindEditorPath()
    {
        var candidates = new[]
        {
            @"C:\Program Files\VSCodium\VSCodium.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static int? FindMarkNumber(JsonObject map, string refId)
    {
        foreach (var kv in map)
        {
            if (kv.Value?.GetValue<string>() == refId && int.TryParse(kv.Key, out var n)) return n;
        }
        return null;
    }

    public void Dispose()
    {
        if (_settingsPid != 0)
        {
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
        try
        {
            if (_editor is { HasExited: false })
            {
                _editor.Kill(entireProcessTree: true);
                _editor.WaitForExit(5000);
            }
        }
        catch
        {
            // Already gone.
        }
    }
}
