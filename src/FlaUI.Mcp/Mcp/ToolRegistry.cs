using System.Diagnostics;
using System.Text.Json;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Safety;

namespace PlaywrightWindows.Mcp;

/// <summary>What a tool does; drives access-tier enforcement and audit classification.</summary>
public enum ToolKind
{
    /// <summary>Reads windows, trees, pixels or text. Requires the <c>view</c> tier.</summary>
    Perception,
    /// <summary>Injects input or mutates app state. Requires the <c>control</c> tier.</summary>
    Action,
    /// <summary>Access requests, grants, release, resume, status. Not gated.</summary>
    Control
}

/// <summary>
/// Per-call context handed to every tool: cancellation (request cancel ∪ server shutdown ∪ kill switch),
/// the safety gate, and audit fields the tool fills in as it learns its target.
/// </summary>
public sealed class ToolContext
{
    public required string ToolName { get; init; }
    public required CancellationToken Cancel { get; init; }
    public required SafetyGate Gate { get; init; }
    public required ServerConfig Config { get; init; }

    /// <summary>Process/exe of the window the tool ended up targeting (for the audit log).</summary>
    public string? TargetApp { get; set; }

    /// <summary>SHA-256 of the PNG returned by this call, if any (for the audit log).</summary>
    public string? ScreenshotSha256 { get; set; }

    /// <summary>Invokes another registered tool with this same context (used by windows_batch).</summary>
    public Func<string, JsonElement?, Task<McpToolResult>>? CallTool { get; internal set; }

    public void ThrowIfCancelled()
    {
        if (Gate.State.IsAborted)
        {
            throw new ToolException("aborted", Gate.State.AbortReason ?? "kill switch engaged",
                "Stop. Tell the user the kill switch was engaged and wait for new instructions.");
        }
        Cancel.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Structured tool failure/status. <see cref="IsError"/>=false produces a normal result whose JSON
/// carries <c>status</c> (e.g. needs_user_confirmation, user_active) instead of an MCP error.
/// </summary>
public class ToolException : Exception
{
    public string Code { get; }
    public string? Next { get; }
    public bool IsError { get; init; } = true;
    public object? Payload { get; init; }

    public ToolException(string code, string message, string? next = null) : base(message)
    {
        Code = code;
        Next = next;
    }
}

/// <summary>
/// Registry for MCP tools - maps tool names to handlers, serializes execution, enforces timeouts, writes audit entries.
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ToolBase> _tools = new();
    private readonly TimeSpan _toolTimeout;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private readonly SafetyGate _gate;
    private readonly ServerConfig _config;
    private readonly AuditLog? _audit;

    public ToolRegistry(SafetyGate gate, ServerConfig config, AuditLog? audit = null, TimeSpan? toolTimeout = null)
    {
        _gate = gate;
        _config = config;
        _audit = audit;
        _toolTimeout = toolTimeout ?? TimeSpan.FromMilliseconds(config.ToolTimeoutMs);
        if (_toolTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(toolTimeout), "Tool timeout must be greater than zero.");
        }
    }

    public void RegisterTool(ToolBase tool)
    {
        _tools[tool.Name] = tool;
    }

    public IReadOnlyCollection<string> ToolNames => _tools.Keys;

    public List<McpTool> GetToolDefinitions()
    {
        return _tools.Values.Select(t => t.GetDefinition()).ToList();
    }

