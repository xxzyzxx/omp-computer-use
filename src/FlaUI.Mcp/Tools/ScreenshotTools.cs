using System.Drawing;
using System.Text.Json;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>Registers the perception image tools: windows_screenshot and windows_zoom.</summary>
public static class ScreenshotTools
{
    public static void Register(ToolRegistry registry, Services services)
    {
        registry.RegisterTool(new ScreenshotTool(services));
        registry.RegisterTool(new ZoomTool(services));
    }

    /// <summary>
    /// Pure marks candidate selection: keeps interactive elements with non-empty bounds that intersect the
    /// captured physical rect and are at least 6x6 px in the final image, snapshot order preserved. When more
    /// than <paramref name="maxMarks"/> remain, the largest elements are kept (still in snapshot order).
    /// </summary>
    public static List<ElementMeta> SelectMarkCandidates(
        IReadOnlyList<ElementMeta> snapshot, Rectangle capturePhysical, double imageScale, int maxMarks)
    {
        var picked = new List<(ElementMeta Meta, long Area, int Index)>();
        for (var i = 0; i < snapshot.Count; i++)
        {
            var meta = snapshot[i];
            if (!meta.Interactive) continue;
            var b = meta.Bounds;
            if (b.Width <= 0 || b.Height <= 0 || !b.IntersectsWith(capturePhysical)) continue;
            if (b.Width * imageScale < 6 || b.Height * imageScale < 6) continue;
            picked.Add((meta, (long)b.Width * b.Height, i));
        }
        if (picked.Count <= maxMarks)
        {
            return picked.Select(p => p.Meta).ToList();
        }
        var keep = picked.OrderByDescending(p => p.Area).Take(maxMarks).Select(p => p.Index).ToHashSet();
        return picked.Where(p => keep.Contains(p.Index)).Select(p => p.Meta).ToList();
    }

    /// <summary>
    /// Paints the screen bounds of blocked top-level windows that fall inside the capture solid black (before
    /// any downscale) and returns their descriptors for the result header.
    /// </summary>
    public static List<object> RedactBlocked(Services s, Bitmap bitmap, Rectangle capturePhysical)
    {
        var redacted = new List<object>();
        foreach (var w in s.Session.ListTopLevelWindows(includeMinimized: false))
        {
            if (!s.Gate.Policy.IsBlocked(w, out _)) continue;
            var r = Rectangle.Intersect(w.Bounds, capturePhysical);
            if (r.Width <= 0 || r.Height <= 0) continue;
            using var g = Graphics.FromImage(bitmap);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, r.X - capturePhysical.X, r.Y - capturePhysical.Y, r.Width, r.Height);
            redacted.Add(new { handle = w.Handle, process = w.ProcessName });
        }
        return redacted;
    }

    /// <summary>Marks source list for a window: the last snapshot, or a fresh nested windows_snapshot call.</summary>
    internal static async Task<IReadOnlyList<ElementMeta>> SnapshotForMarksAsync(Services s, ToolContext ctx, WindowInfo window)
    {
        var nodes = s.Elements.LastSnapshot(window.Handle);
        if (nodes.Count > 0) return nodes;

        if (ctx.CallTool == null)
        {
            throw new ToolException("bad_request",
                $"No snapshot exists for {window.Handle}, so there is nothing to mark.",
                $"Call windows_snapshot window={window.Handle} interactive_only=true first, then retry with marks=true.");
        }
        var result = await ctx.CallTool("windows_snapshot",
            JsonSerializer.SerializeToElement(new { window = window.Handle, interactive_only = true }));
        if (result.IsError == true)
        {
            var message = result.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? "windows_snapshot failed.";
            throw new ToolException("bad_request", $"windows_snapshot failed while collecting marks: {message}",
                "Fix the snapshot problem first, then retry with marks=true.");
        }
        return s.Elements.LastSnapshot(window.Handle);
    }

    /// <summary>Draws marks on <paramref name="image"/> and returns the number-to-ref map; empty when nothing was marked.</summary>
    internal static Dictionary<int, string> DrawMarks(Bitmap image, ImageFrame frame, IReadOnlyList<ElementMeta> candidates)
    {
        var map = new Dictionary<int, string>();
        if (candidates.Count == 0) return map;
        var rects = candidates.Select(c => frame.FromPhysicalRect(c.Bounds)).ToList();
        using var g = Graphics.FromImage(image);
        var labelSize = MarksRenderer.MeasureLabelSize(g, candidates.Count);
        var labels = MarksRenderer.PlaceLabels(rects, new SizeF(image.Width, image.Height), labelSize);
        MarksRenderer.Draw(g, rects, labels);
        for (var i = 0; i < candidates.Count; i++)
        {
            map[i + 1] = candidates[i].Ref;
        }
        return map;
    }

    /// <summary>Disposes the working bitmap and (when different) the captured source bitmap.</summary>
    internal static void DisposeCaptured(Bitmap? working, ScreenCapture.Captured? captured)
    {
        working?.Dispose();
        if (captured != null && !ReferenceEquals(working, captured.Bitmap))
        {
            captured.Bitmap.Dispose();
        }
    }
}

