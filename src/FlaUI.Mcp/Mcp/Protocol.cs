using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlaywrightWindows.Mcp;

/// <summary>
/// MCP Protocol message types and JSON-RPC handling
/// </summary>
public static class McpProtocol
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>Protocol revisions this server understands; the client's requested revision is echoed when it is one of these.</summary>
    public static readonly string[] SupportedProtocolVersions =
    {
        "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"
    };

    public const string DefaultProtocolVersion = "2025-06-18";
}

// JSON-RPC request
public record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("method")]
    public string Method { get; init; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    [JsonIgnore]
    public bool IsNotification => Id == null || Id.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
}

// JSON-RPC response
public record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("result")]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; init; }
}

// JSON-RPC notification (server -> client)
public record JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; init; } = "";

    [JsonPropertyName("params")]
    public object? Params { get; init; }
}

public record JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

// MCP-specific types
public record McpServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "windows-computer-use";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "0.3.0";
}

public record McpCapabilities
{
    [JsonPropertyName("tools")]
    public ToolsCapability? Tools { get; init; }
}

public record ToolsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; init; } = false;
}

public record McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = McpProtocol.DefaultProtocolVersion;

    [JsonPropertyName("capabilities")]
    public McpCapabilities Capabilities { get; init; } = new();

    [JsonPropertyName("serverInfo")]
    public McpServerInfo ServerInfo { get; init; } = new();

    /// <summary>Agent operating instructions; hosts append these to the system prompt while the server is connected.</summary>
    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }
}

public record McpToolAnnotations
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("readOnlyHint")]
    public bool? ReadOnlyHint { get; init; }

    [JsonPropertyName("destructiveHint")]
    public bool? DestructiveHint { get; init; }

    [JsonPropertyName("idempotentHint")]
    public bool? IdempotentHint { get; init; }

    [JsonPropertyName("openWorldHint")]
    public bool? OpenWorldHint { get; init; }
}

public record McpTool
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("inputSchema")]
    public object InputSchema { get; init; } = new { type = "object" };

    [JsonPropertyName("annotations")]
    public McpToolAnnotations? Annotations { get; init; }
}

public record McpToolsListResult
{
    [JsonPropertyName("tools")]
    public List<McpTool> Tools { get; init; } = new();
}

public record McpToolCallParams
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}

public record McpToolResult
{
    [JsonPropertyName("content")]
    public List<McpContent> Content { get; init; } = new();

    [JsonPropertyName("isError")]
    public bool? IsError { get; init; }
}

public record McpContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }
}
