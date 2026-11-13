using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.IntegrationTests;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Safety;
using PlaywrightWindows.Mcp.Tools;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Result of one MCP tool call made through the registry.
/// </summary>
public sealed record ToolCallResult(JsonNode? Header, string? Untrusted, bool IsError, byte[]? Png, string? RawText);

/// <summary>
/// Shared fixture that launches the WinForms and WPF test apps once per test collection and
/// builds the same object graph as Program.cs — twice: once for the functional config
/// (yolo=true, headless) and once for the safety config (yolo=false, deny-prompter).
/// </summary>
public class TestAppFixture : IAsyncLifetime
{
    private const int WindowPollIntervalMs = 250;
    private const int WindowPollTimeoutMs = 15000;

    // Shared across both graphs.
    public UiThread Ui { get; private set; } = null!;

    // Functional config (Yolo=true).
    public ServerConfig FunctionalConfig { get; private set; } = null!;
    public ToolRegistry FunctionalRegistry { get; private set; } = null!;
    public Services FunctionalServices { get; private set; } = null!;
    public SafetyGate FunctionalGate { get; private set; } = null!;
    public SafetyState FunctionalState { get; private set; } = null!;
    public SessionManager FunctionalSession { get; private set; } = null!;
    public AuditLog FunctionalAudit { get; private set; } = null!;

    // Safety config (Yolo=false).
    public ServerConfig SafetyConfig { get; private set; } = null!;
    public ToolRegistry SafetyRegistry { get; private set; } = null!;
    public Services SafetyServices { get; private set; } = null!;
    public SafetyGate SafetyGateInstance { get; private set; } = null!;
    public SafetyState SafetyState { get; private set; } = null!;
    public SessionManager SafetySession { get; private set; } = null!;
    public AuditLog SafetyAudit { get; private set; } = null!;
    public AccessPolicy SafetyPolicy { get; private set; } = null!;
    public DenyPrompter SafetyPrompter { get; private set; } = null!;

    public string WinFormsHandle { get; private set; } = "";
    public string WpfHandle { get; private set; } = "";

    private Process? _winFormsProcess;
    private Process? _wpfProcess;
    private string _tempDir = "";
    private string _tempGrantsPath = "";

    public async Task InitializeAsync()
    {
        var winFormsPath = FindTestAppPath("WinFormsTestApp")
            ?? throw new Exception(
                "WinFormsTestApp not found. Build it first: dotnet build tests/TestApps/WinFormsTestApp");
        var wpfPath = FindTestAppPath("WpfTestApp")
            ?? throw new Exception(
                "WpfTestApp not found. Build it first: dotnet build tests/TestApps/WpfTestApp");

        _tempDir = Path.Combine(Path.GetTempPath(), "flaui-mcp-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tempGrantsPath = Path.Combine(_tempDir, "grants.json");

        FunctionalConfig = new ServerConfig
        {
            Yolo = true,
            Overlay = false,
            AuditDir = _tempDir,
            GrantsPath = _tempGrantsPath,
            Prompts = "deny",
            UserActiveWaitMs = 500,
            IdleResumeSeconds = 5,
        };
        SafetyConfig = new ServerConfig
        {
            Yolo = false,
            Overlay = false,
            AuditDir = _tempDir,
            GrantsPath = _tempGrantsPath,
            Prompts = "deny",
            PromptTimeoutSeconds = 1,
            UserActiveWaitMs = 500,
            IdleResumeSeconds = 5,
        };

        Ui = new UiThread();

        // One window/element/frame state shared by both graphs so handles and refs mean the same thing
        // whichever registry a test calls; only config, gate and tool registry differ.
        var shared = new SessionManager(FunctionalConfig);
        var elements = new ElementRegistry();
        var frames = new FrameStore();
        (FunctionalSession, FunctionalRegistry, FunctionalServices, FunctionalGate, FunctionalState, FunctionalAudit) =
            BuildGraph(FunctionalConfig, shared, elements, frames);
        (SafetySession, SafetyRegistry, SafetyServices, SafetyGateInstance, SafetyState, SafetyAudit) =
            BuildGraph(SafetyConfig, shared, elements, frames);

        _winFormsProcess = LaunchTestApp(winFormsPath);
        _wpfProcess = LaunchTestApp(wpfPath);
        var winFormsProcessId = _winFormsProcess.Id;
        var wpfProcessId = _wpfProcess.Id;

        // Poll for both windows to appear (Win32 enumeration: windows closing mid-poll cannot throw here).
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < WindowPollTimeoutMs
               && (WinFormsHandle == "" || WpfHandle == ""))
        {
            await Task.Delay(WindowPollIntervalMs);
            foreach (var w in SafetySession.ListTopLevelWindows())
            {
                if (w.Title == "FlaUI-MCP Test App" && w.Pid == winFormsProcessId && WinFormsHandle == "")
                {
                    WinFormsHandle = w.Handle;
                }
                else if (w.Title == "FlaUI-MCP WPF Test App" && w.Pid == wpfProcessId && WpfHandle == "")
                {
                    WpfHandle = w.Handle;
                }
            }
        }

        if (WinFormsHandle == "")
            throw new Exception("Timed out waiting for WinForms test app window.");
        if (WpfHandle == "")
            throw new Exception("Timed out waiting for WPF test app window.");
    }

