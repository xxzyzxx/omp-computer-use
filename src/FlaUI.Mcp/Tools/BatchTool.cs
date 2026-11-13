using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Runs several tool calls in one round-trip: step normalisation (tool+args or action shorthand), optional
/// wait-for-stable between action steps, stop-on-error, and a final snapshot/screenshot return (snapshot_diff
/// against the generation captured before the batch). Abort/cancel keeps partial results in the payload.
/// </summary>
public sealed class BatchTool : ToolBase
{
    public const int MaxSteps = 50;

    private static readonly HashSet<string> ActionToolNames = new(StringComparer.Ordinal)
    {
        "windows_click", "windows_type", "windows_fill", "windows_key", "windows_send_keys",
        "windows_scroll", "windows_drag", "windows_launch", "windows_focus", "windows_close", "windows_clipboard_write"
    };

    private static readonly HashSet<string> ShorthandActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "click", "type", "fill", "key", "scroll", "drag", "wait", "snapshot", "screenshot", "zoom", "find",
        "focus", "launch", "close", "clipboard_write", "clipboard_read"
    };

    private static readonly string[] ReturnModes = { "none", "snapshot_diff", "screenshot", "both" };

    private readonly Services _s;

    public BatchTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_batch";
    public override ToolKind Kind => ToolKind.Action;
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(180);

    public override string Description =>
        "Runs up to 50 tool steps in one call: steps=[{tool:\"windows_click\",args:{...}}] or shorthand " +
        "{action:\"click\"|\"type\"|\"fill\"|\"key\"|\"scroll\"|\"drag\"|\"wait\"|\"snapshot\"|\"screenshot\"|" +
        "\"zoom\"|\"find\"|\"focus\"|\"launch\"|\"close\"|\"clipboard_write\"|\"clipboard_read\", ...args}. " +
        "Options: stop_on_error (default true; also stops on user_active/needs_user_confirmation), " +
        "wait_stable_between (default true; windows_wait stable after each injecting step), return " +
        "none|snapshot_diff|screenshot|both (default snapshot_diff: a final windows_snapshot diffed against the " +
        "state before the batch), window (default foreground at batch start). Each step reports {i,tool,ok,ms," +
        "status,code,summary}; the final call's image/untrusted blocks are returned after the header. The kill " +
        "switch aborts within a step boundary and returns the partial results.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            steps = new
            {
                type = "array",
                maxItems = MaxSteps,
                description = "Steps: {tool, args} objects or {action, ...args} shorthand",
                items = new { type = "object" }
            },
            stop_on_error = new { type = "boolean", description = "Stop at the first failing step (default true)" },
            wait_stable_between = new { type = "boolean", description = "windows_wait stable after each injecting step (default true)" },
            @return = new { type = "string", @enum = ReturnModes, description = "Final return (default snapshot_diff)" },
            window = new { type = "string", description = "Window for waits and the final return (default foreground at batch start)" }
        },
        required = new[] { "steps" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var steps = NormalizeSteps(arguments);
        var stopOnError = GetBoolArgument(arguments, "stop_on_error", true);
        var waitStable = GetBoolArgument(arguments, "wait_stable_between", true);
        var ret = GetStringArgument(arguments, "return") ?? "snapshot_diff";
        if (Array.IndexOf(ReturnModes, ret.ToLowerInvariant()) < 0)
        {
            throw new ToolException("bad_request", $"Unknown return mode '{ret}'.",
                "Use return none, snapshot_diff, screenshot or both (default snapshot_diff).");
        }
        ret = ret.ToLowerInvariant();

        var window = _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));
        ctx.Gate.EnsureView(window);
        ctx.TargetApp = window.ProcessName;

        var gen = 0;
        if (ret is "snapshot_diff" or "both")
        {
            gen = _s.Elements.CurrentGeneration(window.Handle);
            if (gen == 0)
            {
                ctx.ThrowIfCancelled();
                await CallAsync(ctx, "windows_snapshot", Json(new { window = window.Handle }), null);
                gen = _s.Elements.CurrentGeneration(window.Handle);
                if (gen == 0)
                {
                    gen = 1; // snapshot ran but produced no registry generation; diff against the first
                }
            }
        }

        var results = new List<object>();
        var allOk = true;
        int? stoppedAt = null;
        for (var i = 0; i < steps.Count; i++)
        {
            try
            {
                ctx.ThrowIfCancelled();
            }
            catch (Exception)
            {
                throw Abort(ctx, results);
            }

            var (tool, args) = steps[i];
            var stepSw = Stopwatch.StartNew();
            McpToolResult result;
            try
            {
                result = await ctx.CallTool!(tool, args);
            }
            catch (ToolException ex) when (ex.Code == "aborted")
            {
                throw Abort(ctx, results);
            }
            catch (OperationCanceledException)
            {
                throw Abort(ctx, results);
            }
            stepSw.Stop();

            var (ok, status, code, summary) = Inspect(result);
            results.Add(new { i, tool, ok, ms = stepSw.ElapsedMilliseconds, status, code, summary });
            if (!ok)
            {
                allOk = false;
            }

            if (code == "aborted" || ctx.Gate.State.IsAborted)
            {
                throw Abort(ctx, results);
            }

            if (!ok && stopOnError)
            {
                stoppedAt = i;
                break;
            }

            if (waitStable && i < steps.Count - 1 && ActionToolNames.Contains(tool))
            {
                ctx.ThrowIfCancelled();
                await CallAsync(ctx, "windows_wait", Json(new { window = window.Handle, stable = true, timeout_ms = 1500 }), results);
            }
        }

        List<McpContent> extra = new();
        object? final = null;
        if (ret != "none")
        {
            try
            {
                if (ret == "screenshot")
                {
                    final = await CallReturnAsync(ctx, extra, "windows_screenshot", Json(new { window = window.Handle }));
                }
                else
                {
                    final = await CallReturnAsync(ctx, extra, "windows_snapshot",
                        Json(new { window = window.Handle, diff_since = gen }));
                    if (ret == "both")
                    {
                        await CallReturnAsync(ctx, extra, "windows_screenshot", Json(new { window = window.Handle }));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw Abort(ctx, results);
            }
        }

        var payload = new
        {
            ok = allOk,
            steps_run = results.Count,
            stopped_at = stoppedAt,
            results,
            final
        };
        var content = new List<McpContent>
        {
            new() { Type = "text", Text = JsonSerializer.Serialize(payload, McpProtocol.JsonOptions) }
        };
        content.AddRange(extra);
        return new McpToolResult { Content = content };
    }

    private async Task<string> CallAsync(ToolContext ctx, string tool, JsonElement args, List<object>? results)
    {
        try
        {
            var result = await ctx.CallTool!(tool, args);
            return result.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? "";
        }
        catch (OperationCanceledException)
        {
            throw Abort(ctx, results ?? new List<object>());
        }
    }

    /// <summary>Runs a final-return call, keeps its non-header blocks, returns its header (parsed when it is JSON).</summary>
    private async Task<object?> CallReturnAsync(ToolContext ctx, List<McpContent> extra, string tool, JsonElement args)
    {
        var result = await ctx.CallTool!(tool, args);
        for (var i = 1; i < result.Content.Count; i++)
        {
            extra.Add(result.Content[i]);
        }
        var text = result.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? "";
        if (text.TrimStart().StartsWith('{'))
        {
            try
            {
                return JsonSerializer.Deserialize<JsonElement>(text);
            }
            catch (JsonException)
            {
                // Free-text result; return it verbatim.
            }
        }
        return text;
    }

    private static ToolException Abort(ToolContext ctx, IReadOnlyList<object> results)
    {
        var reason = ctx.Gate.State.IsAborted
            ? ctx.Gate.State.AbortReason ?? "kill switch engaged"
            : "batch cancelled";
        return new ToolException("aborted", $"Batch aborted after {results.Count} step(s): {reason}.",
            "Stop. Tell the user what was aborted; the partial results are in data.")
        {
            Payload = new { ok = false, steps_run = results.Count, results }
        };
    }

    private static (bool ok, string? status, string? code, string summary) Inspect(McpToolResult result)
    {
        var first = result.Content.FirstOrDefault(c => c.Type == "text");
        var text = first?.Text;
        var summary = text == null ? "" : text.Length <= 200 ? text : text[..200];
        string? status = null;
        string? code = null;
        bool? innerOk = null;
        if (text != null && text.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                {
                    status = st.GetString();
                }
                if (doc.RootElement.TryGetProperty("ok", out var okEl))
                {
                    if (okEl.ValueKind == JsonValueKind.True) innerOk = true;
                    else if (okEl.ValueKind == JsonValueKind.False) innerOk = false;
                }
                if (result.IsError == true && doc.RootElement.TryGetProperty("code", out var cd) && cd.ValueKind == JsonValueKind.String)
                {
                    code = cd.GetString();
                }
            }
            catch (JsonException)
            {
                // Free-text error result; summary still carries it.
            }
        }

        // A control outcome (user_active / needs_user_confirmation) is not an MCP error, but it is
        // also not a successful step: the tool's own JSON says ok=false. Mirror that here so a
        // blocked step can never be reported as success.
        var ok = result.IsError != true
            && innerOk != false
            && status is not ("user_active" or "needs_user_confirmation");
        return (ok, status, code, summary);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, McpProtocol.JsonOptions);

    /// <summary>Validates the steps argument and normalises every step to (tool, args). Pure; unit-tested.</summary>
    public static List<(string Tool, JsonElement Args)> NormalizeSteps(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a ||
            !a.TryGetProperty("steps", out var stepsEl) ||
            stepsEl.ValueKind != JsonValueKind.Array)
        {
            throw new ToolException("bad_request", "steps (array) is required.",
                "Pass steps like [{action:\"click\", ref:\"w1e5\"}] or [{tool:\"windows_click\",args:{...}}].");
        }

        var items = stepsEl.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            throw new ToolException("bad_request", "steps is empty.",
                "Add at least one step, e.g. {action:\"click\", ref:\"w1e5\"}.");
        }
        if (items.Count > MaxSteps)
        {
            throw new ToolException("bad_request", $"Too many steps: {items.Count} (max {MaxSteps}).",
                $"Split the work into several windows_batch calls of at most {MaxSteps} steps.");
        }

        return items.Select(NormalizeStep).ToList();
    }

    public static (string Tool, JsonElement Args) NormalizeStep(JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("bad_request", "Each step must be an object.",
                "Use {tool:\"windows_click\",args:{...}} or shorthand {action:\"click\", ...args}.");
        }
        var node = JsonNode.Parse(step.GetRawText()) as JsonObject
            ?? throw new ToolException("bad_request", "Each step must be an object.",
                "Use {tool:\"windows_click\",args:{...}} or shorthand {action:\"click\", ...args}.");

        if (node["tool"] is JsonValue toolValue && toolValue.TryGetValue<string>(out var tool) && tool.Length > 0)
        {
            var args = node["args"] as JsonObject ?? new JsonObject();
            return (tool, JsonSerializer.SerializeToElement(args, McpProtocol.JsonOptions));
        }

        if (node["action"] is JsonValue actionValue && actionValue.TryGetValue<string>(out var action))
        {
            if (!ShorthandActions.Contains(action))
            {
                throw new ToolException("bad_request",
                    $"Unknown action '{action}'.",
                    "Use one of: " + string.Join(", ", ShorthandActions.OrderBy(x => x)) + " — or pass tool=\"windows_<name>\".");
            }
            node.Remove("action");
            return ("windows_" + action.ToLowerInvariant(), JsonSerializer.SerializeToElement(node, McpProtocol.JsonOptions));
        }

        throw new ToolException("bad_request", "Each step needs tool+args or an action shorthand.",
            "Use {tool:\"windows_click\",args:{...}} or shorthand {action:\"click\", ...args}.");
    }
}