/// <summary>
/// windows_screenshot: window (occlusion-safe), monitor, "all", or region capture with an exact
/// physical-pixel mapping, optional set-of-marks numbering, and redaction of blocked apps on screen captures.
/// </summary>
public sealed class ScreenshotTool : ToolBase
{
    private readonly Services _s;

    public ScreenshotTool(Services services) => _s = services;

    public override string Name => "windows_screenshot";
    public override ToolKind Kind => ToolKind.Perception;

    public override string Description =>
        "Capture pixels and get a PNG plus a coordinate mapping (image_id): every later x,y you pass to action tools " +
        "is a pixel of the returned image; physical = origin + xy/scale. Targets, most specific wins: window (captured " +
        "via PrintWindow, works when occluded), region {x,y,width,height} (space \"physical\" screen pixels, or \"image\" " +
        "pixels of image_id / the latest image), monitor (id, or \"all\" for the whole virtual screen). With nothing " +
        "given: the monitor containing the foreground window (fallback primary) - captures never span monitors unless " +
        "monitor=\"all\". marks=true numbers the interactive elements of the target window on the image (set-of-marks); " +
        "use those numbers as mark=N in action tools. The long edge is downscaled to max_edge (default 1568). On " +
        "monitor/region captures, blocked apps are blacked out and reported as redacted.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new { type = "string", description = "Window spec: handle wN, hwnd, pid:N, title or process substring." },
            monitor = new { type = "string", description = "Monitor id (1-based, see windows_status) or \"all\" for the whole virtual screen." },
            region = new
            {
                type = "object",
                description = "Region to capture instead of a whole window/monitor.",
                properties = new
                {
                    x = new { type = "integer" },
                    y = new { type = "integer" },
                    width = new { type = "integer" },
                    height = new { type = "integer" }
                },
                required = new[] { "x", "y", "width", "height" }
            },
            space = new { type = "string", @enum = new[] { "physical", "image" }, description = "region coordinate space: physical screen pixels (default) or pixels of image_id." },
            image_id = new { type = "string", description = "Frame that space=\"image\" region coordinates refer to (default: latest image)." },
            max_edge = new { type = "integer", description = "Long edge of the returned image in px (default 1568)." },
            marks = new { type = "boolean", description = "Number interactive elements on the image; address them as mark=N (default false)." },
            marks_window = new { type = "string", description = "Window whose elements get marked when capturing a monitor/region (default: window, else the foreground window)." },
            max_marks = new { type = "integer", description = "Cap on marked elements; the largest ones are kept (default 120)." }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var maxEdge = GetIntArgument(arguments, "max_edge", _s.Config.MaxScreenshotEdge);
        if (maxEdge < 16) maxEdge = _s.Config.MaxScreenshotEdge;
        var wantMarks = GetBoolArgument(arguments, "marks");
        var maxMarks = Math.Max(1, GetIntArgument(arguments, "max_marks", 120));

