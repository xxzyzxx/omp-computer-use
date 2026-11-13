using System.Text.Json;
using System.Drawing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Drag & drop between two targets (ref/mark/x,y each): mouse down, optional hold, interpolated moves with
/// ~15 ms pacing, release. Held through HeldInput; releases the button on any failure or cancellation.
/// </summary>
public sealed class DragTool : ToolBase
{
    private readonly Services _s;

    public DragTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_drag";
    public override ToolKind Kind => ToolKind.Action;

    public override string Description =>
        "Drags from one target to another (e.g. to move a file in Explorer). from and to are objects each with " +
        "ref, mark or x,y (+image_id in the pixels of a recent screenshot/zoom). Options: button left (default " +
        "and only supported button), steps 1..100 (interpolated moves, default 12), hold_ms 0..2000 (pause after " +
        "mouse-down before moving, default 150 — Explorer needs it), modifiers [ctrl,shift,alt,win] (e.g. " +
        "[\"ctrl\"] to copy). Both endpoint windows need control access; the drag is refused before any input if " +
        "either is ungranted or blocked. The mouse button is released even when the drag fails or is cancelled. " +
        "Returns the physical from/to points used.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            from = new
            {
                type = "object",
                description = "Start target: { ref } | { mark } | { x, y, image_id? }",
                properties = new
                {
                    @ref = new { type = "string" },
                    mark = new { type = "integer" },
                    x = new { type = "number" },
                    y = new { type = "number" },
                    image_id = new { type = "string" }
                }
            },
            to = new
            {
                type = "object",
                description = "End target: { ref } | { mark } | { x, y, image_id? }",
                properties = new
                {
                    @ref = new { type = "string" },
                    mark = new { type = "integer" },
                    x = new { type = "number" },
                    y = new { type = "number" },
                    image_id = new { type = "string" }
                }
            },
            button = new { type = "string", @enum = new[] { "left" }, description = "Only left is supported (default left)" },
            steps = new { type = "integer", description = "Interpolated moves, 1..100 (default 12)" },
            hold_ms = new { type = "integer", description = "Pause after mouse-down, 0..2000 (default 150)" },
            modifiers = new { type = "array", items = new { type = "string", @enum = new[] { "ctrl", "shift", "alt", "win" } }, description = "Held during the drag (e.g. [\"ctrl\"] to copy)" }
        },
        required = new[] { "from", "to" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var from = GetArgument<JsonElement?>(arguments, "from");
        var to = GetArgument<JsonElement?>(arguments, "to");
        if (from == null || to == null)
        {
            throw new ToolException("bad_request", "from and to are required.",
                "Pass from/to objects with ref, mark or x,y each, e.g. from={\"ref\":\"w1e5\"}, to={\"x\":120,\"y\":80}.");
        }

        var fromTarget = TargetResolver.Resolve(_s, from);
        var toTarget = TargetResolver.Resolve(_s, to);
        var fromPoint = fromTarget.Physical ?? TargetResolver.PointOfElement(_s, fromTarget.Element!);
        var toPoint = toTarget.Physical ?? TargetResolver.PointOfElement(_s, toTarget.Element!);

        var button = ActionArgs.Button(arguments);
        if (button != MouseButton.Left)
        {
            throw new ToolException("bad_request", "Only left-button drags are supported.",
                "Omit button or pass button left.");
        }
        var steps = ActionArgs.IntInRange(arguments, "steps", 12, 1, 100, "12 interpolated drag steps");
        var holdMs = ActionArgs.IntInRange(arguments, "hold_ms", 150, 0, 2000, "a mouse-down hold of 0..2000 ms");
        var modifiers = ActionArgs.Modifiers(arguments);

        // Gate BOTH endpoint windows before any input: source and destination may live in different
        // processes, and an ungranted or blocked destination must never receive mouse input.
        await ctx.Gate.EnsureControlAsync(fromTarget.Window, ctx);
        ctx.ThrowIfCancelled();
        await ctx.Gate.EnsureControlAsync(toTarget.Window, ctx);
        ctx.ThrowIfCancelled();

        // Both endpoints must be the topmost window at their point; re-check the source without
        // activation in case bringing the destination forward covered it.
        ctx.Gate.EnsurePointerTarget(fromTarget.Window, fromPoint);
        ctx.Gate.EnsurePointerTarget(toTarget.Window, toPoint);
        ctx.Gate.EnsurePointerTarget(fromTarget.Window, fromPoint, activate: false);

        Mouse.MoveTo(fromPoint);
        var down = false;
        try
        {
            foreach (var mod in modifiers)
            {
                HeldInput.Press(mod, ctx.Cancel);
            }
            HeldInput.MouseDown(button, ctx.Cancel);
            down = true;
            // Poll during the hold and release before yielding. Use an elapsed-time deadline
            // rather than summing requested delays, which accumulate Windows timer drift.
            var until = Environment.TickCount64 + holdMs;
            while (true)
            {
                var remaining = until - Environment.TickCount64;
                if (remaining <= 0) break;
                await Task.Delay((int)Math.Min(25, remaining), ctx.Cancel);
                ctx.Gate.ThrowIfUserActive("the drag hold", ctx);
            }
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                var pt = new Point(
                    fromPoint.X + (int)Math.Round((toPoint.X - fromPoint.X) * t),
                    fromPoint.Y + (int)Math.Round((toPoint.Y - fromPoint.Y) * t));
                Mouse.MoveTo(pt);
                if (i < steps)
                {
                    await Task.Delay(15, ctx.Cancel);
                    ctx.Gate.ThrowIfUserActive("the drag move", ctx);
                }
                ctx.ThrowIfCancelled();
            }
            HeldInput.MouseUp(button);
            down = false;
        }
        catch
        {
            if (down)
            {
                try { HeldInput.MouseUp(button); } catch { /* best effort during unwind */ }
            }
            throw;
        }
        finally
        {
            for (var i = modifiers.Length - 1; i >= 0; i--)
            {
                HeldInput.Release(modifiers[i]);
            }
        }

        return JsonResult(new
        {
            ok = true,
            method = "drag",
            target = $"{fromTarget.TargetDescription} → {toTarget.TargetDescription}",
            from = new { x = fromPoint.X, y = fromPoint.Y },
            to = new { x = toPoint.X, y = toPoint.Y },
            steps,
            hold_ms = holdMs,
            window = fromTarget.Window.Handle
        });
    }
}
