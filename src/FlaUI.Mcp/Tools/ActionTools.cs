using System.Text.Json;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>Registers every action tool (click/type/fill/key/send_keys/scroll/drag/launch/focus/close/clipboard/wait/batch).</summary>
public static class ActionTools
{
    public static void Register(ToolRegistry registry, Services services)
    {
        registry.RegisterTool(new ClickTool(services));
        registry.RegisterTool(new TypeTool(services));
        registry.RegisterTool(new FillTool(services));
        registry.RegisterTool(new KeyTool(services, "windows_key"));
        registry.RegisterTool(new KeyTool(services, "windows_send_keys"));
        registry.RegisterTool(new ScrollTool(services));
        registry.RegisterTool(new DragTool(services));
        registry.RegisterTool(new LaunchTool(services));
        registry.RegisterTool(new FocusTool(services));
        registry.RegisterTool(new CloseTool(services));
        registry.RegisterTool(new ClipboardReadTool(services));
        registry.RegisterTool(new ClipboardWriteTool(services));
        registry.RegisterTool(new WaitTool(services));
        registry.RegisterTool(new BatchTool(services));
    }
}

/// <summary>Shared argument parsing for the input-injecting tools (buttons, modifiers, counts).</summary>
internal static class ActionArgs
{
    public static MouseButton Button(JsonElement? arguments)
    {
        var name = ToolBaseGetString(arguments, "button") ?? "left";
        return name.ToLowerInvariant() switch
        {
            "left" => MouseButton.Left,
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => throw new ToolException("bad_request", $"Unknown button '{name}'.",
                "Use button left, right or middle (default left).")
        };
    }

    public static string ButtonName(JsonElement? arguments) => ToolBaseGetString(arguments, "button") ?? "left";

    public static VirtualKeyShort[] Modifiers(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a ||
            !a.TryGetProperty("modifiers", out var list) ||
            list.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<VirtualKeyShort>();
        }
        if (list.ValueKind != JsonValueKind.Array)
        {
            throw new ToolException("bad_request", "modifiers must be an array like [\"ctrl\",\"shift\"].",
                "Pass modifiers as an array; valid entries are ctrl, shift, alt, win.");
        }
        var result = new List<VirtualKeyShort>();
        foreach (var item in list.EnumerateArray())
        {
            var token = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
            if (token == null || !KeyChords.IsModifier(token, out var key))
            {
                throw new ToolException("bad_request", $"Unknown modifier '{token ?? ""}'.",
                    "Valid modifiers are ctrl, shift, alt, win.");
            }
            if (!result.Contains(key))
            {
                result.Add(key);
            }
        }
        return result.ToArray();
    }

    /// <summary>Clamped integer argument; throws bad_request outside [min,max].</summary>
    public static int IntInRange(JsonElement? arguments, string name, int defaultValue, int min, int max, string what)
    {
        var value = ToolBaseGetInt(arguments, name, defaultValue);
        if (value < min || value > max)
        {
            throw new ToolException("bad_request", $"{name} must be between {min} and {max} (got {value}).",
                $"Pass {what} in the range {min}..{max} or omit it for the default ({defaultValue}).");
        }
        return value;
    }

    // ToolBase argument helpers are protected; local copies so shared helpers outside tool classes can use them.
    private static string? ToolBaseGetString(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a || !a.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int ToolBaseGetInt(JsonElement? arguments, string name, int defaultValue)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a || !a.TryGetProperty(name, out var prop)) return defaultValue;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt32(out var i) ? i : (int)Math.Round(prop.GetDouble()),
            JsonValueKind.String => int.TryParse(prop.GetString(), out var s) ? s : defaultValue,
            _ => defaultValue
        };
    }
}
