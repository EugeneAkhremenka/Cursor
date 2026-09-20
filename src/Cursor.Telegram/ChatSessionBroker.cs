using Cursor.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cursor.Telegram;

public sealed class ChatSessionBroker
{
    private readonly ICursorAgentHost _host;
    private readonly CursorAgentOptions _options;
    private readonly ILogger<ChatSessionBroker> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private ICursorAgentSession? _session;
    private CancellationTokenSource? _promptCts;
    private string _currentRepoPath;

    public ChatSessionBroker(
        ICursorAgentHost host,
        IOptions<CursorAgentOptions> options,
        ILogger<ChatSessionBroker> logger)
    {
        _host = host;
        _options = options.Value;
        _logger = logger;
        _options.Repos ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _currentRepoPath = _options.RepoPath;
        if (string.IsNullOrWhiteSpace(_currentRepoPath)
            && _options.Repos.Count > 0
            && RepoSelector.TryNormalize(_options.Repos.Values.First(), out var first, out _))
        {
            _currentRepoPath = first;
        }
    }

    public string RepoPath => _currentRepoPath;

    public IReadOnlyList<RepoEntry> ListRepos() => RepoSelector.List(_options, _currentRepoPath);

    public AgentSessionStatus Status()
    {
        var session = _session;
        return new AgentSessionStatus(
            session?.SessionId,
            session?.WorkingDirectory ?? _currentRepoPath,
            session?.Activity ?? AgentActivity.Idle,
            session is not null);
    }

    public bool IsBusy => _mutex.CurrentCount == 0;

    public async Task<PromptResult> PromptAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PromptResult.Fail("Пустой промпт.");
        }

        if (!await _mutex.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return PromptResult.WasBusy();
        }

        var promptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _promptCts = promptCts;
        try
        {
            var session = await EnsureSessionAsync(promptCts.Token).ConfigureAwait(false);
            return await session.PromptAsync(text, progress: null, promptCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PromptResult.WasCancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prompt failed");
            await DropSessionAsync().ConfigureAwait(false);
            return PromptResult.Fail(ex.Message);
        }
        finally
        {
            promptCts.Dispose();
            if (ReferenceEquals(_promptCts, promptCts))
            {
                _promptCts = null;
            }

            _mutex.Release();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        _promptCts?.Cancel();
        var session = _session;
        if (session is not null)
        {
            await session.CancelAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResetAsync()
    {
        _promptCts?.Cancel();
        await DropSessionAsync().ConfigureAwait(false);
    }

    private async Task<ICursorAgentSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is { Activity: not AgentActivity.Faulted } existing)
        {
            return existing;
        }

        await DropSessionAsync().ConfigureAwait(false);
        _session = await _host.CreateSessionAsync(_currentRepoPath, cancellationToken).ConfigureAwait(false);
        return _session;
    }

    public async Task<RepoSwitchResult> SwitchRepoAsync(string selector, CancellationToken cancellationToken)
    {
        if (!await _mutex.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return RepoSwitchResult.WasBusy(_currentRepoPath);
        }

        try
        {
            if (!RepoSelector.TryResolve(_options, selector, out var path, out var error))
            {
                return RepoSwitchResult.Fail(error, _currentRepoPath);
            }

            if (RepoSelector.PathsEqual(path, _currentRepoPath))
            {
                return RepoSwitchResult.Ok(path, changed: false);
            }

            await DropSessionAsync().ConfigureAwait(false);
            _currentRepoPath = path;
            _logger.LogInformation("Switched repo cwd to {Path}", path);
            return RepoSwitchResult.Ok(path, changed: true);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task DropSessionAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispose ACP session");
        }

        _session = null;
    }
}
