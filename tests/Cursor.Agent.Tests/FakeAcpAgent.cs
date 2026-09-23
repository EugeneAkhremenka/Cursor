using System.Text.Json.Nodes;
using Cursor.Agent;

namespace Cursor.Agent.Tests;

internal sealed class FakeAcpAgent : IAsyncDisposable
{
    private readonly AcpJsonRpcClient _rpc;
    private int _sessions;
    private bool _alwaysDropTurn;

    public FakeAcpAgent(Stream duplex)
    {
        _rpc = new AcpJsonRpcClient(duplex, duplex);
        _rpc.IncomingRequestHandler = HandleAsync;
        _rpc.Start();
    }

    public string? LastSessionId { get; private set; }

    public int PermissionRequests { get; private set; }

    public int PromptCalls { get; private set; }

    public JsonNode? LastPrompt { get; private set; }

    private async Task<JsonNode?> HandleAsync(string method, JsonNode? @params, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                return JsonNode.Parse("""{"protocolVersion":1}""");
            case "authenticate":
                return new JsonObject();
            case "session/new":
                LastSessionId = $"sess-{Interlocked.Increment(ref _sessions)}";
                return JsonNode.Parse($$"""{"sessionId":"{{LastSessionId}}"}""");
            case "session/prompt":
                PromptCalls++;
                LastPrompt = @params?["prompt"]?.DeepClone();
                var text = @params?["prompt"]?[0]?["text"]?.GetValue<string>() ?? "";
                var sessionId = @params?["sessionId"]?.GetValue<string>() ?? LastSessionId;
                if (text.Contains("RETRIABLE_ALWAYS", StringComparison.Ordinal)
                    || (text.Contains("RETRIABLE", StringComparison.Ordinal) && PromptCalls == 1))
                {
                    throw new InvalidOperationException("RetriableError: [aborted] read ECONNRESET");
                }

                if (text.Contains("HANG", StringComparison.Ordinal))
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                if (text.Contains("NEED_PERM", StringComparison.Ordinal))
                {
                    PermissionRequests++;
                    await _rpc.SendAsync(
                        "session/request_permission",
                        new { sessionId, toolCall = new { toolCallId = "t1" } },
                        cancellationToken).ConfigureAwait(false);
                }

                if (text.Contains("FAIL", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("prompt failed");
                }

                if (text.Contains("NEED_IMAGE", StringComparison.Ordinal))
                {
                    await _rpc.SendAsync(
                        "cursor/generate_image",
                        new
                        {
                            toolCallId = "img-1",
                            description = "a red square icon",
                            filePath = "assets/red.png"
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                await _rpc.NotifyAsync(
                    "session/update",
                    new
                    {
                        sessionId,
                        update = new
                        {
                            sessionUpdate = "agent_thought_chunk",
                            content = new { type = "text", text = "thinking about it" }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                await _rpc.NotifyAsync(
                    "session/update",
                    new
                    {
                        sessionId,
                        update = new
                        {
                            sessionUpdate = "tool_call",
                            toolCallId = "t-read",
                            name = "read",
                            title = "Reading file",
                            status = "in_progress",
                            locations = new[] { new { path = "/tmp/repo/README.md" } }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                await _rpc.NotifyAsync(
                    "session/update",
                    new
                    {
                        sessionId,
                        update = new
                        {
                            sessionUpdate = "tool_call_update",
                            toolCallId = "t-read",
                            status = "completed"
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                await _rpc.NotifyAsync(
                    "session/update",
                    new
                    {
                        sessionId,
                        update = new
                        {
                            sessionUpdate = "agent_message_chunk",
                            content = new { type = "text", text = $"echo:{text}" }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                _alwaysDropTurn |= text.Contains("DROPPED_ALWAYS", StringComparison.Ordinal);

                // The real CLI ends a dead turn with end_turn and the error glued to the text.
                if (_alwaysDropTurn
                    || (text.Contains("DROPPED", StringComparison.Ordinal) && PromptCalls == 1))
                {
                    await _rpc.NotifyAsync(
                        "session/update",
                        new
                        {
                            sessionId,
                            update = new
                            {
                                sessionUpdate = "agent_message_chunk",
                                content = new
                                {
                                    type = "text",
                                    text = "\n\nError: RetriableError: [aborted] read ECONNRESET"
                                }
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                return JsonNode.Parse("""{"stopReason":"end_turn"}""");
            case "session/cancel":
                return new JsonObject();
            default:
                return new JsonObject();
        }
    }

    public ValueTask DisposeAsync() => _rpc.DisposeAsync();
}
