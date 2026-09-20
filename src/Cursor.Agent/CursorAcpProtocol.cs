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
        if (notification.Method != "session/update")
        {
            return null;
        }

        var update = notification.Params?["update"];
        var kind = update?["sessionUpdate"]?.GetValue<string>();
        if (kind is not "agent_message_chunk")
        {
            return null;
        }

        return update?["content"]?["text"]?.GetValue<string>();
    }

    private static JsonNode? Serialize(object value) =>
        System.Text.Json.JsonSerializer.SerializeToNode(value, value.GetType(), AcpJson.Options);
}
