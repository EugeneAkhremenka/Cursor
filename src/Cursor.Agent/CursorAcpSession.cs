using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cursor.Agent;

internal sealed class CursorAcpSession : ICursorAgentSession
{
    private readonly AcpJsonRpcClient _rpc;
    private readonly ILogger _logger;
    private readonly IAsyncDisposable? _lifetime;
    private readonly AcpClientHooks? _hooks;
    private readonly int _extraRetries;
    private readonly TimeSpan _retryDelay;
    private readonly AcpPromptCapabilities _promptCapabilities;
    private readonly TimeSpan _idleTimeout;
    private readonly object _gate = new();
    private CancellationTokenSource? _promptCts;
    private IProgress<AgentActivityEvent>? _progress;
    private volatile bool _turnHadActivity;

    private static readonly object[] ResumeBlocks =
    [
        new
        {
            type = "text",
            text = "Связь с моделью оборвалась посреди хода. Продолжи с того места, "
                   + "где остановился, не повторяя уже выполненные шаги."
        }
    ];

    public CursorAcpSession(
        AcpJsonRpcClient rpc,
        string sessionId,
        string workingDirectory,
        ILogger? logger = null,
        IAsyncDisposable? lifetime = null,
        AcpClientHooks? hooks = null,
        int extraRetries = 2,
        TimeSpan? retryDelay = null,
        AcpPromptCapabilities? promptCapabilities = null,
        TimeSpan? idleTimeout = null)
    {
        _rpc = rpc;
        SessionId = sessionId;
        WorkingDirectory = workingDirectory;
        _logger = logger ?? NullLogger.Instance;
        _lifetime = lifetime;
        _hooks = hooks;
        _extraRetries = Math.Clamp(extraRetries, 0, 5);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _promptCapabilities = promptCapabilities ?? AcpPromptCapabilities.Unknown;
        _idleTimeout = idleTimeout ?? TimeSpan.Zero;
        if (_hooks is not null)
        {
            _hooks.WorkingDirectory = workingDirectory;
        }

        _rpc.Notification += OnNotification;
    }

    public string SessionId { get; }

    public string WorkingDirectory { get; }

    public AgentActivity Activity { get; private set; } = AgentActivity.Idle;

    public event EventHandler<string>? AssistantDelta;

    public Task<PromptResult> PromptAsync(
        string text,
        IProgress<AgentActivityEvent>? progress,
        CancellationToken cancellationToken) =>
        PromptAsync([PromptPart.FromText(text)], progress, cancellationToken);

    public async Task<PromptResult> PromptAsync(
        IReadOnlyList<PromptPart> parts,
        IProgress<AgentActivityEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (parts is null || parts.Count == 0)
        {
            return PromptResult.Fail("Пустой промпт.");
        }

        object[] promptBlocks;
        try
        {
            promptBlocks = CursorAcpProtocol.ToPromptBlocks(Downgrade(parts));
        }
        catch (ArgumentException ex)
        {
            return PromptResult.Fail(ex.Message);
        }

        if (promptBlocks.Length == 0)
        {
            return PromptResult.Fail("Пустой промпт.");
        }

        var promptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _promptCts?.Dispose();
            _promptCts = promptCts;
        }

        using var watchdog = _idleTimeout > TimeSpan.Zero
            ? new PromptIdleWatchdog(progress, promptCts, _idleTimeout)
            : null;
        var sink = (IProgress<AgentActivityEvent>?)watchdog ?? progress;

        var collected = new StringBuilder();
        void OnDelta(object? _, string chunk)
        {
            collected.Append(chunk);
            sink?.Report(new AgentActivityEvent(AgentActivityKind.Assistant, chunk, IsChunk: true));
        }

        AssistantDelta += OnDelta;
        _progress = sink;
        if (_hooks is not null)
        {
            _hooks.Progress = sink;
        }

        async Task PrepareRetryAsync(string note)
        {
            collected.Clear();
            _turnHadActivity = false;
            sink?.Report(new AgentActivityEvent(AgentActivityKind.Thought, note));
            if (_retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(_retryDelay, promptCts.Token).ConfigureAwait(false);
            }
        }

