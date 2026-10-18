using Cursor.Agent;

namespace Cursor.Agent.Tests;

public sealed class PromptIdleWatchdogTests
{
    [Fact]
    public async Task Cancels_WhenNothingIsReported()
    {
        using var cts = new CancellationTokenSource();
        using var watchdog = new PromptIdleWatchdog(null, cts, TimeSpan.FromMilliseconds(80));

        await cts.Token.WaitHandleAsync(TimeSpan.FromSeconds(5));

        Assert.True(watchdog.TimedOut);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task ActivityKeepsTurnAlive_AndReachesInnerProgress()
    {
        using var cts = new CancellationTokenSource();
        var seen = new List<AgentActivityEvent>();
        using var watchdog = new PromptIdleWatchdog(
            new InlineProgress(seen.Add),
            cts,
            TimeSpan.FromMilliseconds(200));

        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(60);
            watchdog.Report(new AgentActivityEvent(AgentActivityKind.Thought, "tick"));
        }

        Assert.False(watchdog.TimedOut);
        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(5, seen.Count);
    }

    private sealed class InlineProgress : IProgress<AgentActivityEvent>
    {
        private readonly Action<AgentActivityEvent> _onNext;

        public InlineProgress(Action<AgentActivityEvent> onNext) => _onNext = onNext;

        public void Report(AgentActivityEvent value) => _onNext(value);
    }
}

internal static class CancellationTokenWaitExtensions
{
    public static async Task WaitHandleAsync(this CancellationToken token, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = token.Register(() => tcs.TrySetResult());
        await Task.WhenAny(tcs.Task, Task.Delay(timeout));
    }
}
