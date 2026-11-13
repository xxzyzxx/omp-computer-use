using System.Diagnostics;
using System.Text.Json.Nodes;
using FlaUI.Mcp.IntegrationTests;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// M3 — DPI / coordinate mapping: take a screenshot, map a UIA centre to image space,
/// click it, and assert physical coordinates match the UIA centre. Also asserts the
/// screenshot downscale budget and the zoom path. Records the monitor DPI in the output.
/// </summary>
[Collection("TestApps")]
public class M3_DpiTests : IDisposable
{
    private readonly TestAppFixture _f;
    private Process? _calc;

    public M3_DpiTests(TestAppFixture fixture) => _f = fixture;

    [Fact]
    public async Task Calculator_ScreenshotAndClick_MapCorrectly()
    {
        _calc = Process.Start(new ProcessStartInfo
        {
            FileName = "calc.exe",
            UseShellExecute = true
        }) ?? throw new Exception("Could not launch calc.exe");

        var info = await _f.WaitForWindowAsync("Calculator", processName: "CalculatorApp.exe");
        Assert.NotNull(info);

        // Record the monitor DPI from windows_status.
        var status = await _f.Call("windows_status", new { });
        Assert.False(status.IsError, $"windows_status failed: {status.RawText}");
        Assert.NotNull(status.Header);
        var monitors = status.Header!["monitors"] as JsonArray;
        Assert.NotNull(monitors);
        Assert.True(monitors!.Count > 0, "windows_status returned no monitors.");
        var dpi = monitors[0]?["dpi"];
        Assert.NotNull(dpi);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");

        var line = TestAppFixture.FindLine(snap.Untrusted, "Seven", "button");
        Assert.NotNull(line);
        var bounds = TestAppFixture.Bounds(line!);
        Assert.NotNull(bounds);
        var cx = bounds!.Value.x + bounds.Value.w / 2;
        var cy = bounds.Value.y + bounds.Value.h / 2;

        var shot = await _f.Call("windows_screenshot", new Dictionary<string, object?>
        {
            ["window"] = info!.Handle,
            ["marks"] = false
        });
        Assert.False(shot.IsError, $"windows_screenshot failed: {shot.RawText}");
        Assert.NotNull(shot.Header);
        var image = shot.Header!["image"];
        Assert.NotNull(image);
        var origin = image!["origin"];
        Assert.NotNull(origin);
        var scale = (double?)image["scale"];
        Assert.NotNull(scale);
        Assert.True(scale <= 1, $"image.scale {scale} is greater than 1.");
        var size = image["size"];
        Assert.NotNull(size);
        var width = (int?)size["width"];
        var height = (int?)size["height"];
        Assert.NotNull(width);
        Assert.NotNull(height);
        var longEdge = Math.Max(width!.Value, height!.Value);
        Assert.True(longEdge <= 1568, $"image long edge {longEdge} is greater than 1568.");

        var ox = (int?)origin["x"] ?? 0;
        var oy = (int?)origin["y"] ?? 0;
        var xImg = (int)Math.Round((cx - ox) * scale!.Value);
        var yImg = (int)Math.Round((cy - oy) * scale.Value);

        var click = await _f.Call("windows_click", new Dictionary<string, object?>
        {
            ["x"] = xImg,
            ["y"] = yImg,
            ["image_id"] = (string?)image["image_id"],
            ["force_mouse"] = false
        });
        Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
        Assert.NotNull(click.Header);
        var physical = click.Header!["physical"];
        Assert.NotNull(physical);
        var px = (int?)physical["x"];
        var py = (int?)physical["y"];
        Assert.NotNull(px);
        Assert.NotNull(py);
        Assert.True(Math.Abs(px!.Value - cx) <= 2, $"physical x {px} is not within 2 px of {cx}.");
        Assert.True(Math.Abs(py!.Value - cy) <= 2, $"physical y {py} is not within 2 px of {cy}.");

        // Assert the display shows 7.
        var snap2 = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap2.IsError, $"windows_snapshot failed: {snap2.RawText}");
        var displayLine = TestAppFixture.FindLine(snap2.Untrusted, "Display is");
        Assert.NotNull(displayLine);
        Assert.Contains("7", displayLine!);
    }

    [Fact]
    public async Task Calculator_ZoomAndClick_MapCorrectly()
    {
        _calc = Process.Start(new ProcessStartInfo
        {
            FileName = "calc.exe",
            UseShellExecute = true
        }) ?? throw new Exception("Could not launch calc.exe");

        var info = await _f.WaitForWindowAsync("Calculator", processName: "CalculatorApp.exe");
        Assert.NotNull(info);

        var snap = await _f.Call("windows_snapshot", new { window = info!.Handle });
        Assert.False(snap.IsError, $"windows_snapshot failed: {snap.RawText}");
        var line = TestAppFixture.FindLine(snap.Untrusted, "Seven", "button");
        Assert.NotNull(line);
        var bounds = TestAppFixture.Bounds(line!);
        Assert.NotNull(bounds);
        var cx = bounds!.Value.x + bounds.Value.w / 2;
        var cy = bounds.Value.y + bounds.Value.h / 2;

        var ref_ = TestAppFixture.FindRef(snap.Untrusted, "Seven", "button");
        Assert.NotNull(ref_);

        var zoom = await _f.Call("windows_zoom", new Dictionary<string, object?>
        {
            ["ref"] = ref_,
            ["marks"] = false
        });
        Assert.False(zoom.IsError, $"windows_zoom failed: {zoom.RawText}");
        Assert.NotNull(zoom.Header);
        var image = zoom.Header!["image"];
        Assert.NotNull(image);
        var origin = image!["origin"];
        Assert.NotNull(origin);
        var scale = (double?)image["scale"];
        Assert.NotNull(scale);

        var ox = (int?)origin["x"] ?? 0;
        var oy = (int?)origin["y"] ?? 0;
        var xImg = (int)Math.Round((cx - ox) * scale!.Value);
        var yImg = (int)Math.Round((cy - oy) * scale.Value);

        var click = await _f.Call("windows_click", new Dictionary<string, object?>
        {
            ["x"] = xImg,
            ["y"] = yImg,
            ["image_id"] = (string?)image["image_id"],
            ["force_mouse"] = false
        });
        Assert.False(click.IsError, $"windows_click failed: {click.RawText}");
        Assert.NotNull(click.Header);
        var physical = click.Header!["physical"];
        Assert.NotNull(physical);
        var px = (int?)physical["x"];
        var py = (int?)physical["y"];
        Assert.NotNull(px);
        Assert.NotNull(py);
        Assert.True(Math.Abs(px!.Value - cx) <= 2, $"physical x {px} is not within 2 px of {cx}.");
        Assert.True(Math.Abs(py!.Value - cy) <= 2, $"physical y {py} is not within 2 px of {cy}.");
    }

    public void Dispose()
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
}