        WindowInfo? window = null;
        WindowInfo? marksWindow = null;
        string? frameWindow = null;
        Rectangle physical;
        int monitorId;
        List<object> redacted = new();

        var windowSpec = GetStringArgument(arguments, "window");
        var monitorSpec = GetStringArgument(arguments, "monitor");
        var region = ParseRegion(arguments);

        ScreenCapture.Captured captured;
        if (windowSpec != null)
        {
            window = _s.Session.ResolveWindow(windowSpec);
            ctx.Gate.EnsureView(window);
            ctx.TargetApp = window.ProcessName;
            captured = ScreenCapture.Window(window);
            physical = captured.Physical;
            monitorId = Monitors.ForWindow(window.Hwnd)?.Id ?? 0;
            frameWindow = window.Handle;
        }
        else
        {
            // Monitor or region capture: the pixels show whatever is on screen, so gate the foreground
            // window (when there is one) and black out blocked apps.
            var fg = _s.Session.ForegroundWindow();
            if (fg != null)
            {
                ctx.Gate.EnsureView(fg);
                ctx.TargetApp = fg.ProcessName;
            }

            if (region != null)
            {
                physical = region.Value.Space == "image"
                    ? ClampToFrame(region.Value.Rect, _s.Frames.Require(region.Value.ImageId))
                    : region.Value.Rect;
                captured = ScreenCapture.Region(physical);
                monitorId = Monitors.For(physical)?.Id ?? 0;
            }
            else if (monitorSpec is { } spec && !string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(spec, out var id))
                {
                    throw new ToolException("bad_request", $"monitor must be an id or \"all\", got '{spec}'.",
                        "Call windows_status for monitor ids, or omit monitor to capture the foreground window's monitor.");
                }
                var m = Monitors.ById(id) ?? throw new ToolException("bad_request", $"No monitor with id {id}.",
                    "Call windows_status for monitor ids, or use monitor=\"all\".");
                physical = m.Bounds;
                captured = ScreenCapture.Monitor(m);
                monitorId = m.Id;
            }
            else if (monitorSpec != null) // "all"
            {
                physical = Monitors.VirtualScreen();
                captured = ScreenCapture.Region(physical);
                monitorId = 0;
            }
            else
            {
                var monitor = (fg != null ? Monitors.ForWindow(fg.Hwnd) : null) ?? Monitors.Primary()
                    ?? throw new ToolException("bad_region", "No monitor is available to capture.",
                        "Check the display setup; this server needs at least one monitor.");
                physical = monitor.Bounds;
                captured = ScreenCapture.Monitor(monitor);
                monitorId = monitor.Id;
            }

