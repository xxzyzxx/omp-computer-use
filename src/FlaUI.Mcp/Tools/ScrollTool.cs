using System.Text.Json;
using System.Drawing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Scrolls a ref, mark or point: ScrollPattern on refs when available (SmallIncrement steps or SetScrollPercent),
/// otherwise the mouse wheel/horizontal wheel at the physical point.
/// </summary>
public sealed class ScrollTool : ToolBase
{
    private readonly Services _s;

    public ScrollTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_scroll";
    public override ToolKind Kind => ToolKind.Action;

    public override string Description =>
        "Scrolls a target. Target: ref/mark (preferred; the element's Scroll pattern is used when it has one) or " +
        "x,y (+image_id) in the pixels of a recent screenshot/zoom. direction up|down|left|right (default down), " +
        "amount in wheel notches 1..20 (default 3), percent 0-100 (absolute scroll position; only used when the " +
        "ref's element supports Scroll — up/down set the vertical, left/right the horizontal percent). Returns " +
        "method (scroll_pattern|scroll_percent|mouse_wheel), the direction/amount applied and the physical point " +
        "when the wheel was used.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new { type = "string", description = "Element ref from windows_snapshot" },
            mark = new { type = "integer", description = "Mark number from the latest set-of-marks screenshot" },
            x = new { type = "number", description = "x in pixels of the referenced image" },
            y = new { type = "number", description = "y in pixels of the referenced image" },
            image_id = new { type = "string", description = "Image the coordinates belong to (default: latest image)" },
            direction = new { type = "string", @enum = new[] { "up", "down", "left", "right" }, description = "Scroll direction (default down)" },
            amount = new { type = "integer", description = "Wheel notches, 1..20 (default 3)" },
            percent = new { type = "number", description = "Absolute scroll position 0-100 (needs a ref with Scroll support)" }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var target = TargetResolver.Resolve(_s, arguments);
        var direction = (GetStringArgument(arguments, "direction") ?? "down").ToLowerInvariant();
        if (direction is not ("up" or "down" or "left" or "right"))
        {
            throw new ToolException("bad_request", $"Unknown direction '{direction}'.",
                "Use direction up, down, left or right.");
        }
        var amount = ActionArgs.IntInRange(arguments, "amount", 3, 1, 20, "a scroll of 1..20 notches");
        var percent = GetDoubleArgument(arguments, "percent");

        await ctx.Gate.EnsureControlAsync(target.Window, ctx);
        ctx.ThrowIfCancelled();

        var horizontal = direction is "left" or "right";
        string method;
        Point? point = target.Physical;

        var scrollPattern = target.Element?.Element.Patterns.Scroll.IsSupported == true
            ? target.Element!.Element.Patterns.Scroll.Pattern
            : null;

        if (scrollPattern != null && percent is { } p)
        {
            if (p < 0 || p > 100)
            {
                throw new ToolException("bad_request", $"percent must be 0-100 (got {p:0.#}).",
                    "Pass percent between 0 and 100, or omit it to scroll by notches.");
            }
            if (horizontal)
            {
                scrollPattern.SetScrollPercent(p, scrollPattern.VerticalScrollPercent.ValueOrDefault);
            }
            else
            {
                scrollPattern.SetScrollPercent(scrollPattern.HorizontalScrollPercent.ValueOrDefault, p);
            }
            method = "scroll_percent";
        }
        else if (scrollPattern != null)
        {
            var h = direction == "left" ? ScrollAmount.SmallDecrement
                  : direction == "right" ? ScrollAmount.SmallIncrement
                  : ScrollAmount.NoAmount;
            var v = direction == "up" ? ScrollAmount.SmallDecrement
                  : direction == "down" ? ScrollAmount.SmallIncrement
                  : ScrollAmount.NoAmount;
            for (var i = 0; i < amount; i++)
            {
                ctx.ThrowIfCancelled();
                scrollPattern.Scroll(h, v);
            }
            method = "scroll_pattern";
        }
        else
        {
            point ??= TargetResolver.PointOfElement(_s, target.Element!);
            Mouse.MoveTo(point.Value);
            if (horizontal)
            {
                Mouse.HorizontalScroll(direction == "right" ? amount : -amount);
            }
            else
            {
                Mouse.Scroll(direction == "up" ? amount : -amount);
            }
            method = "mouse_wheel";
        }

        return JsonResult(new
        {
            ok = true,
            method,
            target = target.TargetDescription,
            direction,
            amount,
            percent,
            physical = point == null ? null : (object)new { x = point.Value.X, y = point.Value.Y },
            window = target.Window.Handle
        });
    }
}
