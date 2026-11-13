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
        // Windows 11 Notepad is single-instance (new launches become tabs): start from a clean slate.
        foreach (var p in Process.GetProcessesByName("Notepad"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { /* best effort */ }
        }

        var launch = await _f.Call("windows_launch", new { app = "notepad.exe" });
        Assert.False(launch.IsError, $"windows_launch failed: {launch.RawText}");
        var handle = launch.Header!["window"]!["handle"]!.GetValue<string>();
        var info = _f.FunctionalSession.ResolveWindow(handle);
        _notepad = Process.GetProcessById((int)info.Pid);

        var snap = await _f.Call("windows_snapshot", new { window = handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");

        // Windows 11 Notepad exposes the editor as a document named "Text editor"; classic Notepad as an edit box.
        var docRef = TestAppFixture.FindRef(snap.Untrusted, "Text editor")
                    ?? TestAppFixture.FindRef(snap.Untrusted, "Text", "textbox");
        Assert.NotNull(docRef);

        var type = await _f.Call("windows_type", new Dictionary<string, object?> { ["ref"] = docRef, ["text"] = "hello from mcp" });
        Assert.False(type.IsError, $"windows_type failed: {type.RawText}");

        // Ctrl+S opens the Save As dialog.
        var save = await _f.Call("windows_key", new { keys = "ctrl+s", window = handle });
        Assert.False(save.IsError, $"windows_key failed: {save.RawText}");

        // Wait for the Save As dialog (an owned window of the Notepad process).
        var dialog = await _f.WaitForWindowAsync("Save as", processName: "Notepad.exe");
        Assert.NotNull(dialog);

        // Fill the filename field.
        var dialogSnap = await _f.Call("windows_snapshot", new { window = dialog!.Handle });
        Assert.False(dialogSnap.IsError, $"windows_snapshot failed: {dialogSnap.RawText}");
        var fileNameRef = TestAppFixture.FindRef(dialogSnap.Untrusted, "File name", "textbox");
        Assert.NotNull(fileNameRef);

        var fill = await _f.Call("windows_fill", new Dictionary<string, object?> { ["ref"] = fileNameRef, ["value"] = _file });
        Assert.False(fill.IsError, $"windows_fill failed: {fill.RawText}");

        // Press Enter to confirm the save.
        var enter = await _f.Call("windows_key", new { keys = "enter", window = dialog.Handle });
        Assert.False(enter.IsError, $"windows_key failed: {enter.RawText}");

        // Wait for the dialog to be gone.
        var gone = await _f.WaitForWindowGoneAsync(dialog.Hwnd, 20000);
        Assert.True(gone, "Save As dialog did not close");

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
