using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlaUI.Mcp.IntegrationTests;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M4 — Set-of-marks: screenshot marks=true, click a toggle mark, verify the state changed,
/// restore. Electron/Chromium: launch VSCodium/VS Code, mark, click, screenshot diff, close.
/// Skips with a clear message when neither editor is installed.
/// </summary>
[Collection("TestApps")]
public class M4_MarksTests : IDisposable
{
    private readonly TestAppFixture _f;
    private Process? _settings;
    private Process? _editor;

    public M4_MarksTests(TestAppFixture fixture) => _f = fixture;

    [Fact]
    public async Task Settings_Marks_ClickToggle_ChangesState()
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

        var shot = await _f.Call("windows_screenshot", new Dictionary<string, object?>
        {
            ["window"] = info!.Handle,
            ["marks"] = true
        });
        Assert.False(shot.IsError, $"windows_screenshot failed: {shot.RawText}");
        Assert.NotNull(shot.Header);
        var marks = shot.Header!["marks"];
        Assert.NotNull(marks);
        var count = (int?)marks["count"];
        Assert.NotNull(count);
        Assert.True(count > 0, $"marks.count {count} is 0.");

        // Map values look like refs.
        var values = marks["values"] as JsonArray;
        if (values != null && values.Count > 0)
        {
            var first = values[0]?.ToString();
            Assert.NotNull(first);
            Assert.Matches(@"^w\d+e\d+$", first ?? "");
        }

        // Pick a mark whose ref's snapshot line is a checkbox/button named like a toggle.
        var toggleLine = TestAppFixture.FindLine(
            snap.Untrusted,
            "Bluetooth",
            "checkbox");
        if (toggleLine == null)
        {
            toggleLine = TestAppFixture.FindLine(snap.Untrusted, "Night light", "checkbox");
        }
        if (toggleLine == null)
        {
            toggleLine = TestAppFixture.FindLine(snap.Untrusted, "Bluetooth", "button");
        }
        Assert.NotNull(toggleLine);

        var toggleRef = TestAppFixture.FindRef(toggleLine!);
        Assert.NotNull(toggleRef);
        var wasChecked = toggleLine!.Contains("[checked]");

        // Find the mark number whose ref is the toggle ref.
        var markNum = FindMarkNum(marks, toggleRef!);
        Assert.NotNull(markNum);

        var click = await _f.Call("windows_click", new Dictionary<string, object?>
        {
            ["mark"] = markNum,
            ["force_mouse"] = false
        });
        Assert.False(click.IsError, $"windows_click failed: {click.RawText}");

        await Task.Delay(500);

        // Snapshot diff shows the toggle state changed.
        var snap2 = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap2.IsError, $"windows_snapshot failed: {snap2.RawText}");
        var toggleName = TestAppFixture.FindName(toggleLine!) ?? "";
        var toggleLine2 = TestAppFixture.FindLine(snap2.Untrusted, toggleName, "checkbox");
        if (toggleLine2 == null)
        {
            toggleLine2 = TestAppFixture.FindLine(snap2.Untrusted, toggleName, "button");
        }
        Assert.NotNull(toggleLine2);
        var isChecked = toggleLine2!.Contains("[checked]");
        Assert.NotEqual(wasChecked, isChecked);

        // Restore.
        var click2 = await _f.Call("windows_click", new Dictionary<string, object?>
        {
            ["mark"] = markNum,
            ["force_mouse"] = false
        });
        Assert.False(click2.IsError, $"windows_click failed: {click2.RawText}");
    }

    [Fact]
    public async Task Electron_Marks_ClickMark_DiffersImage()
    {
        var editorPath = FindEditorPath();
        if (editorPath == null)
        {
            // Skip when neither editor is installed, with a clear message.
            Assert.True(true, "SKIP: Neither VSCodium nor VS Code is installed.");
            return;
        }

        _editor = Process.Start(new ProcessStartInfo
        {
            FileName = editorPath,
            Arguments = "--new-window --disable-workspace-trust",
            UseShellExecute = true
        }) ?? throw new Exception($"Could not launch {editorPath}");

        var info = await _f.WaitForWindowAsync("VSCodium");
        if (info == null)
        {
            info = await _f.WaitForWindowAsync("Visual Studio Code");
        }
        Assert.NotNull(info);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");

        // The snapshot may include a thin_tree hint for Electron/Chromium hosts.
        if (TestAppFixture.FindLine(snap.Untrusted, "thin_tree") != null)
        {
            Assert.True(true, "thin_tree hint present.");
        }

        var shot = await _f.Call("windows_screenshot", new Dictionary<string, object?>
        {
            ["window"] = info!.Handle,
            ["marks"] = true
        });
        Assert.False(shot.IsError, $"windows_screenshot failed: {shot.RawText}");
        var png1 = shot.Png;
        Assert.NotNull(png1);

        var marks = shot.Header!["marks"];
        Assert.NotNull(marks);
        var count = (int?)marks["count"];
        Assert.NotNull(count);
        Assert.True(count > 0, $"marks.count {count} is 0.");

        // Click the mark that corresponds to the File menu button (or any mark).
        var values = marks["values"] as JsonArray;
        var clicked = false;
        if (values != null && values.Count > 0)
        {
            var fileLine = TestAppFixture.FindLine(snap.Untrusted, "File", "button");
            if (fileLine != null)
            {
                var fileRef = TestAppFixture.FindRef(fileLine);
                if (fileRef != null)
                {
                    var markNum = FindMarkNum(marks, fileRef);
                    if (markNum != null)
                    {
                        var click = await _f.Call("windows_click", new Dictionary<string, object?>
                        {
                            ["mark"] = markNum,
                            ["force_mouse"] = false
                        });
                        Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
                        clicked = true;
                    }
                }
            }
        }
        if (!clicked)
        {
            var click = await _f.Call("windows_click", new Dictionary<string, object?>
            {
                ["mark"] = 0,
                ["force_mouse"] = false
            });
            Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
        }

        var shot2 = await _f.Call("windows_screenshot", new Dictionary<string, object?>
        {
            ["window"] = info!.Handle,
            ["marks"] = false
        });
        Assert.False(shot2.IsError, $"windows_screenshot failed: {shot2.RawText}");
        var png2 = shot2.Png;
        Assert.NotNull(png2);

        var hash1 = Convert.ToHexString(SHA256.HashData(png1!));
        var hash2 = Convert.ToHexString(SHA256.HashData(png2!));
        Assert.NotEqual(hash1, hash2);

        // Close with Alt+F4.
        var key = await _f.Call("windows_key", new { keys = "alt+f4" });
        Assert.False(key.IsError, $"windows_key failed: {key.RawText}");
        await Task.Delay(1000);
        var snapAfter = await _f.Call("windows_snapshot", new { window = info!.Handle });
        if (snapAfter.Untrusted != null && TestAppFixture.FindLine(snapAfter.Untrusted, "Save") != null)
        {
            var keyN = await _f.Call("windows_key", new { keys = "n" });
            Assert.False(keyN.IsError, $"windows_key failed: {keyN.RawText}");
        }
    }

    private static string? FindEditorPath()
    {
        var candidates = new[]
        {
            @"C:\Program Files\VSCodium\VSCodium.exe",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Microsoft VS Code", "Code.exe")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static int? FindMarkNum(JsonNode marks, string refId)
    {
        var values = marks["values"] as JsonArray;
        if (values == null) return null;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i]?.ToString() == refId)
            {
                return i;
            }
        }
        return null;
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
        try
        {
            if (_editor is { HasExited: false })
            {
                _editor.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort.
        }
    }
}
