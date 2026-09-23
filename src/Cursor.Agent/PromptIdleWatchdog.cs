namespace Cursor.Agent;

/// <summary>
/// Upper bound for <c>--endless-retries</c>: cancels the turn when the agent reports no activity
/// for <paramref name="idle"/>. Every reported event restarts the timer.
/// </summary>
internal sealed class PromptIdleWatchdog : IProgress<AgentActivityEvent>, IDisposable
{
    private readonly IProgress<AgentActivityEvent>? _inner;
    private readonly CancellationTokenSource _cts;
    private readonly TimeSpan _idle;
    private readonly Timer _timer;
    private int _timedOut;

    public PromptIdleWatchdog(
        IProgress<AgentActivityEvent>? inner,
        CancellationTokenSource cts,
        TimeSpan idle)
    {
        _inner = inner;
        _cts = cts;
        _idle = idle;
        _timer = new Timer(OnIdle, null, idle, Timeout.InfiniteTimeSpan);
    }

    public bool TimedOut => Volatile.Read(ref _timedOut) == 1;

    public void Report(AgentActivityEvent value)
    {
        try
        {
            _timer.Change(_idle, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }

        _inner?.Report(value);
    }

    private void OnIdle(object? _)
    {
        if (Interlocked.Exchange(ref _timedOut, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _timer.Dispose();
}