            if (!_s.Config.IsBlockedAppsAllowed)
            {
                redacted = ScreenshotTools.RedactBlocked(_s, captured.Bitmap, physical);
            }
        }

        if (wantMarks)
        {
            var marksSpec = GetStringArgument(arguments, "marks_window");
            marksWindow = marksSpec != null ? _s.Session.ResolveWindow(marksSpec) : (window ?? _s.Session.ForegroundWindow());
            if (marksWindow == null)
            {
                throw new ToolException("bad_request", "marks=true needs a window whose elements can be marked.",
                    "Pass marks_window=<window spec> (see windows_list_windows) or focus a window first.");
            }
        }

        var (image, scale) = ImageOps.FitLongEdge(captured.Bitmap, maxEdge);
        var frame = new ImageFrame(_s.Frames.NextId("screenshot"), "screenshot", physical, image.Width, image.Height,
            scale, monitorId, frameWindow, DateTime.UtcNow);
        try
        {
            Dictionary<int, string>? map = null;
            if (wantMarks && marksWindow != null)
            {
                var nodes = await ScreenshotTools.SnapshotForMarksAsync(_s, ctx, marksWindow);
                var candidates = ScreenshotTools.SelectMarkCandidates(nodes, physical, scale, maxMarks);
                map = ScreenshotTools.DrawMarks(image, frame, candidates);
                if (map.Count > 0)
                {
                    _s.Elements.SetMarks(marksWindow.Handle, frame.Id, map);
                }
            }

            var png = ImageOps.ToPng(image);
            ctx.ScreenshotSha256 = ImageOps.Sha256Hex(png);
            _s.Frames.Add(frame);
            return JsonImageResult(new
            {
                ok = true,
                image = frame.Mapping,
                window = frameWindow != null ? window?.ToJson() : null,
                monitor = monitorId,
                method = captured.Method,
                marks = wantMarks ? new { count = map?.Count ?? 0, image_id = frame.Id, map = map ?? new Dictionary<int, string>() } : null,
                redacted = redacted.Count > 0 ? redacted : null
            }, png);
        }
        finally
        {
            ScreenshotTools.DisposeCaptured(image, captured);
        }
    }

    private static Rectangle ClampToFrame(Rectangle rect, ImageFrame frame)
    {
        var clamped = rect;
        clamped.Intersect(frame.SourcePhysical);
        if (clamped.Width <= 0 || clamped.Height <= 0)
        {
            throw new ToolException("bad_region",
                $"Region {rect.X},{rect.Y} {rect.Width}x{rect.Height} (space=image) lies outside image {frame.Id}.",
                $"Use x,y inside 0..{frame.Width - 1}/0..{frame.Height - 1} of {frame.Id}, or take a new windows_screenshot.");
        }
        return clamped;
    }

    private static (Rectangle Rect, string? ImageId, string Space)? ParseRegion(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a
            || !a.TryGetProperty("region", out var r)
            || r.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (r.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("bad_request", "region must be an object {x,y,width,height}.",
                "Pass region={\"x\":0,\"y\":0,\"width\":100,\"height\":100} or omit it.");
        }
        double Get(string name)
        {
            if (r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number) return p.GetDouble();
            throw new ToolException("bad_request", $"region is missing the number '{name}'.",
                "Pass region={\"x\":0,\"y\":0,\"width\":100,\"height\":100} or omit it.");
        }
        var x = Get("x");
        var y = Get("y");
        var w = Get("width");
        var h = Get("height");
        if (w <= 0 || h <= 0)
        {
            throw new ToolException("bad_request", $"region width/height must be positive (got {w}x{h}).",
                "Pass a region with positive width and height, or omit region.");
        }
        var space = GetStringArgument(arguments, "space") ?? "physical";
        if (space is not ("physical" or "image"))
        {
            throw new ToolException("bad_request", $"space must be \"physical\" or \"image\", got '{space}'.",
                "Omit space for physical screen pixels, or pass space=\"image\" with image_id.");
        }
        return (new Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h)),
            GetStringArgument(arguments, "image_id"), space);
    }
}

/// <summary>
/// windows_zoom: fresh, magnified pixels for a region of a previous image (or of an element's live bounds).
/// </summary>
public sealed class ZoomTool : ToolBase
{
    private const int MaxZoomEdge = 8192;
    private const int DefaultMaxMarks = 120;

    private readonly Services _s;

    public ZoomTool(Services services) => _s = services;

    public override string Name => "windows_zoom";
    public override ToolKind Kind => ToolKind.Perception;

