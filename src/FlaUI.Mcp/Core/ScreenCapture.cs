using System.Drawing;
using System.Drawing.Imaging;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Pixel capture in physical coordinates. Windows are captured with PrintWindow(PW_RENDERFULLCONTENT), which
/// renders occluded windows too; monitors and regions use a screen BitBlt. Capture scopes (the overlay hides
/// itself) are entered around every capture.
/// </summary>
public static class ScreenCapture
{
    /// <summary>Optional scope entered around every capture (used by the overlay to hide while pixels are read).</summary>
    public static Func<IDisposable>? CaptureScope { get; set; }

    public sealed record Captured(Bitmap Bitmap, Rectangle Physical, string Method);

    public static Captured Region(Rectangle physical)
    {
        if (physical.Width <= 0 || physical.Height <= 0)
        {
            throw new ToolException("bad_region", "Capture region is empty.", "Pass a region with positive width and height.");
        }
        using var _ = CaptureScope?.Invoke();
        var bmp = new Bitmap(physical.Width, physical.Height, PixelFormat.Format32bppArgb);
        bmp.SetResolution(96, 96);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(physical.Left, physical.Top, 0, 0, physical.Size, CopyPixelOperation.SourceCopy);
        }
        return new Captured(bmp, physical, "bitblt");
    }

    public static Captured Monitor(MonitorInfo monitor) => Region(monitor.Bounds);

    /// <summary>
    /// Captures a top-level window. Occluded windows render via PrintWindow; a blank PrintWindow result falls
    /// back to a screen copy of the window's visible bounds (which then includes anything covering it).
    /// </summary>
    public static Captured Window(WindowInfo window)
    {
        if (window.Minimized)
        {
            throw new ToolException("window_minimized", $"Window {window.Handle} is minimized and has no pixels to capture.",
                $"Call windows_focus window={window.Handle} (restores it), then screenshot again.");
        }

        var visible = Native.GetVisibleWindowBounds(window.Hwnd);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            throw new ToolException("window_empty", $"Window {window.Handle} has no visible bounds.",
                "Call windows_list_windows to pick a visible window.");
        }

        using var _ = CaptureScope?.Invoke();

        if (Native.GetWindowRect(window.Hwnd, out var full) && full.Width > 0 && full.Height > 0)
        {
            var bmp = new Bitmap(full.Width, full.Height, PixelFormat.Format32bppArgb);
            bmp.SetResolution(96, 96);
            var ok = false;
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try
                {
                    ok = Native.PrintWindow(window.Hwnd, hdc, Native.PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            if (ok && !IsBlankOrNearlyBlank(bmp))
            {
                // Crop the invisible resize border so the image matches the visible bounds exactly.
                var crop = new Rectangle(visible.X - full.Left, visible.Y - full.Top, visible.Width, visible.Height);
                crop.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
                if (crop.Width > 0 && crop.Height > 0 && (crop.X != 0 || crop.Y != 0 || crop.Width != bmp.Width || crop.Height != bmp.Height))
                {
                    var cropped = bmp.Clone(crop, PixelFormat.Format32bppArgb);
                    bmp.Dispose();
                    return new Captured(cropped, new Rectangle(full.Left + crop.X, full.Top + crop.Y, crop.Width, crop.Height), "printwindow");
                }
                return new Captured(bmp, full.ToRectangle(), "printwindow");
            }
            bmp.Dispose();
        }

        var fallback = Region(visible);
        return fallback with { Method = "bitblt-fallback" };
    }

    /// <summary>Crops a captured bitmap to a physical rectangle (clamped to the capture).</summary>
    public static Captured Crop(Captured source, Rectangle physical)
    {
        var rel = new Rectangle(physical.X - source.Physical.X, physical.Y - source.Physical.Y, physical.Width, physical.Height);
        rel.Intersect(new Rectangle(0, 0, source.Bitmap.Width, source.Bitmap.Height));
        if (rel.Width <= 0 || rel.Height <= 0)
        {
            throw new ToolException("bad_region", "Region lies outside the captured area.",
                "Pass a region inside the last screenshot (use its size from the mapping).");
        }
        var bmp = source.Bitmap.Clone(rel, PixelFormat.Format32bppArgb);
        return new Captured(bmp, new Rectangle(source.Physical.X + rel.X, source.Physical.Y + rel.Y, rel.Width, rel.Height), source.Method);
    }

    /// <summary>Samples up to 1024 pixels; true when none has visible colour (PrintWindow failure signature).</summary>
    public static bool IsBlankOrNearlyBlank(Bitmap bitmap)
    {
        const int maxSamples = 1024;
        var stepX = Math.Max(1, bitmap.Width / 32);
        var stepY = Math.Max(1, bitmap.Height / 32);
        var samples = 0;
        for (var y = 0; y < bitmap.Height && samples < maxSamples; y += stepY)
        {
            for (var x = 0; x < bitmap.Width && samples < maxSamples; x += stepX)
            {
                samples++;
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 0 && (pixel.R > 8 || pixel.G > 8 || pixel.B > 8))
                {
                    return false;
                }
            }
        }
        return samples > 0;
    }
}
