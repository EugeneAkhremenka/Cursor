using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cursor.Agent;

public sealed class CursorAcpAgentHost : ICursorAgentHost
{
    private readonly CursorAgentOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CursorAcpAgentHost> _logger;
    private readonly IImageGenerator _images;

    public CursorAcpAgentHost(
        IOptions<CursorAgentOptions> options,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CursorAcpAgentHost>();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cursor.TelegramHost/0.1");
        _images = new PollinationsImageGenerator(
            http,
            loggerFactory.CreateLogger<PollinationsImageGenerator>(),
            _options.ImageApiUrl);
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
        CliNetworkConfig.Apply(_options.AgentHttpProtocol, _logger);
        var process = AgentProcess.Start(
            agentPath,
            workingDirectory,
            _options.ApiKey,
            _logger,
            _options.Model,
            _options.Mode,
            _options.EndlessRetries);
        var lifetime = new AgentProcessLifetime(process, logger: _logger);
        var rpc = new AcpJsonRpcClient(process.StandardOutput, process.StandardInput, _logger);
        lifetime.AttachRpc(rpc);
        var hooks = new AcpClientHooks
        {
            Images = _images,
            WorkingDirectory = workingDirectory
        };
        rpc.IncomingRequestHandler = (method, @params, ct) =>
            CursorAcpProtocol.HandleIncomingRequestAsync(
                method,
                @params,
                _options.PermissionMode,
                hooks,
                _logger,
                ct);
        rpc.Start();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var capabilities = await CursorAcpProtocol.HandshakeAsync(rpc, timeout.Token).ConfigureAwait(false);
            var sessionId = await CursorAcpProtocol.NewSessionAsync(rpc, workingDirectory, timeout.Token)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "ACP session {SessionId} started in {Cwd} via {Agent} model {Model} mode {Mode}",
                sessionId,
                workingDirectory,
                agentPath,
                string.IsNullOrWhiteSpace(_options.Model) ? "auto" : _options.Model,
                string.IsNullOrWhiteSpace(_options.Mode) ? "agent" : _options.Mode);
            return new CursorAcpSession(
                rpc,
                sessionId,
                workingDirectory,
                _loggerFactory.CreateLogger<CursorAcpSession>(),
                lifetime,
                hooks,
                _options.PromptRetries,
                TimeSpan.FromMilliseconds(Math.Max(0, _options.PromptRetryDelayMilliseconds)),
                capabilities,
                TimeSpan.FromSeconds(Math.Max(0, _options.PromptIdleTimeoutSeconds)));
        }
        catch
        {
            await lifetime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
