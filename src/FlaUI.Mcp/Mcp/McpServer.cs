using System.Collections.Concurrent;
using System.Text.Json;

namespace PlaywrightWindows.Mcp;

/// <summary>
/// Sink for server-initiated JSON-RPC notifications (kill switch, user activity, …).
/// </summary>
public interface INotificationSink
{
    Task NotifyAsync(string method, object? parameters);
}

/// <summary>
/// MCP server over stdio. Requests are read continuously so that
/// <c>notifications/cancelled</c> and <c>ping</c> are honoured while a long tool call
/// (batch, wait) is in flight; tool execution itself is serialized by the <see cref="ToolRegistry"/>.
/// </summary>
public class McpServer : INotificationSink
{
    private readonly ToolRegistry _toolRegistry;
    private readonly Func<string> _instructions;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();
    private StreamWriter? _writer;

    public McpServer(ToolRegistry toolRegistry, Func<string>? instructions = null)
    {
        _toolRegistry = toolRegistry;
        _instructions = instructions ?? (() => "");
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin, new System.Text.UTF8Encoding(false));
        _writer = new StreamWriter(stdout, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

        // Only JSON-RPC goes to stdout; diagnostics go to stderr.
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

        var pending = new ConcurrentDictionary<Task, byte>();
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonRpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<JsonRpcRequest>(line, McpProtocol.JsonOptions);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Malformed JSON-RPC line ignored: {ex.Message}");
                continue;
            }
            if (request == null) continue;

            var task = Task.Run(() => DispatchAsync(request, cancellationToken), CancellationToken.None);
            pending[task] = 0;
            _ = task.ContinueWith(t => pending.TryRemove(t, out _), TaskScheduler.Default);
        }

        try
        {
            await Task.WhenAll(pending.Keys).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutting down; in-flight tool calls are abandoned.
        }
    }

    private async Task DispatchAsync(JsonRpcRequest request, CancellationToken serverToken)
    {
        try
        {
            if (request.IsNotification)
            {
                HandleNotification(request);
                return;
            }

            var response = await HandleRequestAsync(request, serverToken);
            await WriteAsync(response);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error processing request {request.Method}: {ex}");
        }
    }

    private void HandleNotification(JsonRpcRequest request)
    {
        switch (request.Method)
        {
            case "notifications/initialized":
                break;
            case "notifications/cancelled":
                if (request.Params is { } p && p.TryGetProperty("requestId", out var rid))
                {
                    var key = rid.GetRawText();
                    if (_inFlight.TryGetValue(key, out var cts))
                    {
                        cts.Cancel();
                    }
                }
                break;
            default:
                break;
        }
    }

    private async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest request, CancellationToken serverToken)
    {
        try
        {
            object? result = request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "ping" => new { },
                "tools/list" => HandleToolsList(),
                "tools/call" => await HandleToolCallAsync(request, serverToken),
                _ => throw new JsonRpcException(-32601, $"Method not found: {request.Method}")
            };

            return new JsonRpcResponse { Id = request.Id, Result = result };
        }
        catch (JsonRpcException ex)
        {
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError { Code = ex.Code, Message = ex.Message }
            };
        }
        catch (Exception ex)
        {
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError { Code = -32603, Message = ex.Message }
            };
        }
    }

    private McpInitializeResult HandleInitialize(JsonRpcRequest request)
    {
        var requested = McpProtocol.DefaultProtocolVersion;
        if (request.Params is { } p && p.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String)
        {
            var v = pv.GetString();
            if (v != null && McpProtocol.SupportedProtocolVersions.Contains(v))
            {
                requested = v;
            }
        }

        var instructions = _instructions();
        return new McpInitializeResult
        {
            ProtocolVersion = requested,
            Capabilities = new McpCapabilities { Tools = new ToolsCapability { ListChanged = false } },
            ServerInfo = new McpServerInfo(),
            Instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions
        };
    }

    private McpToolsListResult HandleToolsList() => new() { Tools = _toolRegistry.GetToolDefinitions() };

    private async Task<McpToolResult> HandleToolCallAsync(JsonRpcRequest request, CancellationToken serverToken)
    {
        if (request.Params == null)
        {
            return ErrorResult("Missing params");
        }

        var callParams = JsonSerializer.Deserialize<McpToolCallParams>(request.Params.Value.GetRawText(), McpProtocol.JsonOptions);
        if (callParams == null)
        {
            return ErrorResult("Invalid tool call params");
        }

        var key = request.Id?.GetRawText() ?? Guid.NewGuid().ToString("N");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        _inFlight[key] = cts;
        try
        {
            return await _toolRegistry.ExecuteToolAsync(callParams.Name, callParams.Arguments, cts.Token);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    public async Task NotifyAsync(string method, object? parameters)
    {
        await WriteAsync(new JsonRpcNotification { Method = method, Params = parameters });
    }

    private async Task WriteAsync(object message)
    {
        var writer = _writer;
        if (writer == null) return;
        var json = JsonSerializer.Serialize(message, McpProtocol.JsonOptions);
        await _writeLock.WaitAsync();
        try
        {
            await writer.WriteLineAsync(json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static McpToolResult ErrorResult(string message) => new()
    {
        Content = new List<McpContent> { new() { Type = "text", Text = message } },
        IsError = true
    };
}

public sealed class JsonRpcException : Exception
{
    public int Code { get; }

    public JsonRpcException(int code, string message) : base(message)
    {
        Code = code;
    }
}
