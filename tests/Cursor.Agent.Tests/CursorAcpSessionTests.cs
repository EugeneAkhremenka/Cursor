using Cursor.Agent;

namespace Cursor.Agent.Tests;

public sealed class CursorAcpSessionTests
{
    [Fact]
    public async Task Prompt_ReturnsStreamedAssistantText()
    {
        var (clientStream, serverStream) = ConnectedStreams.Create();
        await using var fake = new FakeAcpAgent(serverStream);
        await using var rpc = CreateClient(clientStream);

        await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
        var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, "/tmp/repo", CancellationToken.None);
        await using var session = new CursorAcpSession(rpc, sessionId, "/tmp/repo");

        var result = await session.PromptAsync("hello", progress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("echo:hello", result.Text);
        Assert.Equal("sess-1", sessionId);
    }

    [Fact]
    public async Task Prompt_ReportsThoughtAndToolProgress()
    {
        var (clientStream, serverStream) = ConnectedStreams.Create();
        await using var fake = new FakeAcpAgent(serverStream);
        await using var rpc = CreateClient(clientStream);

        await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
        var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, "/tmp/repo", CancellationToken.None);
        await using var session = new CursorAcpSession(rpc, sessionId, "/tmp/repo");

        var events = new List<AgentActivityEvent>();
        var result = await session.PromptAsync(
            "hello",
            new SyncProgress<AgentActivityEvent>(events.Add),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("echo:hello", result.Text);
        Assert.Contains(events, e => e.Kind == AgentActivityKind.Thought && e.Text.Contains("thinking"));
        Assert.Contains(events, e => e.Kind == AgentActivityKind.Tool && e.Id == "t-read" && e.Status == "in_progress");
        Assert.Contains(events, e => e.Kind == AgentActivityKind.Tool && e.Id == "t-read" && e.Status == "completed");
        Assert.Contains(events, e => e.Kind == AgentActivityKind.Assistant && e.Text == "echo:hello");
    }

    [Fact]
    public async Task Prompt_AnswersPermissionRequestWithoutHanging()
    {
        var (clientStream, serverStream) = ConnectedStreams.Create();
        await using var fake = new FakeAcpAgent(serverStream);
        await using var rpc = CreateClient(clientStream);

        await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
        var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, "/tmp/repo", CancellationToken.None);
        await using var session = new CursorAcpSession(rpc, sessionId, "/tmp/repo");

        var result = await session.PromptAsync("NEED_PERM please", progress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("echo:NEED_PERM please", result.Text);
        Assert.Equal(1, fake.PermissionRequests);
    }

    [Fact]
    public async Task Prompt_FailingTurn_ReturnsErrorResult()
    {
        var (clientStream, serverStream) = ConnectedStreams.Create();
        await using var fake = new FakeAcpAgent(serverStream);
        await using var rpc = CreateClient(clientStream);

        await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
        var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, "/tmp/repo", CancellationToken.None);
        await using var session = new CursorAcpSession(rpc, sessionId, "/tmp/repo");

        var result = await session.PromptAsync("FAIL now", progress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("prompt failed", result.Error);
        Assert.Equal(AgentActivity.Faulted, session.Activity);
        Assert.Equal(1, fake.PromptCalls);
    }

    [Fact]
    public async Task Prompt_RetriableTransportError_RetriesSilentlyThenSucceeds()
    {
        var (clientStream, serverStream) = ConnectedStreams.Create();
        await using var fake = new FakeAcpAgent(serverStream);
        await using var rpc = CreateClient(clientStream);

        await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
        var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, "/tmp/repo", CancellationToken.None);
        await using var session = new CursorAcpSession(
            rpc,
            sessionId,
            "/tmp/repo",
            extraRetries: 2,
            retryDelay: TimeSpan.Zero);

        var events = new List<AgentActivityEvent>();
        var result = await session.PromptAsync(
            "RETRIABLE please",
            new SyncProgress<AgentActivityEvent>(events.Add),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("echo:RETRIABLE please", result.Text);
        Assert.Equal(2, fake.PromptCalls);
        Assert.Equal(AgentActivity.Idle, session.Activity);
        Assert.Contains(events, e => e.Kind == AgentActivityKind.Thought && e.Text.Contains("повторяю"));
    }

