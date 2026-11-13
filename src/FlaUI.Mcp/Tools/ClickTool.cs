using System.Text.Json;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Clicks a ref, mark or image coordinate. Refs with left/1/no modifiers try UIA patterns first
/// (Invoke → Toggle → SelectionItem → ExpandCollapse) and fall back to a real mouse click; everything
/// else is synthetic mouse input at the physical point. Runs the risky-control confirmation gate.
/// </summary>
public sealed class ClickTool : ToolBase
{
    private readonly Services _s;

    public ClickTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_click";
    public override ToolKind Kind => ToolKind.Action;

    public override string Description =>
        "Clicks a UI element or a screen point. Target: ref (from windows_snapshot), mark (from the latest " +
        "windows_screenshot marks=true) or x,y in the pixels of a recent screenshot/zoom (optionally image_id; " +
        "they are converted to physical screen pixels). Options: button left|right|middle (default left), count " +
        "1-3 (double/triple click), modifiers [ctrl,shift,alt,win], force_mouse (skip UIA patterns and click " +
        "for real). For a plain ref click, UIA patterns (Invoke/Toggle/Select/ExpandCollapse) are used when the " +
        "element supports them, otherwise a real mouse click at the element's clickable point; x,y always clicks " +
        "for real at that physical point. Returns method (invoke|toggle|selection_item|expand_collapse|mouse), " +
        "the physical point used, and for refs the element role/name. Risky controls (pay, delete permanently, …) " +
        "require human confirmation first.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new { type = "string", description = "Element ref like w1e5 from windows_snapshot" },
            mark = new { type = "integer", description = "Mark number from the latest set-of-marks screenshot" },
            x = new { type = "number", description = "x in pixels of the referenced image" },
            y = new { type = "number", description = "y in pixels of the referenced image" },
            image_id = new { type = "string", description = "Image the coordinates belong to (default: latest image)" },
            button = new { type = "string", @enum = new[] { "left", "right", "middle" }, description = "Mouse button (default left)" },
            count = new { type = "integer", @enum = new[] { 1, 2, 3 }, description = "Clicks: 1 single, 2 double, 3 triple (default 1)" },
            modifiers = new { type = "array", items = new { type = "string", @enum = new[] { "ctrl", "shift", "alt", "win" } }, description = "Held during a mouse click" },
            force_mouse = new { type = "boolean", description = "Always use real mouse input, even for refs (default false)" }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var target = TargetResolver.Resolve(_s, arguments);
        var button = ActionArgs.Button(arguments);
        var count = ActionArgs.IntInRange(arguments, "count", 1, 1, 3, "1=single, 2=double, 3=triple");
        var modifiers = ActionArgs.Modifiers(arguments);
        var forceMouse = GetBoolArgument(arguments, "force_mouse");
        var patternPath = target.Element != null && button == MouseButton.Left && count == 1 &&
                          modifiers.Length == 0 && !forceMouse;

        await ctx.Gate.EnsureControlAsync(target.Window, ctx);

        var elementName = target.Element?.Meta.Name;
        if (!patternPath)
        {
            var gatePoint = target.Physical ?? TargetResolver.PointOfElement(_s, target.Element!);
            elementName ??= TargetResolver.TryNameAtPoint(_s, target.Window, gatePoint);
        }
        await ctx.Gate.ConfirmRiskyAsync(elementName, "click", target.Window, ctx);
        ctx.ThrowIfCancelled();

        string? method = null;
        Point point;
        if (patternPath)
        {
            method = TryPatternClick(target.Element!.Element);
            if (method == null)
            {
                point = TargetResolver.PointOfElement(_s, target.Element);
                method = MouseClick(point, button, count, modifiers);
            }
            else
            {
                point = TargetResolver.PointOfElement(_s, target.Element);
            }
        }
        else
        {
            point = target.Physical ?? TargetResolver.PointOfElement(_s, target.Element!);
            method = MouseClick(point, button, count, modifiers);
        }

        return JsonResult(new
        {
            ok = true,
            method,
            target = target.TargetDescription,
            physical = new { x = point.X, y = point.Y },
            window = target.Window.Handle,
            button = ActionArgs.ButtonName(arguments),
            count,
            element = target.Element == null
                ? null
                : new
                {
                    role = target.Element.Meta.Role,
                    name = TargetResolver.Truncate(target.Element.Meta.Name, 64)
                }
        });
    }

    private static string? TryPatternClick(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return "invoke";
            }
        }
        catch
        {
            // Fall through to the next pattern.
        }
        try
        {
            if (element.Patterns.Toggle.IsSupported)
            {
                element.Patterns.Toggle.Pattern.Toggle();
                return "toggle";
            }
        }
        catch
        {
            // Fall through to the next pattern.
        }
        try
        {
            if (element.Patterns.SelectionItem.IsSupported)
            {
                element.Patterns.SelectionItem.Pattern.Select();
                return "selection_item";
            }
        }
        catch
        {
            // Fall through to the next pattern.
        }
        try
        {
            if (element.Patterns.ExpandCollapse.IsSupported)
            {
                var pattern = element.Patterns.ExpandCollapse.Pattern;
                var state = pattern.ExpandCollapseState.ValueOrDefault;
                if (state == ExpandCollapseState.Collapsed || state == ExpandCollapseState.PartiallyExpanded)
                {
                    pattern.Expand();
                }
                else
                {
                    pattern.Collapse();
                }
                return "expand_collapse";
            }
        }
        catch
        {
            // No pattern worked; fall back to a real mouse click.
        }
        return null;
    }

    private static string MouseClick(Point point, MouseButton button, int count, VirtualKeyShort[] modifiers)
    {
        Mouse.MoveTo(point);
        foreach (var mod in modifiers)
        {
            HeldInput.Press(mod);
        }
        try
        {
            if (count == 2)
            {
                Mouse.DoubleClick(button);
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        Thread.Sleep(40);
                    }
                    Mouse.Click(button);
                }
            }
        }
        finally
        {
            for (var i = modifiers.Length - 1; i >= 0; i--)
            {
                HeldInput.Release(modifiers[i]);
            }
        }
        return count == 1 ? "mouse" : $"mouse x{count}";
    }
}
