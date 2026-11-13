using System.Drawing;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Tools;
using Xunit;

namespace PlaywrightWindows.Mcp.Tests;

public class ScreenshotTests
{
    // ----- ImageFrame mapping round trips -----

    public static TheoryData<Rectangle> MonitorFrames() => new()
    {
        new Rectangle(0, 0, 1920, 1080),    // 100 %
        new Rectangle(0, 0, 2400, 1350),    // 125 %
        new Rectangle(0, 0, 2880, 1620),    // 150 %
        new Rectangle(0, 0, 3840, 2160),    // 300 %
        new Rectangle(0, -2160, 3840, 2160) // second monitor above the primary, negative origin
    };

    /// <summary>A frame as ScreenshotTool builds it: physical capture downscaled so the long edge fits maxEdge.</summary>
    private static ImageFrame DownscaledFrame(Rectangle physical, int maxEdge = 1568)
    {
        var s0 = (double)maxEdge / Math.Max(physical.Width, physical.Height);
        var w = Math.Max(1, (int)Math.Round(physical.Width * s0));
        var h = Math.Max(1, (int)Math.Round(physical.Height * s0));
        return new ImageFrame("s1", "screenshot", physical, w, h, (double)w / physical.Width, 1, null, DateTime.UtcNow);
    }

    [Theory]
    [MemberData(nameof(MonitorFrames))]
    public void ImageFrame_PhysicalToImageToPhysical_StaysWithinOnePixel(Rectangle physical)
    {
        var frame = DownscaledFrame(physical);
        var points = new[]
        {
            frame.SourcePhysical.Location,
            new Point(frame.SourcePhysical.Right - 1, frame.SourcePhysical.Bottom - 1),
            new Point(frame.SourcePhysical.X + frame.SourcePhysical.Width / 2, frame.SourcePhysical.Y + frame.SourcePhysical.Height / 2),
            new Point(frame.SourcePhysical.X + 17, frame.SourcePhysical.Y + 23),
            new Point(frame.SourcePhysical.Right - 3, frame.SourcePhysical.Y + 11)
        };
        foreach (var p in points)
        {
            var image = frame.FromPhysical(p.X, p.Y);
            var back = frame.ToPhysical(image.X, image.Y);
            Assert.InRange(back.X, p.X - 1, p.X + 1);
            Assert.InRange(back.Y, p.Y - 1, p.Y + 1);
        }
    }

    [Theory]
    [MemberData(nameof(MonitorFrames))]
    public void ImageFrame_ImageToPhysical_DriftsAtMostTwoPhysicalPixels(Rectangle physical)
    {
        var frame = DownscaledFrame(physical);
        var points = new[]
        {
            (0.0, 0.0),
            (frame.Width - 1.0, frame.Height - 1.0),
            (frame.Width / 2.0, frame.Height / 2.0),
            (frame.Width / 3.0, frame.Height / 4.0)
        };
        foreach (var (ix, iy) in points)
        {
            var phys = frame.ToPhysical(ix, iy);
            var image = frame.FromPhysical(phys.X, phys.Y);
            var back = frame.ToPhysical(image.X, image.Y);
            Assert.InRange(back.X, phys.X - 2, phys.X + 2);
            Assert.InRange(back.Y, phys.Y - 2, phys.Y + 2);
        }
    }

    [Fact]
    public void ImageFrame_NegativeOriginMonitor_MapsToNegativePhysicalCoordinates()
    {
        var frame = DownscaledFrame(new Rectangle(0, -2160, 3840, 2160));
        Assert.Equal(new Point(0, -2160), frame.ToPhysical(0, 0));
        var bottom = frame.ToPhysical(0, frame.Height - 1);
        Assert.InRange(bottom.Y, -2160 + 2160 - 2, -2160 + 2160 + 2);
        var back = frame.FromPhysical(0, -2160);
        Assert.InRange(back.X, -0.5, 0.5);
        Assert.InRange(back.Y, -0.5, 0.5);
    }

    [Fact]
    public void ImageFrame_ZoomUpscale_RoundTrips()
    {
        var frame = new ImageFrame("z1", "zoom", new Rectangle(1000, 500, 200, 150), 800, 600, 4.0, 1, "w1", DateTime.UtcNow);
        Assert.Equal(new Point(1000, 500), frame.ToPhysical(0, 0));
        Assert.Equal(new Point(1200, 650), frame.ToPhysical(799.5, 599.5));

        var rect = frame.FromPhysicalRect(new Rectangle(1100, 560, 50, 40));
        Assert.Equal(400.0, rect.X, 3);
        Assert.Equal(240.0, rect.Y, 3);
        Assert.Equal(200.0, rect.Width, 3);
        Assert.Equal(160.0, rect.Height, 3);

        foreach (var (ix, iy) in new[] { (0.0, 0.0), (799.0, 599.0), (123.5, 456.25) })
        {
            var phys = frame.ToPhysical(ix, iy);
            var image = frame.FromPhysical(phys.X, phys.Y);
            var back = frame.ToPhysical(image.X, image.Y);
            Assert.InRange(back.X, phys.X - 1, phys.X + 1);
            Assert.InRange(back.Y, phys.Y - 1, phys.Y + 1);
        }
    }