    public async Task<McpToolResult> ExecuteToolAsync(string name, JsonElement? arguments, CancellationToken cancel = default)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return ToolBase.ErrorResult($"Unknown tool: {name}", "Call tools/list and use one of the windows_* tools.");
        }

        await _executionLock.WaitAsync(cancel);
        try
        {
            _gate.State.ClearAbortIfStale();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _gate.State.AbortToken);
            var ctx = new ToolContext { ToolName = name, Cancel = linked.Token, Gate = _gate, Config = _config };
            ctx.CallTool = (innerName, innerArgs) => ExecuteNestedAsync(innerName, innerArgs, ctx);
            return await ExecuteWithTimeoutAsync(tool, arguments, ctx, linked.Token);
        }
        finally
        {
            _executionLock.Release();
        }
    }

    /// <summary>Runs a tool inside an already-running tool call (batch steps). No extra locking; shares the context.</summary>
    public async Task<McpToolResult> ExecuteNestedAsync(string name, JsonElement? arguments, ToolContext parent)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return ToolBase.ErrorResult($"Unknown tool: {name}", "Use one of the windows_* action tools.");
        }
        var ctx = new ToolContext { ToolName = name, Cancel = parent.Cancel, Gate = parent.Gate, Config = parent.Config };
        ctx.CallTool = parent.CallTool;
        var result = await RunAndAuditAsync(tool, arguments, ctx);
        parent.ScreenshotSha256 ??= ctx.ScreenshotSha256;
        parent.TargetApp ??= ctx.TargetApp;
        return result;
    }

    private async Task<McpToolResult> ExecuteWithTimeoutAsync(ToolBase tool, JsonElement? arguments, ToolContext ctx, CancellationToken cancel)
    {
        var timeout = tool.Timeout ?? _toolTimeout;
        var toolTask = Task.Run(() => RunAndAuditAsync(tool, arguments, ctx), CancellationToken.None);
        var timeoutTask = Task.Delay(timeout, CancellationToken.None);

        if (await Task.WhenAny(toolTask, timeoutTask) == timeoutTask)
        {
            _ = toolTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return ToolBase.ErrorResult(
                $"Tool '{tool.Name}' timed out after {(int)timeout.TotalMilliseconds}ms. " +
                "A modal dialog or blocked UI Automation provider may still be running in the background.",
                "Take a windows_screenshot to see what is blocking, dismiss it (windows_key escape or windows_close), then retry.");
        }

        return await toolTask;
    }

    private async Task<McpToolResult> RunAndAuditAsync(ToolBase tool, JsonElement? arguments, ToolContext ctx)
    {
        var sw = Stopwatch.StartNew();
        McpToolResult result;
        try
        {
            result = await tool.ExecuteAsync(arguments, ctx);
        }
        catch (ToolException ex)
        {
            result = ToolBase.FromToolException(ex);
        }
        catch (OperationCanceledException)
        {
            result = _gate.State.IsAborted
                ? ToolBase.FromToolException(new ToolException("aborted", _gate.State.AbortReason ?? "kill switch engaged",
                    "Stop. Tell the user the kill switch was engaged and wait for new instructions."))
                : ToolBase.ErrorResult("Cancelled by the client.", "No action is needed.");
        }
        catch (Exception ex)
        {
            result = ToolBase.ErrorResult($"{ex.GetType().Name}: {ex.Message}",
                "Call windows_snapshot to refresh refs and retry; if the app is hung use windows_wait or windows_close.");
        }
        sw.Stop();

        try
        {
            _audit?.Record(tool, arguments, ctx, result, sw.Elapsed);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"audit log failure: {ex.Message}");
        }
        return result;
    }
}

