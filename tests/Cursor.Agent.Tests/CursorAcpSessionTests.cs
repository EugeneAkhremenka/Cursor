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
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _onNext;

        public SyncProgress(Action<T> onNext) => _onNext = onNext;

        public void Report(T value) => _onNext(value);
    }

    private static AcpJsonRpcClient CreateClient(Stream duplex)
    {
        var rpc = new AcpJsonRpcClient(duplex, duplex);
        rpc.IncomingRequestHandler = (method, @params, ct) =>
            CursorAcpProtocol.HandleIncomingRequestAsync(method, @params, "allow-always", logger: null, ct);
        rpc.Start();
        return rpc;
    }
}
