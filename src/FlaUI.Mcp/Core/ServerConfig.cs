using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Server configuration. Loaded from JSON (see <see cref="ConfigLoader"/>); every property has a documented default
/// that is also written to <c>config.json</c> on first run. CLI flags and environment variables override the file.
/// </summary>
public sealed class ServerConfig
{
    /// <summary>
    /// YOLO / allow-all mode for fully automated, fire-and-forget use on non-critical machines.
    /// When true: every app is auto-granted <c>control</c> (including blocked apps), confirmation gates,
    /// the user-input pause and the password-field guard are skipped. The kill switch, focus safety,
    /// stale-ref checks and the audit log stay active. Overlay visibility is controlled separately.
    /// </summary>
    [JsonPropertyName("yolo")]
    public bool Yolo { get; set; } = false;

    /// <summary>Per-call tool timeout in milliseconds (batch and wait use their own, longer budgets).</summary>
    [JsonPropertyName("toolTimeoutMs")]
    public int ToolTimeoutMs { get; set; } = 30000;

    /// <summary>UIA provider timeouts: how long a single UI Automation call may block.</summary>
    [JsonPropertyName("uiaTransactionTimeoutMs")]
    public int UiaTransactionTimeoutMs { get; set; } = 5000;

    [JsonPropertyName("uiaConnectionTimeoutMs")]
    public int UiaConnectionTimeoutMs { get; set; } = 3000;

    /// <summary>Processes (exe names, case-insensitive) automated through UIA2 instead of UIA3.</summary>
    [JsonPropertyName("uia2Apps")]
    public List<string> Uia2Apps { get; set; } = new();

    /// <summary>Screenshot downscale target: the long edge is scaled to fit this many pixels.</summary>
    [JsonPropertyName("maxScreenshotEdge")]
    public int MaxScreenshotEdge { get; set; } = 1568;

    /// <summary>Snapshot output cap in characters (~4 chars per token); larger trees are truncated with a notice.</summary>
    [JsonPropertyName("snapshotMaxChars")]
    public int SnapshotMaxChars { get; set; } = 24000;

    /// <summary>Snapshot node cap after pruning.</summary>
    [JsonPropertyName("snapshotMaxNodes")]
    public int SnapshotMaxNodes { get; set; } = 600;

    /// <summary>Kill switch hotkey (RegisterHotKey syntax: modifiers + key, e.g. "ctrl+alt+pause").</summary>
    [JsonPropertyName("killSwitchHotkey")]
    public string KillSwitchHotkey { get; set; } = "ctrl+alt+pause";

    /// <summary>Seconds of no real user input before the agent may resume after the user touched mouse/keyboard.</summary>
    [JsonPropertyName("idleResumeSeconds")]
    public double IdleResumeSeconds { get; set; } = 3.0;

    /// <summary>Mouse movement below this many physical pixels is ignored by the user-input detector.</summary>
    [JsonPropertyName("userInputMouseDeadZonePx")]
    public int UserInputMouseDeadZonePx { get; set; } = 4;

    /// <summary>How long an action waits for the user to go idle before returning "user is active; waiting" (ms).</summary>
    [JsonPropertyName("userActiveWaitMs")]
    public int UserActiveWaitMs { get; set; } = 8000;

    /// <summary>Show the always-on-top "agent is driving" overlay while a control lock is held.</summary>
    [JsonPropertyName("overlay")]
    public bool Overlay { get; set; } = true;

    /// <summary>How access requests and confirmations are presented: "dialog" (topmost window) or "deny" (headless/tests).</summary>
    [JsonPropertyName("prompts")]
    public string Prompts { get; set; } = "dialog";

    /// <summary>Seconds a human has to answer an access request or confirmation dialog.</summary>
    [JsonPropertyName("promptTimeoutSeconds")]
    public int PromptTimeoutSeconds { get; set; } = 60;

