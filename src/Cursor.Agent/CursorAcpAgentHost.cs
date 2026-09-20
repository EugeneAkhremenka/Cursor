using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cursor.Agent;

public sealed class CursorAcpAgentHost : ICursorAgentHost
{
    private readonly CursorAgentOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CursorAcpAgentHost> _logger;

    public CursorAcpAgentHost(
        IOptions<CursorAgentOptions> options,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CursorAcpAgentHost>();
    }

    public async Task<ICursorAgentSession> CreateSessionAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new InvalidOperationException("Cursor:RepoPath is empty. Set it to a local git checkout.");
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Repo path does not exist: {workingDirectory}");
        }

        var agentPath = AgentProcess.ResolveAgentPath(_options.AgentPath);
        var process = AgentProcess.Start(agentPath, workingDirectory, _options.ApiKey, _logger);
        var lifetime = new AgentProcessLifetime(process, logger: _logger);
        var rpc = new AcpJsonRpcClient(process.StandardOutput, process.StandardInput, _logger);
        lifetime.AttachRpc(rpc);
        rpc.IncomingRequestHandler = (method, @params, ct) =>
            CursorAcpProtocol.HandleIncomingRequestAsync(
                method,
                @params,
                _options.PermissionMode,
                _logger,
                ct);
        rpc.Start();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await CursorAcpProtocol.HandshakeAsync(rpc, timeout.Token).ConfigureAwait(false);
            var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, workingDirectory, timeout.Token)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "ACP session {SessionId} started in {Cwd} via {Agent}",
                sessionId,
                workingDirectory,
                agentPath);
            return new CursorAcpSession(
                rpc,
                sessionId,
                workingDirectory,
                _loggerFactory.CreateLogger<CursorAcpSession>(),
                lifetime);
        }
        catch
        {
            await lifetime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
