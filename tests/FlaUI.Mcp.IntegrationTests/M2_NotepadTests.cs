using System.Diagnostics;
using System.Text.Json;
using FlaUI.Mcp.IntegrationTests;
using PlaywrightWindows.Mcp.Core;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M2 — Notepad flow: launch a unique empty source file in Notepad, type text into that owned
/// document, save it via the File menu's "Save as" to a separate unique target path, and assert
/// the file exists with the expected content. Closes only the owned document tab afterwards;
/// shared/user Notepad instances are never killed.
/// </summary>
[Collection("TestApps")]
public class M2_NotepadTests : IDisposable
{
    private const string TestContent = "hello from mcp";

    private readonly TestAppFixture _f;
    private readonly string _sourceFile;
    private readonly string _targetFile;
    private string? _ownedHandle;

    public M2_NotepadTests(TestAppFixture fixture)
    {
        _f = fixture;
        var id = Guid.NewGuid().ToString("N");
        _sourceFile = Path.Combine(Path.GetTempPath(), "flaui-mcp-m2-src-" + id + ".txt");
        _targetFile = Path.Combine(Path.GetTempPath(), "flaui-mcp-m2-dst-" + id + ".txt");
    }

    [Fact]
    public async Task Notepad_TypeAndSave_WritesFile()
    {
        // Unique empty source fixture: Notepad opens this document, so typed text can never land
        // in a restored user document from a previous session.
        File.WriteAllText(_sourceFile, string.Empty);

        var launch = await _f.Call("windows_launch", new { app = "notepad.exe", args = new[] { _sourceFile } });
        Assert.False(launch.IsError, $"windows_launch failed: {launch.RawText}");

        // Resolve the window by the unique source filename so we own exactly this document,
        // even when an existing Notepad instance hosts it as a new tab.
        var win = await WaitForOwnedWindowAsync(Path.GetFileName(_sourceFile));
        Assert.NotNull(win);
        _ownedHandle = win!.Handle;
        var info = _f.FunctionalSession.ResolveWindow(_ownedHandle);
        Assert.Contains("Notepad", info.ProcessName, StringComparison.OrdinalIgnoreCase);

        var snap = await _f.Call("windows_snapshot", new { window = _ownedHandle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");

        // Windows 11 Notepad exposes the editor as a document named "Text editor"; classic Notepad as an edit box.
        var docRef = TestAppFixture.FindRef(snap.Untrusted, "Text editor")
                    ?? TestAppFixture.FindRef(snap.Untrusted, "Text", "textbox");
        Assert.NotNull(docRef);

        var type = await _f.Call("windows_type", new Dictionary<string, object?> { ["ref"] = docRef, ["text"] = TestContent });
        Assert.False(type.IsError, $"windows_type failed: {type.RawText}");

        // Save through the File menu (acceptance requirement): click the File menu item by live ref,
        // falling back to Alt+F only when the UIA tree exposes no File item.
        await SaveViaFileMenuAsync();

        // Wait for the Save As dialog (an owned window of the Notepad process).
        var dialog = await _f.WaitForWindowAsync("Save as", processName: "Notepad.exe");
        Assert.NotNull(dialog);

        // Discover the filename field by UIA, then use native input. The common dialog can render
        // a ValuePattern-set path while still committing its old filename.
        var dialogSnap = await _f.Call("windows_snapshot", new { window = dialog!.Handle });
        Assert.False(dialogSnap.IsError, $"windows_snapshot failed: {dialogSnap.RawText}");
        var fileNameRef = TestAppFixture.FindRef(dialogSnap.Untrusted, "File name", "textbox");
        Assert.NotNull(fileNameRef);

        var select = await _f.Call("windows_key", new { @ref = fileNameRef, keys = "ctrl+a" });
        Assert.False(select.IsError, $"windows_key (select filename) failed: {select.RawText}");
        var filename = await _f.Call("windows_type", new Dictionary<string, object?> { ["ref"] = fileNameRef, ["text"] = _targetFile });
        Assert.False(filename.IsError, $"windows_type (filename) failed: {filename.RawText}");

        // Confirm from the focused filename field.
        var enter = await _f.Call("windows_key", new { keys = "enter", @ref = fileNameRef });
        Assert.False(enter.IsError, $"windows_key failed: {enter.RawText}");

        // Wait for the dialog to be gone.
        var gone = await _f.WaitForWindowGoneAsync(dialog.Hwnd, 20000);
        Assert.True(gone, "Save As dialog did not close");

        // Assert the file exists with the exact expected content.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && !File.Exists(_targetFile))
        {
            await Task.Delay(200);
        }
        Assert.True(File.Exists(_targetFile), $"The file was not written: {_targetFile}");
        AssertContentMatches(_targetFile, TestContent);
    }

    /// <summary>
    /// Poll top-level windows until the one whose title contains the unique source filename appears,
    /// and return it. Never matches a pre-existing user document because the name is GUID-unique.
    /// </summary>
    private async Task<WindowInfo?> WaitForOwnedWindowAsync(string sourceName)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            var info = _f.SafetySession.ListTopLevelWindows().FirstOrDefault(
                w => w.Title != null && w.Title.Contains(sourceName, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(w.ProcessName, "Notepad.exe", StringComparison.OrdinalIgnoreCase));
            if (info != null) return info;
            await Task.Delay(250);
        }
        return null;
    }

    /// <summary>
    /// Open the File menu by its live menu ref and click the "Save as" item by live ref.
    /// Alt+F is only used when the UIA tree has no File item; the Save action itself always goes
    /// through the File menu (never a bare Ctrl+S).
    /// </summary>
    private async Task SaveViaFileMenuAsync()
    {
        var snap = await _f.Call("windows_snapshot", new { window = _ownedHandle });
        Assert.False(snap.IsError, $"windows_snapshot (menu) failed: {snap.RawText}");

        var fileRef = TestAppFixture.FindRef(snap.Untrusted, "File", "menubaritem")
                    ?? TestAppFixture.FindRef(snap.Untrusted, "File", "menuitem")
                    ?? TestAppFixture.FindRef(snap.Untrusted, "File", "menu");
        if (fileRef != null)
        {
            var click = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = fileRef });
            Assert.False(click.IsError, $"windows_click (File menu) failed: {click.RawText}");
        }
        else
        {
            // UIA exposes no File item here: keyboard access to the File menu instead.
            var key = await _f.Call("windows_key", new { keys = "alt+f", window = _ownedHandle });
            Assert.False(key.IsError, $"windows_key (Alt+F) failed: {key.RawText}");
        }