    [Fact]
    public void FitLongEdge_ScaleEqualsProducedWidthOverSourceWidth()
    {
        var small = new Bitmap(100, 60);
        var portrait = new Bitmap(500, 2000);
        var large = new Bitmap(3840, 2160);
        try
        {
            var (same, smallScale) = ImageOps.FitLongEdge(small, 1568);
            Assert.Same(small, same);
            Assert.Equal(1.0, smallScale);

            var (portraitResult, portraitScale) = ImageOps.FitLongEdge(portrait, 1568);
            using (portraitResult)
            {
                Assert.Equal(392, portraitResult.Width);
                Assert.Equal(1568, portraitResult.Height);
                Assert.Equal((double)portraitResult.Width / 500, portraitScale, 12);
            }

            var (largeResult, largeScale) = ImageOps.FitLongEdge(large, 1568);
            using (largeResult)
            {
                Assert.Equal(1568, largeResult.Width);
                Assert.Equal((double)largeResult.Width / 3840, largeScale, 12);
                Assert.Equal(882, largeResult.Height);
            }
        }
        finally
        {
            small.Dispose();
            portrait.Dispose();
            large.Dispose();
        }
    }

    // ----- marks placement -----

    [Fact]
    public void PlaceLabels_NeverOverlapsAndStaysInsideTheImage()
    {
        // Dense grid of touching element rects; every slot above a row is taken by the row's own labels,
        // so later rows must fall back to their inside slots.
        var rects = new List<RectangleF>();
        for (var row = 0; row < 15; row++)
        {
            for (var col = 0; col < 13; col++)
            {
                rects.Add(new RectangleF(col * 60f, row * 40f, 30f, 20f));
            }
        }
        rects.Add(new RectangleF(770, 0, 30, 20));   // touches the right edge
        rects.Add(new RectangleF(0, 580, 30, 20));   // touches the bottom edge

        var labels = MarksRenderer.PlaceLabels(rects, new SizeF(800, 600), new SizeF(22, 14));

        Assert.Equal(rects.Count, labels.Count);
        for (var i = 0; i < labels.Count; i++)
        {
            var l = labels[i];
            Assert.True(l.Left >= 0 && l.Top >= 0 && l.Right <= 800 && l.Bottom <= 600,
                $"label {i} left the image: {l}");
            for (var j = i + 1; j < labels.Count; j++)
            {
                Assert.False(l.IntersectsWith(labels[j]), $"labels {i} and {j} overlap: {l} vs {labels[j]}");
            }
        }
    }

    [Fact]
    public void PlaceLabels_UsesOutsideAboveSlotFirst_ThenInsideTopLeft()
    {
        var labels = MarksRenderer.PlaceLabels(
            new[] { new RectangleF(100, 200, 60, 40), new RectangleF(100, 0, 60, 40), new RectangleF(0, 0, 60, 40) },
            new SizeF(800, 600), new SizeF(22, 14));

        // Room above: label sits outside, directly above the rect.
        Assert.True(labels[0].Bottom <= 200, $"label 0 should be above the rect: {labels[0]}");
        Assert.Equal(100.0, labels[0].Left, 3);

        // At the top edge there is no room above: the label clamps to the top of the image / inside the rect.
        Assert.True(labels[1].Top >= 0 && labels[1].Top <= 1, $"label 1: {labels[1]}");
        Assert.True(labels[2].Top >= 0 && labels[2].Top <= 1 && labels[2].Left <= 1, $"label 2: {labels[2]}");
    }

    // ----- marks candidate selection -----

    private static ElementMeta Meta(string reference, Rectangle bounds, bool interactive = true)
        => new(reference, "w1", IntPtr.Zero, 4242, 1, "rt:" + reference, "button", "n", null, bounds, false, interactive);

    [Fact]
    public void SelectMarkCandidates_FiltersInteractiveBoundsCaptureAndMinimumSize()
    {
        var snapshot = new List<ElementMeta>
        {
            Meta("w1e1", new Rectangle(10, 10, 40, 20)),                       // kept
            Meta("w1e2", new Rectangle(50, 10, 40, 20), interactive: false),   // not interactive
            Meta("w1e3", Rectangle.Empty),                                     // empty bounds
            Meta("w1e4", new Rectangle(2000, 2000, 40, 20)),                   // outside the capture
            Meta("w1e5", new Rectangle(100, 900, 40, 300)),                    // partially inside
            Meta("w1e6", new Rectangle(500, 500, 10, 10))                      // 10x10 physical: 4x4 px at scale 0.4
        };
        var capture = new Rectangle(0, 0, 1000, 1000);

        var picked = ScreenshotTools.SelectMarkCandidates(snapshot, capture, 0.4, 120);

        Assert.Equal(new[] { "w1e1", "w1e5" }, picked.Select(m => m.Ref).ToArray());
    }

    [Fact]
    public void SelectMarkCandidates_CapKeepsLargestInSnapshotOrder()
    {
        var snapshot = new List<ElementMeta>
        {
            Meta("w1e1", new Rectangle(0, 0, 10, 10)),
            Meta("w1e2", new Rectangle(0, 0, 20, 10)),
            Meta("w1e3", new Rectangle(0, 0, 30, 10)),
            Meta("w1e4", new Rectangle(0, 0, 40, 10)),
            Meta("w1e5", new Rectangle(0, 0, 50, 10))
        };

        var picked = ScreenshotTools.SelectMarkCandidates(snapshot, new Rectangle(0, 0, 1000, 1000), 1.0, 3);

        Assert.Equal(new[] { "w1e3", "w1e4", "w1e5" }, picked.Select(m => m.Ref).ToArray());
    }
}
