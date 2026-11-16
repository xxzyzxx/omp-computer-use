using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// One entry of <c>gameProfiles</c> in config.json. A top-level window is a "game window" when its process
/// matches <see cref="Process"/> and — when given — its title and window class match the regexes. Only game
/// windows accept <c>windows_game_input</c> and get the client-area screen capture; every other window keeps
/// the UI Automation path untouched. With no profiles configured the server behaves exactly as before.
/// </summary>
public sealed class GameProfile
{
    /// <summary>Label shown in tool results (e.g. "minecraft").</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Exe name of the game process, case-insensitive, with or without ".exe" (e.g. "javaw.exe").</summary>
    [JsonPropertyName("process")]
    public string Process { get; set; } = "";

    /// <summary>Optional case-insensitive regex over the window title (e.g. "^Minecraft").</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Optional case-insensitive regex over the Win32 window class (e.g. "^GLFW30$").</summary>
    [JsonPropertyName("windowClass")]
    public string? WindowClass { get; set; }

    /// <summary>
    /// "screen": copy the window's client area from the screen while it is in the foreground (works for
    /// OpenGL/DirectX windows that PrintWindow returns blank or stale for). "printwindow": keep the default path.
    /// </summary>
    [JsonPropertyName("capture")]
    public string Capture { get; set; } = "screen";

    /// <summary>Show the "agent is driving" overlay while this game holds the control lock.</summary>
    [JsonPropertyName("overlay")]
    public bool Overlay { get; set; } = true;

    /// <summary>
    /// While a game input sequence runs, ignore mouse *movement* in the user-activity detector: games that grab
    /// the cursor re-centre it constantly, which can look like the user moving the mouse. Real key presses and
    /// mouse buttons still pause the agent, and the kill switch always works.
    /// </summary>
    [JsonPropertyName("ignoreMouseMoveYield")]
    public bool IgnoreMouseMoveYield { get; set; } = true;

    private Regex? _title;
    private Regex? _class;
    private bool _compiled;
    private string? _error;

    /// <summary>Null when the profile is usable; otherwise why it can never match.</summary>
    public string? Validate()
    {
        Compile();
        return _error;
    }

    public bool Matches(string? processName, string? title, string? className)
    {
        Compile();
        if (_error != null || !ProcessMatches(processName)) return false;
        if (_title != null && !_title.IsMatch(title ?? "")) return false;
        if (_class != null && !_class.IsMatch(className ?? "")) return false;
        return true;
    }

    public bool ProcessMatches(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(Process)) return false;
        return string.Equals(StripExe(processName), StripExe(Process), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripExe(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private void Compile()
    {
        if (_compiled) return;
        _compiled = true;
        if (string.IsNullOrWhiteSpace(Process))
        {
            _error = "process is required";
            return;
        }
        if (Capture is not ("screen" or "printwindow"))
        {
            _error = $"capture must be \"screen\" or \"printwindow\", got \"{Capture}\"";
            return;
        }
        try
        {
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var timeout = TimeSpan.FromMilliseconds(100);
            if (!string.IsNullOrWhiteSpace(Title)) _title = new Regex(Title, options, timeout);
            if (!string.IsNullOrWhiteSpace(WindowClass)) _class = new Regex(WindowClass, options, timeout);
        }
        catch (ArgumentException ex)
        {
            _error = $"bad regex: {ex.Message}";
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Process : Name;
}
