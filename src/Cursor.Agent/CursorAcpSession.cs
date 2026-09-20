using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cursor.Agent;

internal sealed class CursorAcpSession : ICursorAgentSession
{
    private readonly AcpJsonRpcClient _rpc;
    private readonly ILogger _logger;
    private readonly IAsyncDisposable? _lifetime;
    private readonly object _gate = new();
    private CancellationTokenSource? _promptCts;
    private IProgress<AgentActivityEvent>? _progress;

    public CursorAcpSession(
        AcpJsonRpcClient rpc,
        string sessionId,
        string workingDirectory,
        ILogger? logger = null,
        IAsyncDisposable? lifetime = null)
    {
        _rpc = rpc;
        SessionId = sessionId;
        WorkingDirectory = workingDirectory;
        _logger = logger ?? NullLogger.Instance;
        _lifetime = lifetime;
        _rpc.Notification += OnNotification;
    }

    public string SessionId { get; }

    public string WorkingDirectory { get; }

    public AgentActivity Activity { get; private set; } = AgentActivity.Idle;

    public event EventHandler<string>? AssistantDelta;

    public async Task<PromptResult> PromptAsync(
        string text,
        IProgress<AgentActivityEvent>? progress,
        CancellationToken cancellationToken)
    {
        var collected = new StringBuilder();
        void OnDelta(object? _, string chunk)
        {
            collected.Append(chunk);
            progress?.Report(new AgentActivityEvent(AgentActivityKind.Assistant, chunk, IsChunk: true));
        }

        AssistantDelta += OnDelta;
        _progress = progress;
        Activity = AgentActivity.Running;
        var promptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _promptCts?.Dispose();
            _promptCts = promptCts;
        }

        try
        {
            await _rpc.SendAsync(
                "session/prompt",
                new
                {
                    sessionId = SessionId,
                    prompt = new[] { new { type = "text", text } }
                },
                promptCts.Token).ConfigureAwait(false);

            Activity = AgentActivity.Idle;
            return PromptResult.Ok(collected.ToString());
        }
        catch (OperationCanceledException)
        {
            Activity = AgentActivity.Idle;
            return PromptResult.WasCancelled(collected.ToString());
        }
        catch (AcpRpcException ex)
        {
            Activity = AgentActivity.Faulted;
            _logger.LogError(ex, "ACP prompt failed for session {SessionId}", SessionId);
            return PromptResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Activity = AgentActivity.Faulted;
            _logger.LogError(ex, "ACP prompt crashed for session {SessionId}", SessionId);
            return PromptResult.Fail(ex.Message);
        }
        finally
        {
            AssistantDelta -= OnDelta;
            _progress = null;
            lock (_gate)
            {
                if (ReferenceEquals(_promptCts, promptCts))
                {
                    _promptCts = null;
                }
            }

            promptCts.Dispose();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _promptCts?.Cancel();
        }

        try
        {
            await _rpc.SendAsync(
                "session/cancel",
                new { sessionId = SessionId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "session/cancel failed for {SessionId}", SessionId);
        }
    }

    private void OnNotification(object? sender, AcpNotification notification)
    {
        var incomingSession = notification.Params?["sessionId"]?.GetValue<string>();
        if (incomingSession is not null && incomingSession != SessionId)
        {
            return;
        }

        var activity = CursorAcpProtocol.ReadActivity(notification);
        if (activity is null)
        {
            return;
        }

        if (activity.Kind == AgentActivityKind.Assistant)
        {
            AssistantDelta?.Invoke(this, activity.Text);
            return;
        }

        _progress?.Report(activity);
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.Notification -= OnNotification;
        lock (_gate)
        {
            _promptCts?.Cancel();
        }

        if (_lifetime is not null)
        {
            await _lifetime.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await _rpc.DisposeAsync().ConfigureAwait(false);
        }
    }
}
