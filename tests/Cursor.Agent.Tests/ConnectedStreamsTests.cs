using System.Text;

namespace Cursor.Agent.Tests;

public sealed class ConnectedStreamsTests
{
    [Fact]
    public async Task ClientWrite_IsReadableByServer()
    {
        var (client, server) = ConnectedStreams.Create();
        await using var clientWriter = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var serverReader = new StreamReader(server, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

        await clientWriter.WriteLineAsync("ping");
        var line = await serverReader.ReadLineAsync();
        Assert.Equal("ping", line);
    }
}
