using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// A click/type/scroll target resolved from <c>ref</c>, <c>mark</c> or image coordinates. All points are
/// physical pixels. Element targets get their point lazily (clickable point / live bounds centre) because
/// pattern-based actions may not need one.
/// </summary>
public sealed record ResolvedTarget(
    ResolvedElement? Element,
    Point? Physical,
    WindowInfo Window,
    string TargetDescription,
    ImageFrame? Frame);

/// <summary>
/// Shared target resolution for every action tool, in priority order: <c>ref</c> → <c>mark</c> →
/// <c>x,y</c> (interpreted against <see cref="FrameStore.Require"/>'s frame, default the latest image).
/// </summary>
public static class TargetResolver
{
    /// <summary>Resolves ref/mark to a live element; null when neither argument is present. Both given → bad_request.</summary>
    public static ResolvedElement? ResolveRefMark(ElementRegistry elements, JsonElement? args)
    {
        var refId = OptString(args, "ref");
        var mark = OptInt(args, "mark");
        if (refId != null && mark != null)
        {
            throw new ToolException("bad_request", "Pass either ref or mark, not both.",
                "Use the ref from windows_snapshot, or the mark number from the latest set-of-marks screenshot.");
        }
        if (refId != null)
        {
            return elements.Resolve(refId);
        }
        if (mark != null)
        {
            return elements.Resolve(elements.ResolveMark(mark.Value));
        }
        return null;
    }

    /// <summary>Full resolution for actions: element, point (when one exists) and the hosting window. Throws when no target is given.</summary>
    public static ResolvedTarget Resolve(Services services, JsonElement? args)
    {
        var target = ResolveOptional(services, args);
        if (target == null)
        {
            throw new ToolException("bad_request", "Provide ref, mark or x,y.",
                "Use a ref from windows_snapshot, a mark number from the latest set-of-marks screenshot, " +
                "or x,y in the pixels of a recent screenshot/zoom (optionally with image_id).");
        }
        return target;
    }

    /// <summary>Resolution that tolerates a missing target (tools where ref/mark/x,y are all optional); null when none given.</summary>
    public static ResolvedTarget? ResolveOptional(Services services, JsonElement? args)
    {
        var element = ResolveRefMark(services.Elements, args);
        if (element != null)
        {
            var window = services.WindowOf(element.Meta);
            return new ResolvedTarget(element, null, window, Describe(element.Meta), null);
        }

        var x = OptDouble(args, "x");
        var y = OptDouble(args, "y");
        if (x == null || y == null)
        {
            return null;
        }

        var (physical, frame) = ResolveImagePoint(services.Frames, OptString(args, "image_id"), x.Value, y.Value);
        var targetWindow = WindowAt(services, physical);
        var description = $"x,y ({x:0.#},{y:0.#} in image {frame.Id} → physical ({physical.X},{physical.Y}))";
        return new ResolvedTarget(null, physical, targetWindow, description, frame);
    }

    /// <summary>
    /// Maps image coordinates to physical screen coordinates through the referenced frame and validates that
    /// the point lies inside the image.
    /// </summary>
    public static (Point physical, ImageFrame frame) ResolveImagePoint(FrameStore frames, string? imageId, double x, double y)
    {
        var frame = frames.Require(imageId);
        if (!frame.ContainsImagePoint(x, y))
        {
            throw new ToolException("bad_coordinates",
                $"x,y ({x:0.#},{y:0.#}) is outside image {frame.Id} ({frame.Width}x{frame.Height} px).",
                "Take a fresh windows_screenshot and use coordinates inside it, or pass the image_id the coordinates came from.");
        }
        return (frame.ToPhysical(x, y), frame);
    }

    /// <summary>The top-level window under a physical point; the desktop shell counts as a window too.</summary>
    public static WindowInfo WindowAt(Services services, Point physical)
    {
        var hwnd = Native.WindowFromPoint(new Native.POINT { X = physical.X, Y = physical.Y });
        var root = hwnd != IntPtr.Zero ? Native.GetAncestor(hwnd, Native.GA_ROOT) : IntPtr.Zero;
        if (root == IntPtr.Zero)
        {
            root = hwnd;
        }
        if (root != IntPtr.Zero)
        {
            var info = services.Session.TryGetWindowInfo(root);
            if (info != null)
            {
                return info;
            }
        }
        throw new ToolException("window_not_found",
            $"No application window found at physical ({physical.X},{physical.Y}).",
            "Take a new windows_screenshot and pick a point inside an application window, or use a ref/mark instead.");
    }

    /// <summary>Clickable point of a resolved element; falls back to the live bounds centre.</summary>
    public static Point PointOfElement(Services services, ResolvedElement element)
    {
        try
        {
            if (element.Element.TryGetClickablePoint(out var clickable))
            {
                return clickable;
            }
        }
        catch
        {
            // Fall through to the bounds centre.
        }
        var meta = services.Elements.RefreshBounds(element);
        if (!meta.Bounds.IsEmpty)
        {
            return meta.Center;
        }
        if (!element.Meta.Bounds.IsEmpty)
        {
            return element.Meta.Center;
        }
        throw new ToolException("bad_coordinates",
            $"Element {element.Meta.Ref} has no clickable point and no bounds.",
            "Call windows_snapshot to get fresh refs, or click by x,y from a screenshot instead.");
    }

    /// <summary>Best-effort UIA element name at a physical point (used by the risky-click gate for coordinate targets).</summary>
    public static string? TryNameAtPoint(Services services, WindowInfo window, Point physical)
    {
        try
        {
            var element = services.Session.AutomationFor(window).FromPoint(physical);
            return element?.Properties.Name.ValueOrDefault;
        }
        catch
        {
            return null;
        }
    }

    public static string Describe(ElementMeta meta) =>
        $"{meta.Ref} ({meta.Role}{(string.IsNullOrEmpty(meta.Name) ? "" : $" \"{Truncate(meta.Name, 64)}\"")})";

    public static string Truncate(string? text, int max) =>
        text == null ? "" : text.Length <= max ? text : text[..max];

    private static string? OptString(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static int? OptInt(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)
            ? i
            : null;

    private static double? OptDouble(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble()
            : null;
}
