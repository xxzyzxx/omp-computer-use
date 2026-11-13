using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Safety;
using PlaywrightWindows.Mcp.Tools;

var startup = ConfigLoader.Load(args);
var config = startup.Config;
foreach (var note in startup.Notes)
{
    Console.Error.WriteLine($"[config] {note}");
}

using var audit = new AuditLog(config.AuditDir);
using var ui = new UiThread();
var state = new SafetyState(config);
var policy = new AccessPolicy(config);
IUserPrompter prompter = config.Prompts == "dialog" ? new DialogPrompter(ui, config) : new DenyPrompter();
using IOverlay overlay = config.Overlay ? new ControlOverlay(ui, config) : new NullOverlay();
ScreenCapture.CaptureScope = overlay.HideScope;
var gate = new SafetyGate(state, policy, prompter, overlay, config);
using var session = new SessionManager(config);
var services = new Services(config, session, new ElementRegistry(), new FrameStore(), gate, ui, audit);

var tools = new ToolRegistry(gate, config, audit);
tools.RegisterTool(new StatusTool(services));
SnapshotTools.Register(tools, services);
ScreenshotTools.Register(tools, services);
ActionTools.Register(tools, services);
SafetyTools.Register(tools, services);

var server = new McpServer(tools, () => AgentInstructions.Build(config));
gate.Notify = server.NotifyAsync;

using var killSwitch = new KillSwitch(ui, config.KillSwitchHotkey, () => gate.KillSwitch($"hotkey {config.KillSwitchHotkey}"));
using var inputMonitor = new InputMonitor(ui, state, config);

audit.Event("server_start", new
{
    configPath = startup.ConfigPath,
    yolo = config.Yolo,
    hotkey = config.KillSwitchHotkey,
    hotkeyRegistered = killSwitch.Registered,
    inputHooks = inputMonitor.Active,
    prompts = config.Prompts,
    notes = startup.Notes
});
Console.Error.WriteLine(
    $"windows-computer-use ready: yolo={config.Yolo} hotkey={config.KillSwitchHotkey} (registered={killSwitch.Registered}) " +
    $"inputHooks={inputMonitor.Active} prompts={config.Prompts} overlay={config.Overlay} audit={(audit.Enabled ? audit.Directory : "off")}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await server.RunAsync(cts.Token);
}
finally
{
    audit.Event("server_stop", null);
}
