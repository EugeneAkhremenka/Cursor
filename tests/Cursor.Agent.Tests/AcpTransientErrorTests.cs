using Cursor.Agent;

namespace Cursor.Agent.Tests;

public sealed class AcpTransientErrorTests
{
    [Theory]
    [InlineData("RetriableError: [aborted] read ECONNRESET")]
    [InlineData("Error: RetriableError: [aborted] read ECONNRESET")]
    [InlineData("read ECONNRESET")]
    [InlineData("connect ETIMEDOUT")]
    [InlineData("request to https://api.cursor.com aborted, reason: socket hang up")]
    [InlineData("UND_ERR_SOCKET")]
    public void DetectsTransportFailures(string message)
    {
        Assert.True(AcpTransientError.IsRetriable(message));
        Assert.True(AcpTransientError.IsRetriable(new AcpRpcException(-32603, message)));
    }

    [Theory]
    [InlineData("prompt failed")]
    [InlineData("Method not found")]
    [InlineData("")]
    [InlineData(null)]
    public void IgnoresPermanentFailures(string? message)
    {
        Assert.False(AcpTransientError.IsRetriable(message));
    }

    [Fact]
    public void IgnoresCancellation()
    {
        Assert.False(AcpTransientError.IsRetriable(new OperationCanceledException()));
    }
}
