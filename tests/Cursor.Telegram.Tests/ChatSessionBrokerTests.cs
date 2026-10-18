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

    [Fact]
    public async Task RememberedRepo_RestoredOnNewBroker_AndActivateSwitchesUser()
    {
        var firstRepo = Directory.CreateTempSubdirectory().FullName;
        var secondRepo = Directory.CreateTempSubdirectory().FullName;
        var memory = new InMemoryUserRepoMemory();
        var host = new FakeHost();
        host.Release.TrySetResult(true);
        var options = Options.Create(new CursorAgentOptions
        {
            RepoPath = firstRepo,
            Repos = { ["one"] = firstRepo, ["two"] = secondRepo }
        });

        var broker = new ChatSessionBroker(host, options, NullLogger<ChatSessionBroker>.Instance, memory);
        var switched = await broker.SwitchRepoAsync(137, "two", CancellationToken.None);
        Assert.True(switched.Success);
        Assert.Equal(secondRepo, broker.RepoPath);

        var restarted = new ChatSessionBroker(
            host,
            Options.Create(new CursorAgentOptions { RepoPath = firstRepo }),
            NullLogger<ChatSessionBroker>.Instance,
            memory);
        Assert.Equal(Path.GetFullPath(secondRepo), Path.GetFullPath(restarted.RepoPath));

        memory.Remember(999, firstRepo);
        var activated = await restarted.ActivateUserAsync(999, CancellationToken.None);
        Assert.True(activated.Success);
        Assert.True(activated.Changed);
        Assert.Equal(Path.GetFullPath(firstRepo), Path.GetFullPath(restarted.RepoPath));
    }

    [Fact]
    public async Task Guest_FirstVisit_CreatesPersonalWorkspace_AndHidesConfiguredRepos()
    {
        var ownerRepo = Directory.CreateTempSubdirectory().FullName;
        var workspaces = Directory.CreateTempSubdirectory().FullName;
        var memory = new InMemoryUserRepoMemory();
        var host = new FakeHost();
        host.Release.TrySetResult(true);
        var telegram = Options.Create(new TelegramOptions
        {
            AllowedUserIds = [1, 2],
            OwnerUserId = 1,
            UserWorkspaceRoot = workspaces
        });
        var broker = new ChatSessionBroker(
            host,
            Options.Create(new CursorAgentOptions
            {
                RepoPath = ownerRepo,
                Repos = { ["secret"] = ownerRepo }
            }),
            NullLogger<ChatSessionBroker>.Instance,
            memory,
            telegram);

        var activated = await broker.ActivateUserAsync(2, CancellationToken.None);
        Assert.True(activated.Success);
        var personal = Path.GetFullPath(Path.Combine(workspaces, "2"));
        Assert.Equal(personal, Path.GetFullPath(activated.Path));
        Assert.True(Directory.Exists(personal));
        Assert.True(memory.TryGet(2, out var stored));
        Assert.Equal(personal, Path.GetFullPath(stored));

        var names = broker.ListRepos(2).Select(entry => entry.Name).ToList();
        Assert.DoesNotContain("secret", names);
        Assert.DoesNotContain(broker.ListRepos(2), entry => entry.Group == "cursor");

        var ownerNames = broker.ListRepos(1).Select(entry => entry.Name).ToList();
        Assert.Contains("secret", ownerNames);
    }

    [Fact]
    public async Task FreshBroker_SeedsSavedTurns_IntoFirstPrompt()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        var log = new InMemoryChatTranscript();
        var memory = new InMemoryUserRepoMemory();
        var options = Options.Create(new CursorAgentOptions { RepoPath = repo });
        var host1 = new FakeHost();
        host1.Release.TrySetResult(true);
        var first = new ChatSessionBroker(
            host1,
            options,
            NullLogger<ChatSessionBroker>.Instance,
            memory,
            transcript: log);

        await first.ActivateUserAsync(7, CancellationToken.None);
        var written = await first.PromptAsync("first", CancellationToken.None);
        Assert.True(written.Success);
        var firstChat = Assert.Single(log.ListChats(7));
        Assert.Single(log.List(7, firstChat.Id));

        var host2 = new FakeHost();
        host2.Release.TrySetResult(true);
        var restarted = new ChatSessionBroker(
            host2,
            options,
            NullLogger<ChatSessionBroker>.Instance,
            memory,
            transcript: log);
        await restarted.ActivateUserAsync(7, CancellationToken.None);
        var restored = await restarted.PromptAsync("second", CancellationToken.None);

        Assert.True(restored.Success);
        Assert.Contains("Предыдущие ходы", restored.Text);
        Assert.Contains("first", restored.Text);
        Assert.Equal(2, log.List(7, firstChat.Id).Count);
        Assert.Equal("second", log.List(7, firstChat.Id)[1].User);
    }

    [Fact]
    public async Task Reset_ArchivesTranscript_SoNextSessionHasNoRecap()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        var log = new InMemoryChatTranscript();
        var memory = new InMemoryUserRepoMemory();
        var host = new FakeHost();
        host.Release.TrySetResult(true);
        var broker = new ChatSessionBroker(
            host,
            Options.Create(new CursorAgentOptions { RepoPath = repo }),
            NullLogger<ChatSessionBroker>.Instance,
            memory,
            transcript: log);

        await broker.ActivateUserAsync(7, CancellationToken.None);
        await broker.PromptAsync("keep me", CancellationToken.None);
        var archived = Assert.Single(log.ListChats(7));
        await broker.ResetAsync(7);
        Assert.Equal(2, log.ListChats(7).Count);
        Assert.Single(log.List(7, archived.Id));

        var after = await broker.PromptAsync("fresh", CancellationToken.None);
        Assert.Equal("echo:fresh", after.Text);
        Assert.Equal(2, log.ListChats(7).Count);
    }

    [Fact]
    public async Task ContinueChat_RestoresArchivedTurns()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        var log = new InMemoryChatTranscript();
        var host = new FakeHost();
        host.Release.TrySetResult(true);
        var broker = new ChatSessionBroker(
            host,
            Options.Create(new CursorAgentOptions { RepoPath = repo }),
            NullLogger<ChatSessionBroker>.Instance,
            new InMemoryUserRepoMemory(),
            transcript: log);

        await broker.ActivateUserAsync(7, CancellationToken.None);
        await broker.PromptAsync("old work", CancellationToken.None);
        var archived = Assert.Single(log.ListChats(7));
        await broker.ResetAsync(7);
        await broker.PromptAsync("unrelated", CancellationToken.None);

        var continued = await broker.ContinueChatAsync(7, archived.Id, CancellationToken.None);
        Assert.True(continued.Success);
        var restored = await broker.PromptAsync("resume", CancellationToken.None);
        Assert.Contains("Предыдущие ходы", restored.Text);
        Assert.Contains("old work", restored.Text);
        Assert.DoesNotContain("unrelated", restored.Text);
    }

    [Fact]
    public async Task DeleteChat_RemovesArchivedThread()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        var log = new InMemoryChatTranscript();
        var host = new FakeHost();
        host.Release.TrySetResult(true);
        var broker = new ChatSessionBroker(
            host,
            Options.Create(new CursorAgentOptions { RepoPath = repo }),
            NullLogger<ChatSessionBroker>.Instance,
            new InMemoryUserRepoMemory(),
            transcript: log);

        await broker.ActivateUserAsync(7, CancellationToken.None);
        await broker.PromptAsync("keep me", CancellationToken.None);
        var archived = Assert.Single(log.ListChats(7));
        await broker.ResetAsync(7);

        var deleted = await broker.DeleteChatAsync(7, archived.Id, CancellationToken.None);
        Assert.True(deleted.Success);
        Assert.DoesNotContain(log.ListChats(7), chat => chat.Id == archived.Id);
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
            IProgress<AgentActivityEvent>? progress,
            CancellationToken cancellationToken) =>
            await PromptAsync([PromptPart.FromText(text)], progress, cancellationToken);

        public async Task<PromptResult> PromptAsync(
            IReadOnlyList<PromptPart> parts,
            IProgress<AgentActivityEvent>? progress,
            CancellationToken cancellationToken)
        {
            Activity = AgentActivity.Running;
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            Activity = AgentActivity.Idle;
            var text = parts.FirstOrDefault(p => p.Kind == PromptPartKind.Text)?.Text ?? "";
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
