using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cursor.Agent;

internal static class CursorAcpProtocol
{
    public static async Task<AcpPromptCapabilities> HandshakeAsync(
        AcpJsonRpcClient rpc,
        CancellationToken cancellationToken)
    {
        var initialized = await rpc.SendAsync(
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

        return ReadPromptCapabilities(initialized);
    }

    /// <summary>
    /// Reads prompt capabilities from both ACP v1 (<c>agentCapabilities.promptCapabilities.image</c>)
    /// and v2 (presence of <c>capabilities.session.prompt.image</c>). Absent = assume supported,
    /// since older CLI builds answer <c>initialize</c> without advertising anything.
    /// </summary>
    internal static AcpPromptCapabilities ReadPromptCapabilities(JsonNode? initializeResult)
    {
        if (initializeResult?["agentCapabilities"]?["promptCapabilities"] is JsonNode v1)
        {
            return new AcpPromptCapabilities(
                v1["image"]?.GetValue<bool>() ?? true,
                v1["embeddedContext"]?.GetValue<bool>() ?? true);
        }

        if (initializeResult?["capabilities"]?["session"]?["prompt"] is JsonObject v2)
        {
            return new AcpPromptCapabilities(
                v2.ContainsKey("image"),
                v2.ContainsKey("embeddedContext"));
        }

        return AcpPromptCapabilities.Unknown;
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

    public static object[] ToPromptBlocks(IReadOnlyList<PromptPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var blocks = new List<object>(parts.Count);
        foreach (var part in parts)
        {
            switch (part.Kind)
            {
                case PromptPartKind.Text:
                    if (string.IsNullOrWhiteSpace(part.Text))
                    {
                        continue;
                    }

                    blocks.Add(new { type = "text", text = part.Text });
                    break;
                case PromptPartKind.Image:
                    if (string.IsNullOrWhiteSpace(part.DataBase64) || string.IsNullOrWhiteSpace(part.MimeType))
                    {
                        throw new ArgumentException("Image prompt part needs mimeType and data.");
                    }

                    blocks.Add(string.IsNullOrWhiteSpace(part.Uri)
                        ? new { type = "image", mimeType = part.MimeType, data = part.DataBase64 }
                        : new { type = "image", mimeType = part.MimeType, data = part.DataBase64, uri = part.Uri });
                    break;
                case PromptPartKind.ResourceLink:
                    if (string.IsNullOrWhiteSpace(part.Uri) || string.IsNullOrWhiteSpace(part.Name))
                    {
                        throw new ArgumentException("Resource link needs uri and name.");
                    }

                    blocks.Add(string.IsNullOrWhiteSpace(part.MimeType)
                        ? new { type = "resource_link", uri = part.Uri, name = part.Name }
                        : new { type = "resource_link", uri = part.Uri, name = part.Name, mimeType = part.MimeType });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(parts), part.Kind, "Unknown prompt part.");
            }
        }

        return blocks.ToArray();
    }

    public static async Task<JsonNode?> HandleIncomingRequestAsync(
        string method,
        JsonNode? @params,
        string permissionMode,
        AcpClientHooks? hooks,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
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
                return Serialize(new
                {
                    outcome = new { outcome = "selected", optionId = option }
                });

            case "cursor/ask_question":
                return Serialize(new
                {
                    outcome = new { outcome = "skipped", reason = "no interactive UI" }
                });

            case "cursor/create_plan":
                return Serialize(new
                {
                    outcome = new { outcome = "accepted" }
                });

            case "cursor/generate_image":
                return await GenerateImageAsync(@params, hooks, logger, cancellationToken)
                    .ConfigureAwait(false);

            default:
                logger.LogWarning("Unhandled ACP request {Method}; returning empty result", method);
                return new JsonObject();
        }
    }

    private static async Task<JsonNode?> GenerateImageAsync(
        JsonNode? @params,
        AcpClientHooks? hooks,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var description = TryString(@params?["description"]) ?? "";
        var requested = TryString(@params?["filePath"]);
        var toolCallId = TryString(@params?["toolCallId"]);
        var references = ReadStringArray(@params?["referenceImagePaths"]);
        var cwd = hooks?.WorkingDirectory ?? "";

        if (hooks?.Images is null)
        {
            return Rejected("image generator is not configured");
        }

        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            return Rejected("working directory is not set");
        }

        if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(requested))
        {
            return Rejected("missing image description");
        }

        var dest = GeneratedImagePath.Resolve(cwd, requested, description);
        var label = string.IsNullOrWhiteSpace(description)
            ? Path.GetFileName(dest)
            : Truncate(description, 80);
        ReportImage(hooks, toolCallId, "in_progress", "генерация: " + label);

        try
        {
            if (!GeneratedImagePath.HasContent(dest))
            {
                dest = await hooks.Images
                    .GenerateAsync(description, dest, references, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!GeneratedImagePath.HasContent(dest))
            {
                GeneratedImagePath.DeleteIfIncomplete(dest);
                return Rejected("generator did not write an image");
            }

            ReportImage(hooks, toolCallId, "completed", dest);
            logger.LogInformation("Generated image {Path}", dest);
            return Serialize(new
            {
                outcome = new { outcome = "generated", filePath = dest }
            });
        }
        catch (OperationCanceledException)
        {
            GeneratedImagePath.DeleteIfIncomplete(dest);
            return Serialize(new { outcome = new { outcome = "cancelled" } });
        }
        catch (Exception ex)
        {
            GeneratedImagePath.DeleteIfIncomplete(dest);
            logger.LogWarning(ex, "cursor/generate_image failed");
            ReportImage(hooks, toolCallId, "failed", ex.Message);
            return Rejected(ex.Message);
        }
    }

    private static void ReportImage(
        AcpClientHooks hooks,
        string? toolCallId,
        string status,
        string text)
    {
        hooks.Progress?.Report(new AgentActivityEvent(
            AgentActivityKind.Image,
            text,
            Id: toolCallId,
            Status: status));
    }

    private static JsonNode? Rejected(string reason) =>
        Serialize(new { outcome = new { outcome = "rejected", reason } });

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            return [];
        }

        var items = new List<string>(array.Count);
        foreach (var item in array)
        {
            var value = TryString(item);
            if (!string.IsNullOrWhiteSpace(value))
            {
                items.Add(value);
            }
        }

        return items;
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