    /// <summary>Apps (exe names) that always hold the given tier without asking: {"notepad.exe": "control"}.</summary>
    [JsonPropertyName("allowlist")]
    public Dictionary<string, string> Allowlist { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Exe names or window-title regexes that are refused even with a grant unless <see cref="AllowBlockedApps"/> is set.
    /// Entries ending in ".exe" match the process; other entries are case-insensitive regexes over the window title.
    /// </summary>
    [JsonPropertyName("blockedApps")]
    public List<string> BlockedApps { get; set; } = new()
    {
        "consent.exe", "CredentialUIBroker.exe", "LogonUI.exe", "SecHealthUI.exe", "SecurityHealthSystray.exe",
        "1Password.exe", "KeePass.exe", "KeePassXC.exe", "Bitwarden.exe", "LastPass.exe", "Dashlane.exe",
        "ProtonPass.exe", "NordPass.exe",
        @"\bWindows Security\b", @"\bbank(ing)?\b", @"\bPayPal\b", @"\bVenmo\b", @"\bCoinbase\b", @"\bWindows Hello\b"
    };

    /// <summary>Override for <see cref="BlockedApps"/> (also implied by <see cref="Yolo"/>).</summary>
    [JsonPropertyName("allowBlockedApps")]
    public bool AllowBlockedApps { get; set; } = false;

    /// <summary>Also block the process chain that launched this server (the harness terminal). Recommended.</summary>
    [JsonPropertyName("blockHarnessTerminal")]
    public bool BlockHarnessTerminal { get; set; } = true;

    /// <summary>Case-insensitive regexes over element names that require human confirmation before a click.</summary>
    [JsonPropertyName("riskyPatterns")]
    public List<string> RiskyPatterns { get; set; } = new()
    {
        @"\bpay(ment)?\b", @"\bpurchase\b", @"\bbuy( now)?\b", @"\bcheck ?out\b", @"\bsend\b", @"\btransfer\b",
        @"\bdelete permanently\b", @"\bempty recycle bin\b", @"\bformat\b", @"\buninstall\b", @"\bshut ?down\b",
        @"\brestart\b", @"\bfactory reset\b", @"\breset this pc\b", @"\bconfirm payment\b", @"\bplace order\b"
    };

    /// <summary>Refuse to type into fields UIA reports as password fields (IsPassword). Disabled by YOLO.</summary>
    [JsonPropertyName("passwordGuard")]
    public bool PasswordGuard { get; set; } = true;

    /// <summary>Directory for the append-only JSONL audit log. Empty disables logging.</summary>
    [JsonPropertyName("auditDir")]
    public string AuditDir { get; set; } = ConfigLoader.DefaultAuditDir;

    /// <summary>Persisted "always" grants live here.</summary>
    [JsonPropertyName("grantsPath")]
    public string GrantsPath { get; set; } = ConfigLoader.DefaultGrantsPath;

    /// <summary>Exe names treated as Electron/Chromium hosts for the thin-tree hint.</summary>
    [JsonPropertyName("electronApps")]
    public List<string> ElectronApps { get; set; } = new()
    {
        "Code.exe", "VSCodium.exe", "Discord.exe", "slack.exe", "Teams.exe", "ms-teams.exe", "obsidian.exe",
        "Notion.exe", "Figma.exe", "Postman.exe", "Signal.exe", "WhatsApp.exe", "electron.exe", "Cursor.exe"
    };

    public bool IsBlockedAppsAllowed => Yolo || AllowBlockedApps;
    public bool IsPasswordGuardActive => PasswordGuard && !Yolo;

    public bool IsUia2App(string? processName)
        => processName != null && Uia2Apps.Any(a => string.Equals(a, processName, StringComparison.OrdinalIgnoreCase));

    public bool IsElectronApp(string? processName)
        => processName != null && ElectronApps.Any(a => string.Equals(a, processName, StringComparison.OrdinalIgnoreCase));
}

public static class ConfigLoader
{
    public static readonly string DefaultRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsComputerUse");

