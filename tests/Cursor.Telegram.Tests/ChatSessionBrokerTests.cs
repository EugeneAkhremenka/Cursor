using Cursor.Agent;
using Cursor.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cursor.Telegram.Tests;

public sealed class ChatSessionBrokerTests
{
    [Fact]
    public async Task Prompt_UsesHostSession_AndBusyOnOverlap()
    {
        var host = new FakeHost();
        var broker = new ChatSessionBroker(
            host,
            Options.Create(new CursorAgentOptions { RepoPath = "/tmp/repo" }),
            NullLogger<ChatSessionBroker>.Instance);

        var first = broker.PromptAsync("one", CancellationToken.None);
        await host.Entered.Task;

        var busy = await broker.PromptAsync("two", CancellationToken.None);
        Assert.True(busy.Busy);

        host.Release.TrySetResult(true);
        var done = await first;
        Assert.True(done.Success);
        Assert.Equal("echo:one", done.Text);
    }

    [Fact]
    public async Task Reset_DisposesSession()
    {
        var host = new FakeHost();
        host.Release.TrySetResult(true);
        var broker = new ChatSessionBroker(
            host,
            Options.Create(new CursorAgentOptions { RepoPath = "/tmp/repo" }),
            NullLogger<ChatSessionBroker>.Instance);

        await broker.PromptAsync("hi", CancellationToken.None);
        Assert.True(broker.Status().HasSession);

        await broker.ResetAsync();
        Assert.False(broker.Status().HasSession);
        Assert.True(host.LastSession?.Disposed);
    }

    [Fact]
    public async Task SwitchRepo_ByName_ResetsSessionAndUpdatesCwd()
    {
        var app = Directory.CreateTempSubdirectory().FullName;
        var infra = Directory.CreateTempSubdirectory().FullName;
        var host = new FakeHost();
        host.Release.TrySetResult(true);
        var broker = new ChatSessionBroker(
            host,
            Options.Create(new CursorAgentOptions
            {
                RepoPath = app,
                Repos = { ["app"] = app, ["infra"] = infra }
            }),
            NullLogger<ChatSessionBroker>.Instance);

        await broker.PromptAsync("hi", CancellationToken.None);
        Assert.Equal(app, host.LastSession?.WorkingDirectory);

        var switched = await broker.SwitchRepoAsync("infra", CancellationToken.None);
        Assert.True(switched.Success);
        Assert.True(switched.Changed);
        Assert.False(broker.Status().HasSession);
        Assert.Equal(infra, broker.RepoPath);

        await broker.PromptAsync("next", CancellationToken.None);
        Assert.Equal(infra, host.LastSession?.WorkingDirectory);
    }

    [Fact]
    public async Task SwitchRepo_WhileBusy_IsRejected()
    {
        var host = new FakeHost();
        var broker = new ChatSessionBroker(
            host,
            Options.Create(new CursorAgentOptions { RepoPath = "/tmp" }),
            NullLogger<ChatSessionBroker>.Instance);

        var first = broker.PromptAsync("one", CancellationToken.None);
        await host.Entered.Task;
        var switched = await broker.SwitchRepoAsync("/var", CancellationToken.None);
        Assert.True(switched.Busy);

        host.Release.TrySetResult(true);
        await first;
    }

    private sealed class FakeHost : ICursorAgentHost
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeSession? LastSession { get; private set; }

        public Task<ICursorAgentSession> CreateSessionAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            LastSession = new FakeSession(workingDirectory, Entered, Release);
            return Task.FromResult<ICursorAgentSession>(LastSession);
        }
    }

    private sealed class FakeSession : ICursorAgentSession
    {
        private readonly TaskCompletionSource<bool> _entered;
        private readonly TaskCompletionSource<bool> _release;

        public FakeSession(string cwd, TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release)
        {
            WorkingDirectory = cwd;
            _entered = entered;
            _release = release;
        }

        public string SessionId => "fake";

        public string WorkingDirectory { get; }

        public AgentActivity Activity { get; private set; } = AgentActivity.Idle;

        public bool Disposed { get; private set; }

        public async Task<PromptResult> PromptAsync(
            string text,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            Activity = AgentActivity.Running;
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            Activity = AgentActivity.Idle;
            return PromptResult.Ok("echo:" + text);
        }

        public Task CancelAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