    public Task DisposeAsync()
    {
        CloseProcess(_winFormsProcess);
        CloseProcess(_wpfProcess);
        try
        {
            FunctionalSession.Dispose(); // shared with the safety graph
        }
        catch
        {
            // best effort — the session may already be dead.
        }
        try
        {
            Ui.Dispose();
        }
        catch
        {
            // best effort — the UI thread may already be dead.
        }
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best effort.
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors Program.cs's object graph: config, audit, ui thread, safety gate, session manager,
    /// services, tool registry and the five Register calls. Test-controlled config per fixture use.
    /// </summary>
    private (SessionManager, ToolRegistry, Services, SafetyGate, SafetyState, AuditLog) BuildGraph(
        ServerConfig config, SessionManager session, ElementRegistry elements, FrameStore frames)
    {
        var audit = new AuditLog(config.AuditDir);
        var state = new SafetyState(config);
        var policy = new AccessPolicy(config);
        IUserPrompter prompter = config.Prompts == "dialog" ? new DialogPrompter(Ui, config) : new DenyPrompter();
        IOverlay overlay = config.Overlay ? new ControlOverlay(Ui, config) : new NullOverlay();
        ScreenCapture.CaptureScope = overlay.HideScope;
        var gate = new SafetyGate(state, policy, prompter, overlay, config);
        var services = new Services(config, session, elements, frames, gate, Ui, audit);
        var tools = new ToolRegistry(gate, config, audit);
        tools.RegisterTool(new StatusTool(services));
        SnapshotTools.Register(tools, services);
        ScreenshotTools.Register(tools, services);
        ActionTools.Register(tools, services);
        SafetyTools.Register(tools, services);
        return (session, tools, services, gate, state, audit);
    }

    public Window? GetWinFormsWindow() => SafetySession.GetWindow(WinFormsHandle);
    public Window? GetWpfWindow() => SafetySession.GetWindow(WpfHandle);

    /// <summary>
    /// Call an MCP tool with the given arguments by name through the registry, so tests stay
    /// independent of tool class names. Uses the functional graph by default; pass safety=true
    /// for the safety-config graph.
    /// </summary>
    public async Task<ToolCallResult> Call(string tool, object args, bool safety = false)
    {
        var registry = safety ? SafetyRegistry : FunctionalRegistry;
        var json = JsonSerializer.Serialize(args);
        using var doc = JsonDocument.Parse(json);
        var result = await registry.ExecuteToolAsync(tool, doc.RootElement.Clone());
        return ParseResult(result);
    }

    /// <summary>
    /// Call an MCP tool through the registry in the given graph.
    /// </summary>
    public async Task<ToolCallResult> Call(string tool, object args, ToolRegistry registry)
    {
        var json = JsonSerializer.Serialize(args);
        using var doc = JsonDocument.Parse(json);
        var result = await registry.ExecuteToolAsync(tool, doc.RootElement.Clone());
        return ParseResult(result);
    }

    private static ToolCallResult ParseResult(McpToolResult result)
    {
        JsonNode? header = null;
        string? untrusted = null;
        byte[]? png = null;
        string? raw = null;

        foreach (var content in result.Content)
        {
            if (content.Type == "text" && content.Text is { } text)
            {
                if (header == null && text.TrimStart().StartsWith('{'))
                {
                    try
                    {
                        header = JsonNode.Parse(text);
                    }
                    catch
                    {
                        header = null;
                    }
                }
                if (text.TrimStart().StartsWith("<untrusted_screen_text>"))
                {
                    untrusted = text
                        .TrimStart()
                        .Replace("<untrusted_screen_text>", "")
                        .Replace("</untrusted_screen_text>", "")
                        .Trim();
                }
            }
            if (content.Type == "image" && content.Data is { } data)
            {
                try
                {
                    png = Convert.FromBase64String(data);
                }
                catch
                {
                    png = null;
                }
            }
        }
        var firstText = result.Content.FirstOrDefault(c => c.Type == "text" && c.Text is { })?.Text;
        raw = firstText ?? result.Content.FirstOrDefault(c => c.Type == "image")?.Data;
        return new ToolCallResult(header, untrusted, result.IsError == true, png, raw);
    }

    /// <summary>
    /// Parse a ref like [ref=w1e12] from a snapshot line whose name contains the substring.
    /// Returns null if nothing matches.
    /// </summary>
    public static string? FindRef(string? untrustedTree, string nameSubstring, string? role = null)
    {
        if (untrustedTree == null) return null;
        foreach (var raw in untrustedTree.Split('\n'))
        {
            var line = raw.Trim();
            if (role != null && !line.Contains(role, StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(line, @"\[ref=(w\d+e\d+)\]");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// Parse a ref like [ref=w1e12] from a single snapshot line such as
    /// - button "Seven" [ref=w1e12] @x,y,wxh.
    /// </summary>
    public static string? FindRef(string line)
    {
        var m = Regex.Match(line, @"\[ref=(w\d+e\d+)\]");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Parse the quoted name from a single snapshot line such as
    /// - button "Seven" [ref=w1e12] @x,y,wxh.
    /// </summary>
    public static string? FindName(string line)
    {
        var m = Regex.Match(line, @"-?\s+\w+\s+""([^""]+)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Parse a snapshot line like - button "Seven" [ref=w1e12] @x,y,wxh.
    /// </summary>
    public static (int x, int y, int w, int h)? Bounds(string line)
    {
        var m = Regex.Match(line, @"@(-?\d+),(-?\d+),(-?\d+)x(-?\d+)");
        if (m.Success)
        {
            return (
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value));
        }
        return null;
    }

    /// <summary>
    /// Parse the full snapshot line for a ref (used by Bounds/FindRef).
    /// </summary>
    public static string? FindLine(string? untrustedTree, string nameSubstring, string? role = null)
    {
        if (untrustedTree == null) return null;
        foreach (var raw in untrustedTree.Split('\n'))
        {
            var line = raw.Trim();
            if (role != null && !line.Contains(role, StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(line, @"\[ref=w\d+e\d+\]")) return line;
        }
        return null;
    }

    /// <summary>
    /// Take a snapshot of a window and return the text.
    /// </summary>
    public string TakeSnapshot(string handle)
    {
        var r = Call("windows_snapshot", new { window = handle }).GetAwaiter().GetResult();
        return r.Untrusted ?? "";
    }

    /// <summary>
    /// Find an element ref by name in the snapshot of the given window.
    /// Takes a fresh snapshot each time — use the overload accepting a
    /// pre-built snapshot when multiple lookups against the same snapshot are needed.
    /// </summary>
    public string? FindRefByName(string handle, string name)
    {
        return FindRef(TakeSnapshot(handle), name);
    }

    /// <summary>
    /// Find an element ref by name in a pre-built snapshot string.
    /// </summary>
    public static string? FindRefInSnapshot(string snapshot, string name)
    {
        return FindRef(snapshot, name);
    }

    /// <summary>
    /// Poll for a window whose title contains the substring, up to 20 s.
    /// </summary>
    public async Task<WindowInfo?> WaitForWindowAsync(string titleSubstring, int timeoutMs = 20000, string? processName = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var info = SafetySession.ListTopLevelWindows().FirstOrDefault(
                w => w.Title != null && w.Title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase)
                     && (processName == null || string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase)));
            if (info != null) return info;
            await Task.Delay(250);
        }
        return null;
    }

    /// <summary>Poll until the HWND no longer exists (default 10 s). Returns false on timeout.</summary>
    public async Task<bool> WaitForWindowGoneAsync(IntPtr hwnd, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (SafetySession.TryGetWindowInfo(hwnd) == null) return true;
            await Task.Delay(200);
        }
        return false;
    }

    /// <summary>windows_wait for an element whose name contains <paramref name="query"/>; true when it appeared.</summary>
    public async Task<bool> WaitForElementAsync(string windowHandle, string query, int timeoutMs = 15000, bool safety = false)
    {
        var wait = await Call("windows_wait", new Dictionary<string, object?>
        {
            ["window"] = windowHandle,
            ["element"] = new Dictionary<string, object?> { ["query"] = query, ["state"] = "appears" },
            ["timeout_ms"] = timeoutMs
        }, safety);
        return !wait.IsError && wait.Header?["satisfied"]?.GetValue<bool>() == true;
    }

    private static Process LaunchTestApp(string path)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        }) ?? throw new Exception($"Could not launch {path}");
    }

    private static string? FindTestAppPath(string appName)
    {
        var exe = appName + ".exe";
        var baseDir = AppContext.BaseDirectory;

        // Walk up to the repo root (contains src/) and use the app's own build output: the exe needs its
        // .dll and .deps.json beside it, which the copies in this project's output directory lack.
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        var candidates = new List<string>();
        if (dir != null)
        {
            foreach (var cfg in new[] { "Debug", "Release" })
            {
                candidates.Add(Path.Combine(dir.FullName, "tests", "TestApps", appName, "bin", cfg, "net8.0-windows", exe));
            }
        }
        candidates.Add(Path.Combine(baseDir, "TestApps", appName, exe));
        candidates.Add(Path.Combine(baseDir, exe));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && File.Exists(Path.ChangeExtension(candidate, ".dll"))) return candidate;
        }
        return null;
    }

    private static void CloseProcess(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // best effort.
        }
    }
}

/// <summary>
/// Collection definition so all test classes share the same fixture (same app instances).
/// </summary>
[CollectionDefinition("TestApps")]
public class TestAppCollection : ICollectionFixture<TestAppFixture>
{
}