    public static readonly string DefaultConfigPath = Path.Combine(DefaultRoot, "config.json");
    public static readonly string DefaultAuditDir = Path.Combine(DefaultRoot, "audit");
    public static readonly string DefaultGrantsPath = Path.Combine(DefaultRoot, "grants.json");

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    public sealed record StartupOptions(string ConfigPath, ServerConfig Config, List<string> Notes);

    /// <summary>
    /// Resolves configuration: file (CLI --config, env WINDOWS_MCP_CONFIG, default path; written with defaults when
    /// absent), then CLI/env overrides. Never throws on a bad file: falls back to defaults and records a note.
    /// </summary>
    public static StartupOptions Load(string[] args)
    {
        var notes = new List<string>();
        var configPath = Environment.GetEnvironmentVariable("WINDOWS_MCP_CONFIG");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
            {
                configPath = args[++i];
            }
        }
        configPath = string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath : Path.GetFullPath(configPath);

        ServerConfig config;
        if (File.Exists(configPath))
        {
            try
            {
                config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(configPath), FileOptions) ?? new ServerConfig();
            }
            catch (Exception ex)
            {
                notes.Add($"config {configPath} unreadable ({ex.Message}); using defaults");
                config = new ServerConfig();
            }
        }
        else
        {
            config = new ServerConfig();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, JsonSerializer.Serialize(config, FileOptions));
                notes.Add($"wrote default config to {configPath}");
            }
            catch (Exception ex)
            {
                notes.Add($"could not write default config to {configPath}: {ex.Message}");
            }
        }

        ApplyOverrides(config, args, notes);
        return new StartupOptions(configPath, config, notes);
    }

    private static void ApplyOverrides(ServerConfig config, string[] args, List<string> notes)
    {
        var envYolo = Environment.GetEnvironmentVariable("WINDOWS_MCP_YOLO");
        if (IsTruthy(envYolo))
        {
            config.Yolo = true;
            notes.Add("YOLO enabled by WINDOWS_MCP_YOLO");
        }
        if (IsTruthy(Environment.GetEnvironmentVariable("WINDOWS_MCP_NO_OVERLAY")))
        {
            config.Overlay = false;
        }
        var envPrompts = Environment.GetEnvironmentVariable("WINDOWS_MCP_PROMPTS");
        if (!string.IsNullOrWhiteSpace(envPrompts))
        {
            config.Prompts = envPrompts.Trim().ToLowerInvariant();
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--yolo":
                case "--allow-all":
                    config.Yolo = true;
                    notes.Add($"YOLO enabled by {args[i]}");
                    break;
                case "--no-overlay":
                    config.Overlay = false;
                    break;
                case "--prompts" when i + 1 < args.Length:
                    config.Prompts = args[++i].Trim().ToLowerInvariant();
                    break;
                case "--audit-dir" when i + 1 < args.Length:
                    config.AuditDir = args[++i];
                    break;
                case "--no-audit":
                    config.AuditDir = "";
                    break;
                case "--hotkey" when i + 1 < args.Length:
                    config.KillSwitchHotkey = args[++i];
                    break;
                case "--max-edge" when i + 1 < args.Length && int.TryParse(args[i + 1], out var edge):
                    config.MaxScreenshotEdge = edge;
                    i++;
                    break;
                case "--config":
                    i++;
                    break;
            }
        }

        if (config.Prompts is not ("dialog" or "deny"))
        {
            notes.Add($"unknown prompts mode '{config.Prompts}', using 'dialog'");
            config.Prompts = "dialog";
        }
        config.MaxScreenshotEdge = Math.Clamp(config.MaxScreenshotEdge, 256, 8192);
        config.IdleResumeSeconds = Math.Max(0, config.IdleResumeSeconds);
        config.AuditDir = string.IsNullOrWhiteSpace(config.AuditDir) ? "" : Environment.ExpandEnvironmentVariables(config.AuditDir);
        config.GrantsPath = string.IsNullOrWhiteSpace(config.GrantsPath) ? DefaultGrantsPath : Environment.ExpandEnvironmentVariables(config.GrantsPath);
    }

    private static bool IsTruthy(string? v) => v != null && v.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