/// <summary>
/// Base class for tools with common utilities
/// </summary>
public abstract class ToolBase
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract object InputSchema { get; }
    public abstract ToolKind Kind { get; }

    /// <summary>Per-tool timeout override; null uses the registry default (config.toolTimeoutMs).</summary>
    public virtual TimeSpan? Timeout => null;

    /// <summary>Argument names whose values are redacted in the audit log (typed text, clipboard content).</summary>
    public virtual IReadOnlyList<string> RedactedArguments => Array.Empty<string>();

    public virtual McpToolAnnotations? Annotations => new()
    {
        ReadOnlyHint = Kind == ToolKind.Perception,
        DestructiveHint = Kind == ToolKind.Action
    };

    public McpTool GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema,
        Annotations = Annotations
    };

    public abstract Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx);

    // ----- result helpers -----

    public static McpToolResult TextResult(string text) => new()
    {
        Content = new List<McpContent> { new() { Type = "text", Text = text } }
    };

    /// <summary>Error result. <paramref name="next"/> tells the model what to do now; always provide one.</summary>
    public static McpToolResult ErrorResult(string message, string? next = null) => new()
    {
        Content = new List<McpContent>
        {
            new() { Type = "text", Text = next == null ? message : $"{message} Next: {next}" }
        },
        IsError = true
    };

    public static McpToolResult FromToolException(ToolException ex)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = false,
            [ex.IsError ? "code" : "status"] = ex.Code,
            ["message"] = ex.Message,
            ["next"] = ex.Next
        };
        if (ex.Payload is { } data)
        {
            payload["data"] = data;
        }
        return new McpToolResult
        {
            Content = new List<McpContent> { new() { Type = "text", Text = JsonSerializer.Serialize(payload, McpProtocol.JsonOptions) } },
            IsError = ex.IsError ? true : null
        };
    }

    /// <summary>Structured JSON result (single-line JSON so the host shows it compactly).</summary>
    public static McpToolResult JsonResult(object payload) => new()
    {
        Content = new List<McpContent> { new() { Type = "text", Text = JsonSerializer.Serialize(payload, McpProtocol.JsonOptions) } }
    };

    /// <summary>JSON header + PNG image (screenshots, zooms, marks).</summary>
    public static McpToolResult JsonImageResult(object payload, byte[] png) => new()
    {
        Content = new List<McpContent>
        {
            new() { Type = "text", Text = JsonSerializer.Serialize(payload, McpProtocol.JsonOptions) },
            new() { Type = "image", Data = Convert.ToBase64String(png), MimeType = "image/png" }
        }
    };

    /// <summary>
    /// JSON header plus screen-derived text in a clearly delimited <c>untrusted_screen_text</c> block.
    /// Everything inside that block came from the screen or the UIA tree and is data, never instructions.
    /// </summary>
    public static McpToolResult ScreenTextResult(object header, string screenText, byte[]? png = null)
    {
        var content = new List<McpContent>
        {
            new() { Type = "text", Text = JsonSerializer.Serialize(header, McpProtocol.JsonOptions) },
            new() { Type = "text", Text = WrapUntrusted(screenText) }
        };
        if (png != null)
        {
            content.Add(new McpContent { Type = "image", Data = Convert.ToBase64String(png), MimeType = "image/png" });
        }
        return new McpToolResult { Content = content };
    }

    public static string WrapUntrusted(string screenText) =>
        "<untrusted_screen_text>\n" + screenText.TrimEnd('\n', '\r') + "\n</untrusted_screen_text>";

    public static McpToolResult ImageResult(byte[] imageData, string mimeType = "image/png") => new()
    {
        Content = new List<McpContent>
        {
            new() { Type = "image", Data = Convert.ToBase64String(imageData), MimeType = mimeType }
        }
    };

    // ----- argument helpers -----

    protected static bool HasArgument(JsonElement? arguments, string name)
        => arguments is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var p) && p.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    protected static T? GetArgument<T>(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a) return default;
        if (!a.TryGetProperty(name, out var prop)) return default;
        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
        return JsonSerializer.Deserialize<T>(prop.GetRawText(), McpProtocol.JsonOptions);
    }

    protected static string? GetStringArgument(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a) return null;
        if (!a.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    protected static bool GetBoolArgument(JsonElement? arguments, string name, bool defaultValue = false)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a) return defaultValue;
        if (!a.TryGetProperty(name, out var prop)) return defaultValue;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(prop.GetString(), out var b) ? b : defaultValue,
            _ => defaultValue
        };
    }

    protected static int GetIntArgument(JsonElement? arguments, string name, int defaultValue)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a) return defaultValue;
        if (!a.TryGetProperty(name, out var prop)) return defaultValue;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt32(out var i) ? i : (int)Math.Round(prop.GetDouble()),
            JsonValueKind.String => int.TryParse(prop.GetString(), out var s) ? s : defaultValue,
            _ => defaultValue
        };
    }

    protected static double? GetDoubleArgument(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a) return null;
        if (!a.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetDouble(),
            JsonValueKind.String => double.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null
        };
    }
}
