using System.Text.Json;

namespace PlaywrightWindows.Mcp.Core;

public enum GameEventKind
{
    KeyDown,
    KeyUp,
    KeyTap,
    MouseMove,
    MouseTo,
    ButtonDown,
    ButtonUp,
    Click,
    Scroll,
    Text,
    Wait
}

public enum GameButton
{
    Left,
    Right,
    Middle
}

/// <summary>One step of a <c>windows_game_input</c> sequence, already validated.</summary>
public sealed record GameEvent(GameEventKind Kind)
{
    public ScanKey Key { get; init; }
    public GameButton Button { get; init; }
    /// <summary>Relative mouse delta in mouse counts (MouseMove).</summary>
    public int Dx { get; init; }
    public int Dy { get; init; }
    /// <summary>Image-pixel target (MouseTo).</summary>
    public double X { get; init; }
    public double Y { get; init; }
    public string? ImageId { get; init; }
    /// <summary>Hold time (KeyTap/Click), move duration (MouseMove) or delay (Wait), in ms.</summary>
    public int Ms { get; init; }
    /// <summary>Wheel notches, positive = up/away from the user (Scroll).</summary>
    public int Amount { get; init; }
    public string? Text { get; init; }

    /// <summary>Time this step is expected to take, for the sequence budget.</summary>
    public int PlannedMs => Kind switch
    {
        GameEventKind.KeyTap or GameEventKind.Click or GameEventKind.MouseMove or GameEventKind.Wait => Ms,
        GameEventKind.Text => (Text?.Length ?? 0) * GameSequence.TextCharMs,
        _ => 0
    };

    public string Describe() => Kind switch
    {
        GameEventKind.KeyDown => $"key_down {Key.Name}",
        GameEventKind.KeyUp => $"key_up {Key.Name}",
        GameEventKind.KeyTap => $"key {Key.Name} {Ms}ms",
        GameEventKind.MouseMove => $"mouse_move {Dx},{Dy}",
        GameEventKind.MouseTo => $"mouse_to {X:0},{Y:0}",
        GameEventKind.ButtonDown => $"button_down {Button.ToString().ToLowerInvariant()}",
        GameEventKind.ButtonUp => $"button_up {Button.ToString().ToLowerInvariant()}",
        GameEventKind.Click => $"click {Button.ToString().ToLowerInvariant()} {Ms}ms",
        GameEventKind.Scroll => $"scroll {Amount}",
        GameEventKind.Text => $"text ({Text?.Length ?? 0} chars)",
        _ => $"wait {Ms}ms"
    };
}

/// <summary>
/// Pure parser for the <c>events</c> array of <c>windows_game_input</c>. Each event is an object with a
/// <c>do</c> field. The whole sequence runs inside the server with millisecond timing, so one model round trip
/// can hold a key, turn the camera and click.
/// </summary>
public static class GameSequence
{
    public const int MaxEvents = 200;
    public const int MaxTotalMs = 30000;
    public const int MaxStepMs = 10000;
    public const int MaxDelta = 20000;
    public const int MaxTextChars = 256;
    public const int DefaultTapMs = 50;
    public const int TextCharMs = 12;

    private const string Shape =
        "Each event is {\"do\": ...}: key_down/key_up {key}; key {key, hold_ms}; mouse_move {dx, dy, ms}; " +
        "mouse_to {x, y, image_id}; button_down/button_up {button}; click {button, hold_ms}; scroll {amount}; " +
        "text {text}; wait {ms}.";

    public static IReadOnlyList<GameEvent> Parse(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a
            || !a.TryGetProperty("events", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            throw Bad("events must be an array of event objects.");
        }
        return ParseEvents(list);
    }