    public override string Description =>
        "Zoom into a previous image for a closer look and get fresh pixels: pass x,y,width,height in pixels of image_id " +
        "(default: the latest screenshot or zoom - zooming a zoom is allowed), or ref/mark to zoom to that element's live " +
        "bounds plus padding physical px (default 12). scale \"auto\" (default) magnifies so the crop's long edge becomes " +
        "min(max_edge, 4x its physical size); pass a number for a fixed scale. marks=true numbers the interactive elements " +
        "inside the crop. Returns a PNG whose image_id you use for subsequent x,y coordinates.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            x = new { type = "number", description = "Left edge of the crop in image_id pixels (with y/width/height)." },
            y = new { type = "number", description = "Top edge of the crop in image_id pixels." },
            width = new { type = "number", description = "Crop width in image_id pixels." },
            height = new { type = "number", description = "Crop height in image_id pixels." },
            image_id = new { type = "string", description = "Source image (default: the latest screenshot or zoom)." },
            @ref = new { type = "string", description = "Zoom to this element's live bounds instead of x/y/width/height." },
            mark = new { type = "integer", description = "Zoom to this mark's element instead of x/y/width/height." },
            padding = new { type = "integer", description = "Extra physical px around a ref/mark crop (default 12)." },
            scale = new { description = "\"auto\" (default) or a fixed scale factor (image px per physical px)." },
            max_edge = new { type = "integer", description = "Long-edge cap for scale=\"auto\" (default 1568)." },
            marks = new { type = "boolean", description = "Number interactive elements inside the crop." },
            max_marks = new { type = "integer", description = "Cap on marked elements (default 120)." }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var frame = _s.Frames.Require(GetStringArgument(arguments, "image_id"));
        var maxEdge = GetIntArgument(arguments, "max_edge", _s.Config.MaxScreenshotEdge);
        if (maxEdge < 16) maxEdge = _s.Config.MaxScreenshotEdge;
        var maxMarks = Math.Max(1, GetIntArgument(arguments, "max_marks", DefaultMaxMarks));

        ElementMeta? refMeta = null;
        WindowInfo? sourceWindow = null;
        var refId = GetStringArgument(arguments, "ref");
        if (refId == null && HasArgument(arguments, "mark"))
        {
            refId = _s.Elements.ResolveMark(GetIntArgument(arguments, "mark", 0));
        }
        if (refId != null)
        {
            refMeta = _s.Elements.RefreshBounds(_s.Elements.Resolve(refId));
            sourceWindow = _s.WindowOf(refMeta);
            ctx.Gate.EnsureView(sourceWindow);
            ctx.TargetApp = sourceWindow.ProcessName;
        }
        else if (WindowForHandle(frame.WindowHandle) is { } frameWindow)
        {
            sourceWindow = frameWindow;
            ctx.Gate.EnsureView(sourceWindow);
            ctx.TargetApp = sourceWindow.ProcessName;
        }

        Rectangle physical;
        if (refMeta is { } meta)
        {
            var pad = Math.Max(0, GetIntArgument(arguments, "padding", 12));
            var b = meta.Bounds;
            physical = new Rectangle(b.X - pad, b.Y - pad, b.Width + 2 * pad, b.Height + 2 * pad);
        }
        else
        {
            var x = GetDoubleArgument(arguments, "x");
            var y = GetDoubleArgument(arguments, "y");
            var w = GetDoubleArgument(arguments, "width");
            var h = GetDoubleArgument(arguments, "height");
            if (x is null || y is null || w is null || h is null)
            {
                throw new ToolException("bad_request", "windows_zoom needs ref/mark or x,y,width,height.",
                    "Pass x,y,width,height in pixels of the latest image (see its mapping), or ref/mark from a snapshot.");
            }
            if (w <= 0 || h <= 0)
            {
                throw new ToolException("bad_request", $"width/height must be positive (got {w}x{h}).",
                    "Pass a positive width and height in image pixels.");
            }
            if (!frame.ContainsImagePoint(x.Value, y.Value))
            {
                throw new ToolException("bad_coordinates",
                    $"x,y ({x},{y}) is outside image {frame.Id} (0..{frame.Width - 1}, 0..{frame.Height - 1}).",
                    $"Use coordinates from image {frame.Id}'s mapping, or take a new windows_screenshot.");
            }
            physical = frame.ToPhysicalRect(x.Value, y.Value, w.Value, h.Value);
        }

