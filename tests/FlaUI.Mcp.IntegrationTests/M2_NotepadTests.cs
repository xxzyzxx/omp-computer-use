using System.Diagnostics;
using System.Text.Json;
using FlaUI.Mcp.IntegrationTests;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M2 — Notepad flow: launch, type text, save-as dialog, fill filename, key enter,
/// assert file exists with the content. Closes the app afterwards.
/// </summary>
[Collection("TestApps")]
public class M2_NotepadTests : IDisposable
{
    private readonly TestAppFixture _f;
    private readonly string _file;
    private Process? _notepad;

    public M2_NotepadTests(TestAppFixture fixture)
    {
        _f = fixture;
        _file = Path.Combine(Path.GetTempPath(), "flaui-mcp-notepad-" + Guid.NewGuid().ToString("N") + ".txt");
    }

    [Fact]
    public async Task Notepad_TypeAndSave_WritesFile()
    {
        _notepad = Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            UseShellExecute = true
        }) ?? throw new Exception("Could not launch notepad.exe");

        var info = await _f.WaitForWindowAsync("Notepad");
        Assert.NotNull(info);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");

        // The document ref (or textbox ref) — the Notepad document is a TextBox named "Text Editor" or similar.
        var docRef = TestAppFixture.FindRef(snap.Untrusted, "Text Editor", "textbox")
                    ?? TestAppFixture.FindRef(snap.Untrusted, "Text", "textbox");
        if (docRef == null)
        {
            // Fall back to the window itself: focus the window and type without a ref.
            var focus = await _f.Call("windows_focus", new { window = info!.Handle });
            Assert.False(focus.IsError, $"windows_focus failed: {focus.RawText}");
        }
        else
        {
            var type = await _f.Call("windows_type", new Dictionary<string, object?> { ["ref"] = docRef, ["text"] = "hello from mcp" });
            Assert.False(type.IsError, $"windows_type failed: {type.RawText}");
        }

        // Ctrl+S opens the Save As dialog.
        var save = await _f.Call("windows_key", new { keys = "ctrl+s" });
        Assert.False(save.IsError, $"windows_key failed: {save.RawText}");

        // Wait for the Save As dialog.
        var dialog = await _f.WaitForWindowAsync("Save As");
        Assert.NotNull(dialog);

        // Fill the filename field.
        var dialogSnap = await _f.Call("windows_snapshot", new { window = dialog!.Handle });
        Assert.False(dialogSnap.IsError, $"windows_snapshot failed: {dialogSnap.RawText}");
        var fileNameRef = TestAppFixture.FindRef(dialogSnap.Untrusted, "File name", "textbox");
        Assert.NotNull(fileNameRef);

        var fill = await _f.Call("windows_fill", new Dictionary<string, object?> { ["ref"] = fileNameRef, ["value"] = _file });
        Assert.False(fill.IsError, $"windows_fill failed: {fill.RawText}");

        // Press Enter to confirm the save.
        var enter = await _f.Call("windows_key", new { keys = "enter" });
        Assert.False(enter.IsError, $"windows_key failed: {enter.RawText}");

        // Wait for the dialog to be gone.
        var gone = await _f.WaitForWindowAsync("Save As", timeoutMs: 20000);
        Assert.Null(gone);

        // Assert the file exists with the expected content.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && !File.Exists(_file))
        {
            await Task.Delay(200);
        }
        Assert.True(File.Exists(_file), "The file was not written.");
        var content = File.ReadAllText(_file);
        Assert.Contains("hello from mcp", content);
    }

    public void Dispose()
    {
        try
        {
            if (_notepad is { HasExited: false })
            {
                _notepad.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort.
        }
        try
        {
            if (File.Exists(_file))
            {
                File.Delete(_file);
            }
        }
        catch
        {
            // best effort.
        }
    }
}