        Activity = AgentActivity.Running;
        _turnHadActivity = false;

        try
        {
            var maxAttempts = 1 + _extraRetries;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _rpc.SendAsync(
                            "session/prompt",
                            new
                            {
                                sessionId = SessionId,
                                prompt = promptBlocks
                            },
                            promptCts.Token)
                        .ConfigureAwait(false);

                    var text = collected.ToString();
                    if (!AcpTurnError.TryReadTrailing(text, out var turnError)
                        || !AcpTransientError.IsRetriable(turnError))
                    {
                        Activity = AgentActivity.Idle;
                        return PromptResult.Ok(text);
                    }

                    if (attempt >= maxAttempts)
                    {
                        Activity = AgentActivity.Faulted;
                        _logger.LogError(
                            "ACP turn for session {SessionId} died mid-stream: {Error}",
                            SessionId,
                            turnError);
                        return new PromptResult(false, AcpTurnError.StripTrailing(text), turnError);
                    }

                    _logger.LogWarning(
                        "ACP turn for {SessionId} died mid-stream ({Error}), resume {Attempt}/{Max}",
                        SessionId,
                        turnError,
                        attempt,
                        maxAttempts);
                    promptBlocks = ResumeBlocks;
                    await PrepareRetryAsync("Связь оборвалась, продолжаю ход…").ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Activity = AgentActivity.Idle;
                    if (watchdog?.TimedOut == true)
                    {
                        _logger.LogWarning(
                            "ACP session {SessionId} reported no activity for {Seconds}s; turn cancelled",
                            SessionId,
                            (int)_idleTimeout.TotalSeconds);
                        return PromptResult.TimedOut(
                            collected.ToString(),
                            $"Агент молчал {FormatIdle(_idleTimeout)} — ход прерван.");
                    }

                    return PromptResult.WasCancelled(collected.ToString());
                }
                catch (Exception ex)
                {
                    if (attempt >= maxAttempts || !AcpTransientError.IsRetriable(ex))
                    {
                        Activity = AgentActivity.Faulted;
                        if (ex is AcpRpcException)
                        {
                            _logger.LogError(ex, "ACP prompt failed for session {SessionId}", SessionId);
                        }
                        else
                        {
                            _logger.LogError(ex, "ACP prompt crashed for session {SessionId}", SessionId);
                        }

                        return PromptResult.Fail(ex.Message);
                    }

                    _logger.LogWarning(
                        ex,
                        "ACP prompt transient failure for {SessionId}, retry {Attempt}/{Max}",
                        SessionId,
                        attempt,
                        maxAttempts);
                    if (_turnHadActivity)
                    {
                        // The turn already ran tools or wrote text; resending it would redo that work.
                        promptBlocks = ResumeBlocks;
                    }

                    await PrepareRetryAsync("Сеть моргнула, повторяю запрос…").ConfigureAwait(false);
                }
            }
        }
        finally
        {
            AssistantDelta -= OnDelta;
            _progress = null;
            if (_hooks is not null)
            {
                _hooks.Progress = null;
            }
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

    private static string FormatIdle(TimeSpan idle) =>
        idle.TotalSeconds < 90
            ? $"{(int)idle.TotalSeconds} с"
            : $"{(int)Math.Round(idle.TotalMinutes)} мин";

    private IReadOnlyList<PromptPart> Downgrade(IReadOnlyList<PromptPart> parts)
    {
        if (_promptCapabilities.Image || parts.All(static p => p.Kind != PromptPartKind.Image))
        {
            return parts;
        }

        var downgraded = new List<PromptPart>(parts.Count);
        foreach (var part in parts)
        {
            if (part.Kind != PromptPartKind.Image)
            {
                downgraded.Add(part);
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.Uri))
            {
                _logger.LogWarning("Agent has no image prompt capability; dropping an inline image.");
                continue;
            }

            downgraded.Add(PromptPart.FromResourceLink(
                part.Uri,
                part.Name ?? Path.GetFileName(new Uri(part.Uri).LocalPath),
                part.MimeType));
        }

        return downgraded;
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

        _turnHadActivity = true;

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
