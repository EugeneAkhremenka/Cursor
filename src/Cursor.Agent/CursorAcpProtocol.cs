using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cursor.Agent;

internal static class CursorAcpProtocol
{
    public static async Task HandshakeAsync(AcpJsonRpcClient rpc, CancellationToken cancellationToken)
    {
        await rpc.SendAsync(
            "initialize",
            new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    fs = new { readTextFile = false, writeTextFile = false },
                    terminal = false
                },
                clientInfo = new { name = "cursor-telegram", version = "0.1.0" }
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            await rpc.SendAsync(
                "authenticate",
                new { methodId = "cursor_login" },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcpRpcException)
        {
            // Some CLI builds treat env/API-key auth as already signed in.
        }
    }

    public static async Task<string> NewSessionAsync(
        AcpJsonRpcClient rpc,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await rpc.SendAsync(
            "session/new",
            new { cwd = workingDirectory, mcpServers = Array.Empty<object>() },
            cancellationToken).ConfigureAwait(false);

        var sessionId = result?["sessionId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("ACP session/new did not return sessionId.");
        }

        return sessionId;
    }

    public static Task<JsonNode?> HandleIncomingRequestAsync(
        string method,
        JsonNode? @params,
        string permissionMode,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        _ = @params;
        _ = cancellationToken;
        logger ??= NullLogger.Instance;

        switch (method)
        {
            case "session/request_permission":
                var option = permissionMode switch
                {
                    "reject-once" => "reject-once",
                    "allow-once" => "allow-once",
                    _ => "allow-always"
                };
                return Task.FromResult<JsonNode?>(Serialize(new
                {
                    outcome = new { outcome = "selected", optionId = option }
                }));

            case "cursor/ask_question":
                return Task.FromResult<JsonNode?>(Serialize(new
                {
                    outcome = new { outcome = "skipped", reason = "no interactive UI" }
                }));

            case "cursor/create_plan":
                return Task.FromResult<JsonNode?>(Serialize(new
                {
                    outcome = new { outcome = "accepted" }
                }));

            default:
                logger.LogWarning("Unhandled ACP request {Method}; returning empty result", method);
                return Task.FromResult<JsonNode?>(new JsonObject());
        }
    }

    public static string? ReadAssistantChunk(AcpNotification notification)
    {
        var activity = ReadActivity(notification);
        return activity is { Kind: AgentActivityKind.Assistant } ? activity.Text : null;
    }

    public static AgentActivityEvent? ReadActivity(AcpNotification notification)
    {
        if (notification.Method != "session/update")
        {
            return null;
        }

        var update = notification.Params?["update"];
        var kind = TryString(update?["sessionUpdate"]);
        return kind switch
        {
            "agent_message_chunk" => ReadMessage(update, AgentActivityKind.Assistant, isChunk: true),
            "agent_message" => ReadMessage(update, AgentActivityKind.Assistant, isChunk: false),
            "agent_thought_chunk" => ReadMessage(update, AgentActivityKind.Thought, isChunk: true),
            "agent_thought" => ReadMessage(update, AgentActivityKind.Thought, isChunk: false),
            "tool_call" or "tool_call_update" => ReadTool(update),
            "plan" => ReadPlan(update),
            _ => null
        };
    }

    private static AgentActivityEvent? ReadMessage(JsonNode? update, AgentActivityKind kind, bool isChunk)
    {
        var text = ReadTextContent(update?["content"]);
        return string.IsNullOrEmpty(text)
            ? null
            : new AgentActivityEvent(kind, text, isChunk, TryString(update?["messageId"]));
    }

    private static AgentActivityEvent? ReadTool(JsonNode? update)
    {
        if (update is null)
        {
            return null;
        }

        var id = TryString(update["toolCallId"]);
        var status = TryString(update["status"]);
        var title = TryString(update["title"]);
        var name = TryString(update["name"]) ?? TryString(update["kind"]);
        var path = FirstLocationPath(update["locations"]) ?? DescribeInput(update["rawInput"]);
        var text = title;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = string.IsNullOrWhiteSpace(name) ? "tool" : name;
            if (!string.IsNullOrWhiteSpace(path))
            {
                text = $"{text} — {path}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(path)
                 && text.IndexOf(path, StringComparison.OrdinalIgnoreCase) < 0)
        {
            text = $"{text} — {ShortPath(path)}";
        }

        return new AgentActivityEvent(AgentActivityKind.Tool, text, Id: id, Status: status);
    }

    private static AgentActivityEvent? ReadPlan(JsonNode? update)
    {
        if (update?["entries"] is not JsonArray entries || entries.Count == 0)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var entry in entries)
        {
            var content = TryString(entry?["content"]);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var status = TryString(entry?["status"]) ?? "";
            var icon = status switch
            {
                "completed" => "✅",
                "in_progress" => "▶",
                _ => "•"
            };
            lines.Add($"{icon} {content}");
        }

        return lines.Count == 0
            ? null
            : new AgentActivityEvent(AgentActivityKind.Plan, string.Join("\n", lines));
    }

    internal static string? ReadTextContent(JsonNode? content)
    {
        if (content is null)
        {
            return null;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var raw))
        {
            return raw;
        }

        if (content is JsonArray array)
        {
            var parts = new List<string>();
            foreach (var item in array)
            {
                var part = ReadTextContent(item);
                if (!string.IsNullOrEmpty(part))
                {
                    parts.Add(part);
                }
            }

            return parts.Count == 0 ? null : string.Concat(parts);
        }

        var text = TryString(content["text"]);
        if (!string.IsNullOrEmpty(text))
        {
            return text;
        }

        return ReadTextContent(content["content"]);
    }

    private static string? FirstLocationPath(JsonNode? locations)
    {
        if (locations is not JsonArray array)
        {
            return null;
        }

        foreach (var item in array)
        {
            var path = TryString(item?["path"]);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? DescribeInput(JsonNode? raw)
    {
        if (raw is null)
        {
            return null;
        }

        if (raw is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return Truncate(text, 100);
        }

        if (raw is JsonObject obj)
        {
            foreach (var key in new[] { "command", "cmd", "path", "file", "filePath", "query", "pattern", "url" })
            {
                if (obj[key] is JsonValue field && field.TryGetValue<string>(out var found)
                    && !string.IsNullOrWhiteSpace(found))
                {
                    return Truncate(found, 100);
                }
            }
        }

        return null;
    }

    private static string ShortPath(string path)
    {
        var file = Path.GetFileName(path);
        return string.IsNullOrEmpty(file) ? Truncate(path, 80) : file;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static string? TryString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? Serialize(object value) =>
        System.Text.Json.JsonSerializer.SerializeToNode(value, value.GetType(), AcpJson.Options);
}
