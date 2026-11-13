using System.Text.Json;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Safety;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// The four Control tools: access requests, grant listing, releasing the control lock and resuming after the
/// user was active. They are never gated themselves — they are the gate's user-facing surface.
/// </summary>
public static class SafetyTools
{
    public static void Register(ToolRegistry registry, Services services)
    {
        registry.RegisterTool(new RequestAccessTool(services));
        registry.RegisterTool(new ListAccessTool(services));
        registry.RegisterTool(new ReleaseTool(services));
        registry.RegisterTool(new ResumeTool(services));
    }
}

public sealed class RequestAccessTool : ToolBase
{
    private readonly Services _s;

    public RequestAccessTool(Services services) => _s = services;

    public override string Name => "windows_request_access";
    public override ToolKind Kind => ToolKind.Control;

    /// <summary>Access dialogs can wait for a human: allow the prompt timeout plus slack.</summary>
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(_s.Config.PromptTimeoutSeconds + 10);

    public override string Description =>
        "Ask the human for permission to see or control an app. Shows a dialog on their screen with a countdown; " +
        "the human (never the model) answers. Call it before acting on an app that is not yet granted, and pass a " +
        "short honest reason. Denied or timed-out requests are not errors; do not retry without new instructions.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            apps = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Exe names to request access for, e.g. [\"notepad.exe\"]."
            },
            tier = new
            {
                type = "string",
                @enum = new[] { "view", "control" },
                description = "\"view\" for read-only access, \"control\" for input injection and state changes."
            },
            reason = new { type = "string", description = "Short honest reason shown to the human." }
        },
        required = new[] { "apps", "tier", "reason" }
    };

    public override McpToolAnnotations? Annotations => new() { ReadOnlyHint = true, DestructiveHint = false };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var apps = GetArgument<string[]>(arguments, "apps");
        if (apps == null || apps.Length == 0)
        {
            throw new ToolException("bad_request", "apps must be a non-empty array of exe names.",
                "Retry with apps=[\"notepad.exe\"], tier=\"view\"|\"control\" and a short reason.");
        }
        var tierName = GetStringArgument(arguments, "tier")?.ToLowerInvariant();
        var tier = tierName switch
        {
            "view" => AccessTier.View,
            "control" => AccessTier.Control,
            _ => AccessTier.None
        };
        var reason = GetStringArgument(arguments, "reason");
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ToolException("bad_request", "reason is required: the human sees it in the access dialog.",
                "Retry with a one-line honest reason for needing this app.");
        }

        var result = await ctx.Gate.RequestAccessAsync(apps, tier, reason, ctx);
        return JsonResult(new
        {
            ok = result.Granted,
            granted = result.Granted,
            answer = result.Answer.ToString(),
            apps = result.Apps,
            tier = result.Tier.ToString().ToLowerInvariant(),
            message = result.Message,
            next = result.Granted
                ? null
                : "Tell the human you could not get access and stop; only they can answer the dialog, " +
                  "add the app to the allowlist in config.json or enable yolo mode."
        });
    }
}

public sealed class ListAccessTool : ToolBase
{
    private readonly Services _s;

    public ListAccessTool(Services services) => _s = services;

    public override string Name => "windows_list_access";
    public override ToolKind Kind => ToolKind.Control;

    public override string Description =>
        "List the current access grants per app (view/control, session or persistent), yolo mode, blocked apps, " +
        "the harness process ids and whether the control lock is held. Use it to find out what you may touch.";

    public override object InputSchema => new { type = "object", properties = new { } };

    public override McpToolAnnotations? Annotations => new() { ReadOnlyHint = true, DestructiveHint = false };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        return Task.FromResult(JsonResult(new
        {
            ok = true,
            yolo = _s.Config.Yolo,
            grants = _s.State.Grants.Select(g => g.ToJson()).ToList(),
            blocked_apps = _s.Config.BlockedApps,
            harness_pids = _s.Gate.Policy.HarnessPids,
            control_active = _s.State.ControlActive
        }));
    }
}

public sealed class ReleaseTool : ToolBase
{
    private readonly Services _s;

    public ReleaseTool(Services services) => _s = services;

    public override string Name => "windows_release";
    public override ToolKind Kind => ToolKind.Control;

    public override string Description =>
        "Release the control lock and the overlay so the user regains the mouse and keyboard immediately. " +
        "Call it when you are done acting or before handing back to the human. Control re-arms automatically " +
        "on the next action.";

    public override object InputSchema => new { type = "object", properties = new { } };

    public override McpToolAnnotations? Annotations => new() { ReadOnlyHint = false, DestructiveHint = false };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        ctx.Gate.Release();
        return Task.FromResult(JsonResult(new
        {
            ok = true,
            released = true,
            message = "Control lock released; the user has the mouse and keyboard. Ask before acting again."
        }));
    }
}

public sealed class ResumeTool : ToolBase
{
    private readonly Services _s;

    public ResumeTool(Services services) => _s = services;

    public override string Name => "windows_resume";
    public override ToolKind Kind => ToolKind.Control;

    public override string Description =>
        "Clear the user-activity pause so actions may continue. Call this ONLY after the human explicitly said " +
        "to continue (e.g. \"go ahead\"); it does not grant access and does not override the kill switch.";

    public override object InputSchema => new { type = "object", properties = new { } };

    public override McpToolAnnotations? Annotations => new() { ReadOnlyHint = false, DestructiveHint = false };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        ctx.Gate.Resume();
        return Task.FromResult(JsonResult(new
        {
            ok = true,
            resumed = true,
            message = "User-activity pause cleared (the human said to continue)."
        }));
    }
}
