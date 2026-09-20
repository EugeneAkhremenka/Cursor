using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cursor.Agent;

internal sealed class AcpJsonRpcClient : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextId;
    private Task? _readLoop;
    private bool _disposed;

    public AcpJsonRpcClient(Stream input, Stream output, ILogger? logger = null)
        : this(CreateReader(input), CreateWriter(output), logger)
    {
    }

    public AcpJsonRpcClient(StreamReader reader, StreamWriter writer, ILogger? logger = null)
    {
        _reader = reader;
        _writer = writer;
        _writer.AutoFlush = true;
        _writer.NewLine = "\n";
        _logger = logger ?? NullLogger.Instance;
    }

    private static StreamReader CreateReader(Stream input)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new StreamReader(input, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
    }

    private static StreamWriter CreateWriter(Stream output)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new StreamWriter(output, encoding, bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    public Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? IncomingRequestHandler { get; set; }

    public event EventHandler<AcpNotification>? Notification;

    public void Start()
    {
        _readLoop ??= Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    public async Task<JsonNode?> SendAsync(string method, object? @params, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id.ToString()] = tcs;

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };
        if (@params is not null)
        {
            message["params"] = SerializeParams(@params);
        }

        try
        {
            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id.ToString(), out _);
            throw;
        }
    }

    public Task NotifyAsync(string method, object? @params, CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };
        if (@params is not null)
        {
            message["params"] = SerializeParams(@params);
        }

        return WriteAsync(message, cancellationToken);
    }

    public Task RespondAsync(object id, JsonNode? result, CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["result"] = result
        };
        SetId(message, id);
        return WriteAsync(message, cancellationToken);
    }

    public Task RespondErrorAsync(object id, int code, string messageText, CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = messageText
            }
        };
        SetId(message, id);
        return WriteAsync(message, cancellationToken);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "ACP stdout closed");
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(line);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Ignoring invalid ACP JSON: {Line}", Truncate(line));
                    continue;
                }

                if (parsed is not JsonObject obj)
                {
                    continue;
                }

                try
                {
                    await DispatchAsync(obj, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to dispatch ACP message");
                }
            }
        }
        finally
        {
            FailPending(new IOException("ACP connection closed."));
        }
    }

    private Task DispatchAsync(JsonObject obj, CancellationToken cancellationToken)
    {
        var method = obj["method"]?.GetValue<string>();
        var hasId = obj.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var hasResult = obj.ContainsKey("result");
        var hasError = obj.ContainsKey("error");

        if (method is not null && hasId)
        {
            var id = UnwrapId(idNode!);
            _ = HandleIncomingRequestAsync(method, id, obj["params"], cancellationToken);
            return Task.CompletedTask;
        }

        if (method is not null)
        {
            Notification?.Invoke(this, new AcpNotification { Method = method, Params = obj["params"] });
            return Task.CompletedTask;
        }

        if (hasId && (hasResult || hasError))
        {
            var key = IdKey(idNode!);
            if (!_pending.TryRemove(key, out var tcs))
            {
                _logger.LogDebug("ACP response for unknown id {Id}", key);
                return Task.CompletedTask;
            }

            if (hasError)
            {
                tcs.TrySetException(ToRpcException(obj["error"]));
                return Task.CompletedTask;
            }

            tcs.TrySetResult(obj["result"]);
        }

        return Task.CompletedTask;
    }

    private async Task HandleIncomingRequestAsync(
        string method,
        object id,
        JsonNode? @params,
        CancellationToken cancellationToken)
    {
        try
        {
            var handler = IncomingRequestHandler;
            var result = handler is null
                ? null
                : await handler(method, @params, cancellationToken).ConfigureAwait(false);
            await RespondAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ACP incoming request {Method} failed", method);
            try
            {
                await RespondErrorAsync(id, -32603, ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception respondEx)
            {
                _logger.LogDebug(respondEx, "Failed to send ACP error response");
            }
        }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var json = message.ToJsonString(AcpJson.Options);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var tcs))
            {
                tcs.TrySetException(exception);
            }
        }
    }

    private static JsonNode? SerializeParams(object @params)
    {
        if (@params is JsonNode node)
        {
            return node.DeepClone();
        }

        return JsonSerializer.SerializeToNode(@params, @params.GetType(), AcpJson.Options);
    }

    private static void SetId(JsonObject message, object id)
    {
        message["id"] = id switch
        {
            int i => i,
            long l => l,
            string s => s,
            JsonNode node => node.DeepClone(),
            _ => id.ToString()
        };
    }

    private static object UnwrapId(JsonNode idNode)
    {
        if (idNode is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i))
            {
                return i;
            }

            if (value.TryGetValue<long>(out var l))
            {
                return l;
            }

            if (value.TryGetValue<string>(out var s))
            {
                return s;
            }
        }

        return idNode.ToJsonString();
    }

    private static string IdKey(JsonNode idNode) => UnwrapId(idNode).ToString() ?? "";

    private static AcpRpcException ToRpcException(JsonNode? errorNode)
    {
        var code = errorNode?["code"]?.GetValue<int>() ?? -1;
        var message = errorNode?["message"]?.GetValue<string>() ?? "ACP error";
        JsonElement? data = null;
        if (errorNode?["data"] is { } dataNode)
        {
            data = JsonSerializer.Deserialize<JsonElement>(dataNode.ToJsonString());
        }

        return new AcpRpcException(code, message, data);
    }

    private static string Truncate(string line) =>
        line.Length <= 200 ? line : line[..200] + "…";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        FailPending(new ObjectDisposedException(nameof(AcpJsonRpcClient)));

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Reader may be blocked on a live process until it is killed.
            }
            catch (Exception)
            {
                // ignored
            }
        }

        _reader.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _lifetime.Dispose();
    }
}