        Assert.True(await _f.WaitForElementAsync(_ownedHandle!, "Save as", timeoutMs: 5000),
            "The File menu did not expose its Save as item.");
        // Fresh snapshot: menu items are only visible while the menu is open.
        var menuSnap = await _f.Call("windows_snapshot", new { window = _ownedHandle });
        Assert.False(menuSnap.IsError, $"windows_snapshot (menu items) failed: {menuSnap.RawText}");
        var saveAsRef = TestAppFixture.FindRef(menuSnap.Untrusted, "Save as")
            ?? TestAppFixture.FindRef(menuSnap.Untrusted, "Save As");
        Assert.True(saveAsRef != null,
            "File menu opened but no 'Save as' item found in snapshot. Snapshot:\n" + menuSnap.Untrusted);

        var saveClick = await _f.Call("windows_click", new Dictionary<string, object?> { ["ref"] = saveAsRef });
        Assert.False(saveClick.IsError, $"windows_click (Save as) failed: {saveClick.RawText}");
    }

    /// <summary>
    /// Exact-content comparison, tolerating only platform artifacts: a UTF-8 BOM and newline style.
    /// </summary>
    private static void AssertContentMatches(string path, string expected)
    {
        // ReadAllText detects the encoding BOM without allocating a second byte array.
        var actual = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(expected.Replace("\r\n", "\n"), actual);
    }

    public void Dispose()
    {
        try
        {
            // Ctrl+W closes only our active document tab (or its window if it is the last tab).
            // Never close a shared Notepad window merely because our document is selected.
            if (_ownedHandle != null)
            {
                try
                {
                    var owned = _f.SafetySession.ListTopLevelWindows().FirstOrDefault(
                        w => w.Handle == _ownedHandle);
                    if (owned is { Title: not null }
                        && (owned.Title.Contains(Path.GetFileName(_sourceFile), StringComparison.OrdinalIgnoreCase)
                            || owned.Title.Contains(Path.GetFileName(_targetFile), StringComparison.OrdinalIgnoreCase)))
                    {
                        _f.Call("windows_key", new { keys = new[] { "escape", "ctrl+w" }, window = _ownedHandle }).GetAwaiter().GetResult();
                        var closing = _f.Call("windows_snapshot", new { window = _ownedHandle }).GetAwaiter().GetResult();
                        var discard = TestAppFixture.FindRef(closing.Untrusted, "Don't save", "button");
                        if (discard != null)
                        {
                            _f.Call("windows_click", new { @ref = discard }).GetAwaiter().GetResult();
                        }
                    }
                }
                catch
                {
                    // best effort — the window may already be gone.
                }
            }
        }
        catch
        {
            // best effort.
        }
        try
        {
            if (File.Exists(_sourceFile)) File.Delete(_sourceFile);
            if (File.Exists(_targetFile)) File.Delete(_targetFile);
        }
        catch
        {
            // best effort.
        }
    }
}
