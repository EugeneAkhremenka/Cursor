using System.Text.Json;
using Cursor.Agent;

namespace Cursor.Agent.Tests;

public sealed class CursorAcpPromptBlocksTests
{
    [Fact]
    public void ToPromptBlocks_MapsTextImageAndLink()
    {
        var blocks = CursorAcpProtocol.ToPromptBlocks(
        [
            PromptPart.FromText("look"),
            PromptPart.FromImage("image/png", "YWJj", "file:///tmp/a.png"),
            PromptPart.FromResourceLink("file:///tmp/b.txt", "b.txt", "text/plain")
        ]);

        var json = JsonSerializer.SerializeToNode(blocks)!.AsArray();
        Assert.Equal(3, json.Count);
        Assert.Equal("text", json[0]?["type"]?.GetValue<string>());
        Assert.Equal("look", json[0]?["text"]?.GetValue<string>());
        Assert.Equal("image", json[1]?["type"]?.GetValue<string>());
        Assert.Equal("image/png", json[1]?["mimeType"]?.GetValue<string>());
        Assert.Equal("YWJj", json[1]?["data"]?.GetValue<string>());
        Assert.Equal("resource_link", json[2]?["type"]?.GetValue<string>());
        Assert.Equal("b.txt", json[2]?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Prompt_SendsImageBlock()
    {
        var (clientStream, serverStream) = ConnectedStreams.Create();
        await using var fake = new FakeAcpAgent(serverStream);
        await using var rpc = CreateClient(clientStream);

        await CursorAcpProtocol.HandshakeAsync(rpc, CancellationToken.None);
        var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, "/tmp/repo", CancellationToken.None);
        await using var session = new CursorAcpSession(rpc, sessionId, "/tmp/repo");

        var result = await session.PromptAsync(
            [
                PromptPart.FromText("see this"),
                PromptPart.FromImage("image/png", "AQID")
            ],
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("echo:see this", result.Text);
        Assert.Equal(2, fake.LastPrompt?.AsArray().Count);
        Assert.Equal("image", fake.LastPrompt?[1]?["type"]?.GetValue<string>());
        Assert.Equal("AQID", fake.LastPrompt?[1]?["data"]?.GetValue<string>());
    }

    [Fact]
    public async Task Prompt_WithoutImageCapability_SendsResourceLink()
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
            promptCapabilities: new AcpPromptCapabilities(Image: false, EmbeddedContext: true));

        var result = await session.PromptAsync(
            [
                PromptPart.FromText("see this"),
                PromptPart.FromImage("image/png", "AQID", "file:///tmp/repo/shot.png")
            ],
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("resource_link", fake.LastPrompt?[1]?["type"]?.GetValue<string>());
        Assert.Equal("shot.png", fake.LastPrompt?[1]?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Prompt_SilentAgent_StopsOnIdleTimeout()
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
            idleTimeout: TimeSpan.FromMilliseconds(150));

        var result = await session.PromptAsync("HANG please", progress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Contains("молчал", result.Error);
    }

    [Theory]
    [InlineData("""{"protocolVersion":1}""", true)]
    [InlineData("""{"agentCapabilities":{"promptCapabilities":{"image":false}}}""", false)]
    [InlineData("""{"agentCapabilities":{"promptCapabilities":{"image":true}}}""", true)]
    [InlineData("""{"capabilities":{"session":{"prompt":{"embeddedContext":{}}}}}""", false)]
    [InlineData("""{"capabilities":{"session":{"prompt":{"image":{}}}}}""", true)]
    public void ReadPromptCapabilities_HandlesV1AndV2(string json, bool expectedImage)
    {
        var caps = CursorAcpProtocol.ReadPromptCapabilities(System.Text.Json.Nodes.JsonNode.Parse(json));
        Assert.Equal(expectedImage, caps.Image);
    }

    private static AcpJsonRpcClient CreateClient(Stream duplex)
    {
        var rpc = new AcpJsonRpcClient(duplex, duplex);
        rpc.IncomingRequestHandler = (method, @params, ct) =>
            CursorAcpProtocol.HandleIncomingRequestAsync(
                method,
                @params,
                "allow-always",
                hooks: null,
                logger: null,
                ct);
        rpc.Start();
        return rpc;
    }
}
