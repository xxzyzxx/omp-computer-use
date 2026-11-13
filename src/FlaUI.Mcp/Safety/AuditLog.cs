using System.Text.Json;
using System.Text.Json.Nodes;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Safety;

/// <summary>
/// Append-only JSONL audit log, one file per day: timestamp, tool, target app, arguments with typed text
/// redacted, result summary and the SHA-256 of any screenshot returned. <c>scripts/replay-audit.ps1</c> renders it.
/// </summary>
public sealed class AuditLog : IDisposable
{
    private static readonly HashSet<string> AlwaysRedacted = new(StringComparer.OrdinalIgnoreCase) { "text", "value", "content", "clipboard", "password", "keys", "chord", "key" };
    private readonly string? _dir;
    private readonly object _lock = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..12];
    private StreamWriter? _writer;
    private string? _writerDate;

    public AuditLog(string? directory)
    {
        _dir = string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    public bool Enabled => _dir != null;
    public string? Directory => _dir;
    public string SessionId => _sessionId;

    public void Event(string kind, object? details)
    {
        Write(new JsonObject
        {
            ["ts"] = DateTime.UtcNow.ToString("o"),
            ["session"] = _sessionId,
            ["event"] = kind,
            ["details"] = details == null ? null : JsonSerializer.SerializeToNode(details, McpProtocol.JsonOptions)
        });
    }

    public void Record(ToolBase tool, JsonElement? arguments, ToolContext ctx, McpToolResult result, TimeSpan elapsed)
    {
        var firstText = result.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? "";
        string? status = null;
        string? code = null;
        if (firstText.StartsWith('{'))
        {
            try
            {
                var node = JsonNode.Parse(firstText);
                status = node?["status"]?.GetValue<string>();
                code = node?["code"]?.GetValue<string>();
            }
            catch
            {
                // Not a JSON header; keep the raw summary.
            }
        }

        Write(new JsonObject
        {
            ["ts"] = DateTime.UtcNow.ToString("o"),
            ["session"] = _sessionId,
            ["tool"] = tool.Name,
            ["kind"] = tool.Kind.ToString().ToLowerInvariant(),
            ["target_app"] = ctx.TargetApp,
            ["args"] = Redact(arguments, tool.RedactedArguments),
            ["ok"] = result.IsError != true && status == null,
            ["status"] = status,
            ["code"] = code,
            ["is_error"] = result.IsError == true,
            ["result"] = Summarize(firstText),
            ["images"] = result.Content.Count(c => c.Type == "image"),
            ["screenshot_sha256"] = ctx.ScreenshotSha256,
            ["duration_ms"] = (int)elapsed.TotalMilliseconds
        });
    }

    public static JsonNode? Redact(JsonElement? arguments, IReadOnlyList<string> redactedKeys)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } a) return null;
        var redacted = new HashSet<string>(AlwaysRedacted, StringComparer.OrdinalIgnoreCase);
        foreach (var k in redactedKeys) redacted.Add(k);
        return RedactNode(JsonNode.Parse(a.GetRawText()), redacted);
    }

    private static JsonNode? RedactNode(JsonNode? node, HashSet<string> redacted)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var kv in obj)
                {
                    if (redacted.Contains(kv.Key) && kv.Value is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        copy[kv.Key] = $"<redacted {s.Length} chars>";
                    }
                    else if (redacted.Contains(kv.Key) && kv.Value is JsonArray secretItems)
                    {
                        copy[kv.Key] = $"<redacted {secretItems.Count} items>";
                    }
                    else
                    {
                        copy[kv.Key] = RedactNode(kv.Value, redacted);
                    }
                }
                return copy;
            case JsonArray arr:
                var list = new JsonArray();
                foreach (var item in arr) list.Add(RedactNode(item, redacted));
                return list;
            case null:
                return null;
            default:
                return JsonNode.Parse(node.ToJsonString());
        }
    }

    private static string Summarize(string text)
    {
        // Screen text is data; keep only a short, single-line prefix in the log.
        var oneLine = text.Replace("\r", "").Replace("\n", " ");
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }

    private void Write(JsonObject entry)
    {
        if (_dir == null) return;
        lock (_lock)
        {
            var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (_writer == null || _writerDate != date)
            {
                _writer?.Dispose();
                System.IO.Directory.CreateDirectory(_dir);
                var path = Path.Combine(_dir, $"audit-{date}.jsonl");
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                _writerDate = date;
            }
            _writer.WriteLine(entry.ToJsonString());
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
