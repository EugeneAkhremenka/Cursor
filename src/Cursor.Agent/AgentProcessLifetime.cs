using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cursor.Agent;

internal sealed class AgentProcessLifetime : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ILogger _logger;
    private AcpJsonRpcClient? _rpc;
    private bool _disposed;

    public AgentProcessLifetime(Process process, ILogger? logger = null)
    {
        _process = process;
        _logger = logger ?? NullLogger.Instance;
    }

    public void AttachRpc(AcpJsonRpcClient rpc) => _rpc = rpc;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_rpc is not null)
        {
            await _rpc.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stop ACP agent process {Pid}", _process.Id);
        }
        finally
        {
            _process.Dispose();
        }
    }
}