    public static IReadOnlyList<GameEvent> ParseEvents(JsonElement list)
    {
        var events = new List<GameEvent>();
        var total = 0L;
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (events.Count >= MaxEvents)
            {
                throw Bad($"Too many events (limit {MaxEvents}); split the sequence across calls.");
            }
            var ev = ParseOne(item, index++);
            total += ev.PlannedMs;
            if (total > MaxTotalMs)
            {
                throw Bad($"The sequence would run longer than {MaxTotalMs} ms; split it across calls.");
            }
            events.Add(ev);
        }
        if (events.Count == 0)
        {
            throw Bad("events is empty.");
        }
        return events;
    }

    private static GameEvent ParseOne(JsonElement item, int index)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw Bad($"events[{index}] must be an object.");
        }
        var verb = Str(item, "do") ?? Str(item, "action") ?? Str(item, "type");
        switch (verb?.Trim().ToLowerInvariant())
        {
            case "key_down":
                return new GameEvent(GameEventKind.KeyDown) { Key = ScanCodes.Require(Str(item, "key")) };
            case "key_up":
                return new GameEvent(GameEventKind.KeyUp) { Key = ScanCodes.Require(Str(item, "key")) };
            case "key":
            case "tap":
                return new GameEvent(GameEventKind.KeyTap)
                {
                    Key = ScanCodes.Require(Str(item, "key")),
                    Ms = Int(item, "hold_ms", DefaultTapMs, 1, MaxStepMs, index)
                };
            case "mouse_move":
            case "look":
                var dx = Int(item, "dx", 0, -MaxDelta, MaxDelta, index);
                var dy = Int(item, "dy", 0, -MaxDelta, MaxDelta, index);
                if (dx == 0 && dy == 0)
                {
                    throw Bad($"events[{index}] mouse_move needs a non-zero dx or dy (relative mouse counts).");
                }
                return new GameEvent(GameEventKind.MouseMove) { Dx = dx, Dy = dy, Ms = Int(item, "ms", 0, 0, MaxStepMs, index) };
            case "mouse_to":
                if (!item.TryGetProperty("x", out _) || !item.TryGetProperty("y", out _))
                {
                    throw Bad($"events[{index}] mouse_to needs x and y (pixels of the latest screenshot).");
                }
                return new GameEvent(GameEventKind.MouseTo)
                {
                    X = Dbl(item, "x", index),
                    Y = Dbl(item, "y", index),
                    ImageId = Str(item, "image_id")
                };
            case "button_down":
                return new GameEvent(GameEventKind.ButtonDown) { Button = Button(item, index) };
            case "button_up":
                return new GameEvent(GameEventKind.ButtonUp) { Button = Button(item, index) };
            case "click":
                return new GameEvent(GameEventKind.Click)
                {
                    Button = Button(item, index),
                    Ms = Int(item, "hold_ms", DefaultTapMs, 1, MaxStepMs, index)
                };
            case "scroll":
                var amount = Int(item, "amount", 0, -100, 100, index);
                if (amount == 0)
                {
                    throw Bad($"events[{index}] scroll needs a non-zero amount (wheel notches, positive = up).");
                }
                return new GameEvent(GameEventKind.Scroll) { Amount = amount };
            case "text":
                var text = Str(item, "text");
                if (string.IsNullOrEmpty(text) || text.Length > MaxTextChars)
                {
                    throw Bad($"events[{index}] text must be 1..{MaxTextChars} characters.");
                }
                return new GameEvent(GameEventKind.Text) { Text = text };
            case "wait":
            case "sleep":
                return new GameEvent(GameEventKind.Wait) { Ms = Int(item, "ms", 0, 1, MaxStepMs, index) };
            default:
                throw Bad($"events[{index}] has unknown do='{verb ?? ""}'.");
        }
    }

    /// <summary>
    /// Splits a relative move into <paramref name="steps"/> deltas whose sums are exactly dx and dy
    /// (rounding error is carried, never dropped).
    /// </summary>
    public static IReadOnlyList<(int Dx, int Dy)> SplitDelta(int dx, int dy, int steps)
    {
        steps = Math.Max(1, steps);
        var result = new List<(int, int)>(steps);
        int sentX = 0, sentY = 0;
        for (var i = 1; i <= steps; i++)
        {
            var targetX = (int)Math.Round((double)dx * i / steps, MidpointRounding.AwayFromZero);
            var targetY = (int)Math.Round((double)dy * i / steps, MidpointRounding.AwayFromZero);
            var stepX = targetX - sentX;
            var stepY = targetY - sentY;
            sentX = targetX;
            sentY = targetY;
            if (stepX != 0 || stepY != 0) result.Add((stepX, stepY));
        }
        return result;
    }

    /// <summary>How many chunks a relative move is sent in: ~8 ms cadence when timed, else ≤120 counts per chunk.</summary>
    public static int StepsFor(int dx, int dy, int ms)
    {
        var largest = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var steps = ms > 0 ? ms / 8 : (int)Math.Ceiling(largest / 120.0);
        return Math.Clamp(steps, 1, Math.Max(1, Math.Min(largest, 500)));
    }

    private static GameButton Button(JsonElement item, int index)
    {
        var name = Str(item, "button") ?? "left";
        return name.Trim().ToLowerInvariant() switch
        {
            "left" => GameButton.Left,
            "right" => GameButton.Right,
            "middle" => GameButton.Middle,
            _ => throw Bad($"events[{index}] has unknown button '{name}' (use left, right or middle).")
        };
    }

    private static string? Str(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null
        };
    }

    private static int Int(JsonElement item, string name, int defaultValue, int min, int max, int index)
    {
        var value = defaultValue;
        if (item.TryGetProperty(name, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number)
            {
                value = p.TryGetInt32(out var i) ? i : (int)Math.Round(p.GetDouble());
            }
            else if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s))
            {
                value = s;
            }
            else if (p.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                throw Bad($"events[{index}].{name} must be an integer.");
            }
        }
        if (value < min || value > max)
        {
            throw Bad($"events[{index}].{name} must be between {min} and {max} (got {value}).");
        }
        return value;
    }

    private static double Dbl(JsonElement item, string name, int index)
    {
        var p = item.GetProperty(name);
        if (p.ValueKind == JsonValueKind.Number) return p.GetDouble();
        if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }
        throw Bad($"events[{index}].{name} must be a number.");
    }

    private static ToolException Bad(string message) => new("bad_request", message, Shape);
}