        if (sourceWindow != null)
        {
            // The window may have moved since the source frame; the crop must stay inside its live bounds.
            physical.Intersect(sourceWindow.Bounds);
        }
        physical.Intersect(frame.SourcePhysical);
        if (physical.Width <= 0 || physical.Height <= 0)
        {
            throw new ToolException("bad_region",
                $"The zoom region has no overlap with image {frame.Id} ({frame.SourcePhysical.Width}x{frame.SourcePhysical.Height} physical at {frame.SourcePhysical.Location}).",
                "Take a fresh windows_screenshot, then zoom into coordinates from its mapping.");
        }

        ScreenCapture.Captured captured;
        if (sourceWindow != null)
        {
            captured = ScreenCapture.Crop(ScreenCapture.Window(sourceWindow), physical);
        }
        else
        {
            captured = ScreenCapture.Region(physical);
            if (!_s.Config.IsBlockedAppsAllowed)
            {
                ScreenshotTools.RedactBlocked(_s, captured.Bitmap, physical);
            }
        }

        var scaleArg = GetDoubleArgument(arguments, "scale");
        double scale;
        if (scaleArg is { } s && s > 0)
        {
            scale = s;
        }
        else
        {
            var longEdge = Math.Max(captured.Bitmap.Width, captured.Bitmap.Height);
            scale = Math.Min(maxEdge, 4.0 * longEdge) / longEdge;
        }
        var scaledW = Math.Max(1, (int)Math.Round(captured.Bitmap.Width * scale));
        var scaledH = Math.Max(1, (int)Math.Round(captured.Bitmap.Height * scale));
        if (Math.Max(scaledW, scaledH) > MaxZoomEdge)
        {
            captured.Bitmap.Dispose();
            throw new ToolException("bad_request",
                $"scale {scale:0.###} would produce a {scaledW}x{scaledH} image (max edge {MaxZoomEdge}).",
                "Use a smaller scale, or rely on scale=\"auto\".");
        }

        var (image, appliedScale) = ImageOps.ScaleTo(captured.Bitmap, scale);
        var zoomFrame = new ImageFrame(_s.Frames.NextId("zoom"), "zoom", captured.Physical, image.Width, image.Height,
            appliedScale, Monitors.For(captured.Physical)?.Id ?? 0,
            refMeta?.WindowHandle ?? frame.WindowHandle, DateTime.UtcNow);
        try
        {
            Dictionary<int, string>? map = null;
            if (GetBoolArgument(arguments, "marks"))
            {
                var marksWindow = sourceWindow ?? _s.Session.ForegroundWindow();
                if (marksWindow != null)
                {
                    var nodes = await ScreenshotTools.SnapshotForMarksAsync(_s, ctx, marksWindow);
                    var candidates = ScreenshotTools.SelectMarkCandidates(nodes, captured.Physical, appliedScale, maxMarks);
                    map = ScreenshotTools.DrawMarks(image, zoomFrame, candidates);
                    if (map.Count > 0)
                    {
                        _s.Elements.SetMarks(marksWindow.Handle, zoomFrame.Id, map);
                    }
                }
            }

            var png = ImageOps.ToPng(image);
            ctx.ScreenshotSha256 = ImageOps.Sha256Hex(png);
            _s.Frames.Add(zoomFrame);
            return JsonImageResult(new
            {
                ok = true,
                image = zoomFrame.Mapping,
                source_image_id = frame.Id,
                region_physical = new { x = captured.Physical.X, y = captured.Physical.Y, width = captured.Physical.Width, height = captured.Physical.Height },
                marks = map == null ? null : new { count = map.Count, image_id = zoomFrame.Id, map }
            }, png);
        }
        finally
        {
            ScreenshotTools.DisposeCaptured(image, captured);
        }
    }

    private WindowInfo? WindowForHandle(string? handle)
    {
        if (string.IsNullOrEmpty(handle)) return null;
        var hwnd = _s.Session.HwndForHandle(handle);
        return hwnd != null ? _s.Session.TryGetWindowInfo(hwnd.Value) : null;
    }
}
