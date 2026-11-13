using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// An image the model has seen, with the exact mapping from its pixels back to physical screen pixels:
/// <c>physical = SourcePhysical.Location + image / Scale</c>. Every screenshot and zoom returns one of these
/// and coordinates the model sends back are interpreted against a frame (the most recent one by default).
/// </summary>
public sealed record ImageFrame(
    string Id,
    string Kind,
    Rectangle SourcePhysical,
    int Width,
    int Height,
    double Scale,
    int MonitorId,
    string? WindowHandle,
    DateTime CreatedAt)
{
    public Point ToPhysical(double x, double y)
    {
        var px = SourcePhysical.X + x / Scale;
        var py = SourcePhysical.Y + y / Scale;
        return new Point((int)Math.Round(px), (int)Math.Round(py));
    }

    public PointF FromPhysical(int px, int py)
        => new((float)((px - SourcePhysical.X) * Scale), (float)((py - SourcePhysical.Y) * Scale));

    public Rectangle ToPhysicalRect(double x, double y, double w, double h)
    {
        var p = ToPhysical(x, y);
        return new Rectangle(p.X, p.Y, Math.Max(1, (int)Math.Round(w / Scale)), Math.Max(1, (int)Math.Round(h / Scale)));
    }

    public RectangleF FromPhysicalRect(Rectangle r)
    {
        var p = FromPhysical(r.X, r.Y);
        return new RectangleF(p.X, p.Y, (float)(r.Width * Scale), (float)(r.Height * Scale));
    }

    public bool ContainsImagePoint(double x, double y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>Mapping block included in every image result.</summary>
    public object Mapping => new
    {
        image_id = Id,
        kind = Kind,
        size = new { width = Width, height = Height },
        origin = new { x = SourcePhysical.X, y = SourcePhysical.Y },
        scale = Math.Round(Scale, 6),
        physical_rect = new { x = SourcePhysical.X, y = SourcePhysical.Y, width = SourcePhysical.Width, height = SourcePhysical.Height },
        monitor = MonitorId,
        window = WindowHandle,
        coordinates = "x,y you pass to actions are pixels of this image; physical = origin + xy/scale"
    };
}

/// <summary>Remembers recent frames so actions can resolve image coordinates.</summary>
public sealed class FrameStore
{
    private const int MaxFrames = 24;
    private readonly object _lock = new();
    private readonly List<ImageFrame> _frames = new();
    private int _counter;

    public string NextId(string kind)
    {
        lock (_lock)
        {
            return (kind == "zoom" ? "z" : "s") + (++_counter);
        }
    }

    public void Add(ImageFrame frame)
    {
        lock (_lock)
        {
            _frames.Add(frame);
            if (_frames.Count > MaxFrames)
            {
                _frames.RemoveAt(0);
            }
        }
    }

    public ImageFrame? Last
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count == 0 ? null : _frames[^1];
            }
        }
    }

    public ImageFrame? LastOfKind(string kind)
    {
        lock (_lock)
        {
            return _frames.LastOrDefault(f => f.Kind == kind);
        }
    }

    public ImageFrame? Get(string id)
    {
        lock (_lock)
        {
            return _frames.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Frame for a coordinate action: explicit image id, else the most recent image of any kind.</summary>
    public ImageFrame Require(string? imageId)
    {
        if (!string.IsNullOrWhiteSpace(imageId))
        {
            return Get(imageId) ?? throw new ToolException("unknown_image", $"Image {imageId} is not a recent screenshot/zoom.",
                "Take a new windows_screenshot and use its image_id (or omit image_id to use the latest image).");
        }
        return Last ?? throw new ToolException("no_screenshot", "No screenshot has been taken, so x,y have no coordinate space.",
            "Call windows_screenshot first, then pass x,y from that image (or use ref/mark instead).");
    }
}

public static class ImageOps
{
    /// <summary>Scales so the long edge fits <paramref name="maxEdge"/>; never upscales. Returns the scale applied.</summary>
    public static (Bitmap bitmap, double scale) FitLongEdge(Bitmap source, int maxEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= maxEdge)
        {
            return (source, 1.0);
        }
        var scale = (double)maxEdge / longEdge;
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var scaled = Resample(source, w, h);
        // Use the real ratio so rounding of w/h does not drift the mapping.
        return (scaled, (double)w / source.Width);
    }

    /// <summary>Upscales small crops so text stays legible (zoom). Returns the scale applied.</summary>
    public static (Bitmap bitmap, double scale) ScaleTo(Bitmap source, double scale)
    {
        if (Math.Abs(scale - 1.0) < 1e-9) return (source, 1.0);
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        return (Resample(source, w, h), (double)w / source.Width);
    }

    public static Bitmap Resample(Bitmap source, int w, int h)
    {
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        result.SetResolution(96, 96);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = w < source.Width ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.SmoothingMode = SmoothingMode.None;
        g.DrawImage(source, new Rectangle(0, 0, w, h), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        return result;
    }

    public static byte[] ToPng(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Fraction of sampled pixels that differ by more than a small threshold (0..1). Images must match in size.</summary>
    public static double DifferenceRatio(Bitmap a, Bitmap b, int threshold = 24)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 1.0;
        var rect = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            long differing = 0, total = 0;
            unsafe
            {
                for (var y = 0; y < a.Height; y++)
                {
                    var pa = (byte*)da.Scan0 + y * da.Stride;
                    var pb = (byte*)db.Scan0 + y * db.Stride;
                    for (var x = 0; x < a.Width; x++)
                    {
                        var i = x * 4;
                        var d = Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
                        if (d > threshold) differing++;
                        total++;
                    }
                }
            }
            return total == 0 ? 0 : (double)differing / total;
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }
}