    [Fact]
    public async Task Prompt_RetriableTransportError_Exhausted_ReturnsFail()
    {
        var (clientStream, serverStream) = ConnectedStreams.Create();
        await using var fake = new FakeAcpAgent(serverStream);
        await using var rpc = CreateClient(clientStream);

        await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
        var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, "/tmp/repo", CancellationToken.None);
        await using var session = new CursorAcpSession(
            rpc,
            sessionId,
            "/tmp/repo",
            extraRetries: 2,
            retryDelay: TimeSpan.Zero);

        var result = await session.PromptAsync("RETRIABLE_ALWAYS now", progress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ECONNRESET", result.Error);
        Assert.Equal(3, fake.PromptCalls);
        Assert.Equal(AgentActivity.Faulted, session.Activity);
    }

    [Fact]
    public async Task Prompt_TurnDiedMidStream_ResumesInsteadOfResendingPrompt()
    {
        var (clientStream, serverStream) = ConnectedStreams.Create();
        await using var fake = new FakeAcpAgent(serverStream);
        await using var rpc = CreateClient(clientStream);

        await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
        var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, "/tmp/repo", CancellationToken.None);
        await using var session = new CursorAcpSession(
            rpc,
            sessionId,
            "/tmp/repo",
            extraRetries: 2,
            retryDelay: TimeSpan.Zero);

        var events = new List<AgentActivityEvent>();
        var result = await session.PromptAsync(
            "DROPPED please",
            new SyncProgress<AgentActivityEvent>(events.Add),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain("ECONNRESET", result.Text);
        Assert.Equal(2, fake.PromptCalls);
        var resent = fake.LastPrompt?[0]?["text"]?.GetValue<string>();
        Assert.Contains("Продолжи", resent);
        Assert.DoesNotContain("DROPPED", resent);
        Assert.Contains(events, e => e.Kind == AgentActivityKind.Thought && e.Text.Contains("продолжаю ход"));
    }

    [Fact]
    public async Task Prompt_TurnDiesEveryTime_FailsWithPartialTextAndError()
    {
        var (clientStream, serverStream) = ConnectedStreams.Create();
        await using var fake = new FakeAcpAgent(serverStream);
        await using var rpc = CreateClient(clientStream);

        await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
        var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, "/tmp/repo", CancellationToken.None);
        await using var session = new CursorAcpSession(
            rpc,
            sessionId,
            "/tmp/repo",
            extraRetries: 1,
            retryDelay: TimeSpan.Zero);

        var result = await session.PromptAsync("DROPPED_ALWAYS now", progress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ECONNRESET", result.Error);
        Assert.DoesNotContain("Error:", result.Text);
        Assert.StartsWith("echo:", result.Text);
        Assert.Equal(2, fake.PromptCalls);
        Assert.Equal(AgentActivity.Faulted, session.Activity);
    }

    [Fact]
    public async Task Prompt_GeneratesImageWhenAgentRequestsIt()
    {
        var cwd = Directory.CreateTempSubdirectory("acp-img").FullName;
        try
        {
            var generator = new RecordingImageGenerator();
            var hooks = new AcpClientHooks { Images = generator, WorkingDirectory = cwd };
            var (clientStream, serverStream) = ConnectedStreams.Create();
            await using var fake = new FakeAcpAgent(serverStream);
            await using var rpc = CreateClient(clientStream, hooks);

            await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
            var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, cwd, CancellationToken.None);
            await using var session = new CursorAcpSession(rpc, sessionId, cwd, hooks: hooks);

            var events = new List<AgentActivityEvent>();
            var result = await session.PromptAsync(
                "NEED_IMAGE please",
                new SyncProgress<AgentActivityEvent>(events.Add),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("a red square icon", generator.Descriptions);
            var written = Path.Combine(cwd, "assets", "red.png");
            Assert.True(File.Exists(written));
            Assert.Contains(events, e => e.Kind == AgentActivityKind.Image && e.Status == "completed");
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _onNext;

        public SyncProgress(Action<T> onNext) => _onNext = onNext;

        public void Report(T value) => _onNext(value);
    }

    private static AcpJsonRpcClient CreateClient(Stream duplex, AcpClientHooks? hooks = null)
    {
        var rpc = new AcpJsonRpcClient(duplex, duplex);
        rpc.IncomingRequestHandler = (method, @params, ct) =>
            CursorAcpProtocol.HandleIncomingRequestAsync(
                method,
                @params,
                "allow-always",
                hooks,
                logger: null,
                ct);
        rpc.Start();
        return rpc;
    }
}
