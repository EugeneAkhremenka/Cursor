using System.IO.Pipes;

namespace Cursor.Agent.Tests;

internal static class ConnectedStreams
{
    public static (DuplexStream Client, DuplexStream Server) Create()
    {
        var clientToServer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var serverToClient = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var serverIn = new AnonymousPipeClientStream(PipeDirection.In, clientToServer.GetClientHandleAsString());
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, serverToClient.GetClientHandleAsString());
        clientToServer.DisposeLocalCopyOfClientHandle();
        serverToClient.DisposeLocalCopyOfClientHandle();
        return (
            new DuplexStream(clientIn, clientToServer),
            new DuplexStream(serverIn, serverToClient));
    }
}

internal sealed class DuplexStream : Stream
{
    private readonly Stream _read;
    private readonly Stream _write;

    public DuplexStream(Stream read, Stream write)
    {
        _read = read;
        _write = write;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _write.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _read.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _read.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _read.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _write.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _write.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _write.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _read.Dispose();
            _write.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _read.DisposeAsync().ConfigureAwait(false);
        await _write.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
