using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Set-of-marks rendering: a 2px rectangle outline per element plus a numbered label box in a 6-colour
/// high-contrast palette. All coordinates are pixels of the image being drawn on (after any downscale).
/// Label placement is a pure function (<see cref="PlaceLabels"/>): greedy over six candidate slots per
/// element (outside above the rect, then inside corners, then centre), never overlapping an already
/// placed label and always clamped inside the image.
/// </summary>
public static class MarksRenderer
{
    /// <summary>High-contrast colours, cycled per mark.</summary>
    public static readonly Color[] Palette =
    {
        Color.FromArgb(0xE5, 0x39, 0x35), // red
        Color.FromArgb(0x1E, 0x88, 0xE5), // blue
        Color.FromArgb(0x2E, 0x7D, 0x32), // green
        Color.FromArgb(0xF9, 0x8C, 0x00), // orange
        Color.FromArgb(0x8E, 0x24, 0xAA), // purple
        Color.FromArgb(0x00, 0x89, 0x7B), // teal
    };

    private static readonly Lazy<Font> LabelFontLazy = new(() => new Font("Segoe UI", 8.5f, FontStyle.Bold));

    /// <summary>Bold label font (~11-12 px tall at 96 dpi; mark images are drawn at 96 dpi).</summary>
    public static Font LabelFont => LabelFontLazy.Value;

    /// <summary>Size of one label box, sized for the widest number that will be drawn (1..maxNumber).</summary>
    public static SizeF MeasureLabelSize(Graphics g, int maxNumber)
    {
        var text = g.MeasureString(Math.Max(1, maxNumber).ToString(CultureInfo.InvariantCulture), LabelFont);
        return new SizeF(text.Width + 4, text.Height + 2);
    }

    /// <summary>
    /// Pure placement: returns one label rectangle per element rect, in the same order. Labels never
    /// overlap each other (best effort when every slot of an element already collides) and stay inside
    /// <paramref name="image"/>.
    /// </summary>
    public static IReadOnlyList<RectangleF> PlaceLabels(IReadOnlyList<RectangleF> rects, SizeF image, SizeF label)
    {
        var placed = new List<RectangleF>(rects.Count);
        foreach (var rect in rects)
        {
            placed.Add(PlaceOne(rect, image, label, placed));
        }
        return placed;
    }

    private static RectangleF PlaceOne(RectangleF rect, SizeF image, SizeF label, List<RectangleF> placed)
    {
        // Priority order: outside above the top-left corner, then inside the four corners, then centre.
        ReadOnlySpan<PointF> anchors = stackalloc PointF[]
        {
            new(rect.Left, rect.Top - label.Height - 1),
            new(rect.Left + 1, rect.Top + 1),
            new(rect.Right - label.Width - 1, rect.Top + 1),
            new(rect.Left + 1, rect.Bottom - label.Height - 1),
            new(rect.Right - label.Width - 1, rect.Bottom - label.Height - 1),
            new(rect.Left + (rect.Width - label.Width) / 2f, rect.Top + (rect.Height - label.Height) / 2f),
        };

        RectangleF best = default;
        var bestOverlap = float.MaxValue;
        for (var i = 0; i < anchors.Length; i++)
        {
            var candidate = Clamp(new RectangleF(anchors[i].X, anchors[i].Y, label.Width, label.Height), image);
            var overlap = PlacedOverlap(candidate, placed);
            if (overlap <= 0f)
            {
                return candidate;
            }
            if (overlap < bestOverlap)
            {
                bestOverlap = overlap;
                best = candidate;
            }
        }
        return best;
    }

    private static RectangleF Clamp(RectangleF r, SizeF image)
    {
        var x = Math.Clamp(r.X, 0, Math.Max(0, image.Width - r.Width));
        var y = Math.Clamp(r.Y, 0, Math.Max(0, image.Height - r.Height));
        return new RectangleF(x, y, r.Width, r.Height);
    }

    private static float PlacedOverlap(RectangleF candidate, List<RectangleF> placed)
    {
        float total = 0;
        foreach (var p in placed)
        {
            var inter = RectangleF.Intersect(candidate, p);
            if (inter.Width > 0 && inter.Height > 0)
            {
                total += inter.Width * inter.Height;
            }
        }
        return total;
    }

    /// <summary>Draws all element outlines first, then all labels, so no outline crosses a label.</summary>
    public static void Draw(Graphics g, IReadOnlyList<RectangleF> rects, IReadOnlyList<RectangleF> labels)
    {
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        for (var i = 0; i < rects.Count; i++)
        {
            using var pen = new Pen(Palette[i % Palette.Length], 2f);
            var r = rects[i];
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }

        for (var i = 0; i < rects.Count; i++)
        {
            var color = Palette[i % Palette.Length];
            var label = labels[i];
            using (var fill = new SolidBrush(color))
            {
                g.FillRectangle(fill, label.X, label.Y, label.Width, label.Height);
            }
            using (var border = new Pen(Color.White, 1f))
            {
                g.DrawRectangle(border, label.X, label.Y, label.Width, label.Height);
            }
            var text = (i + 1).ToString(CultureInfo.InvariantCulture);
            using var textBrush = new SolidBrush(TextColorFor(color));
            var ts = g.MeasureString(text, LabelFont);
            g.DrawString(text, LabelFont, textBrush,
                label.X + (label.Width - ts.Width) / 2f, label.Y + (label.Height - ts.Height) / 2f);
        }
    }

    private static Color TextColorFor(Color c)
        => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 140 ? Color.Black : Color.White;
}
