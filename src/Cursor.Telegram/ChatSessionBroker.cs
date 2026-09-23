using Cursor.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cursor.Telegram;

public sealed class ChatSessionBroker
{
    private readonly ICursorAgentHost _host;
    private readonly CursorAgentOptions _options;
    private readonly TelegramOptions _telegram;
    private readonly IUserRepoMemory _memory;
    private readonly IChatTranscript _transcript;
    private readonly ILogger<ChatSessionBroker> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private ICursorAgentSession? _session;
    private CancellationTokenSource? _promptCts;
    private string _currentRepoPath;
    private long? _activeUserId;
    private string? _activeChatId;
    private bool _seedRestore;

    public ChatSessionBroker(
        ICursorAgentHost host,
        IOptions<CursorAgentOptions> options,
        ILogger<ChatSessionBroker> logger)
        : this(host, options, logger, new InMemoryUserRepoMemory(), telegram: null, transcript: null)
    {
    }

    public ChatSessionBroker(
        ICursorAgentHost host,
        IOptions<CursorAgentOptions> options,
        ILogger<ChatSessionBroker> logger,
        IUserRepoMemory memory,
        IOptions<TelegramOptions>? telegram = null,
        IChatTranscript? transcript = null)
    {
        _host = host;
        _options = options.Value;
        _telegram = telegram?.Value ?? new TelegramOptions();
        _logger = logger;
        _memory = memory;
        _transcript = transcript ?? NullChatTranscript.Instance;
        _options.Repos ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _currentRepoPath = _options.RepoPath;
        if (string.IsNullOrWhiteSpace(_currentRepoPath)
            && _options.Repos.Count > 0
            && RepoSelector.TryNormalize(_options.Repos.Values.First(), out var first, out _))
        {
            _currentRepoPath = first;
        }

        if (_memory.TryGetLast(out var userId, out var remembered)
            && Directory.Exists(remembered))
        {
            _currentRepoPath = Path.GetFullPath(remembered);
            _logger.LogInformation("Restored repo {Path} for Telegram user {UserId}", _currentRepoPath, userId);
        }
    }

    public string RepoPath => _currentRepoPath;

    public IReadOnlyList<RepoEntry> ListRepos() => ListRepos(userId: null);

    public IReadOnlyList<RepoEntry> ListRepos(long? userId) =>
        RepoSelector.List(OptionsFor(userId), CurrentPathFor(userId), ExtraRepos(userId));

    public AgentSessionStatus Status()
    {
        var session = _session;
        return new AgentSessionStatus(
            session?.SessionId,
            session?.WorkingDirectory ?? _currentRepoPath,
            session?.Activity ?? AgentActivity.Idle,
            session is not null,
            string.IsNullOrWhiteSpace(_options.Model) ? "auto" : _options.Model.Trim(),
            string.IsNullOrWhiteSpace(_options.Mode) ? "agent" : _options.Mode.Trim());
    }

    public bool IsBusy => _mutex.CurrentCount == 0;

    public IReadOnlyList<ChatThread> ListChats(long userId) =>
        _transcript.ListChats(userId);

    public Task<PromptResult> PromptAsync(string text, CancellationToken cancellationToken) =>
        PromptAsync(text, progress: null, cancellationToken);

    public Task<PromptResult> PromptAsync(
        string text,
        IProgress<AgentActivityEvent>? progress,
        CancellationToken cancellationToken) =>
        PromptAsync([PromptPart.FromText(text)], progress, cancellationToken);

    public async Task<PromptResult> PromptAsync(
        IReadOnlyList<PromptPart> parts,
        IProgress<AgentActivityEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (parts is null || parts.Count == 0 || parts.All(static p =>
                p.Kind == PromptPartKind.Text && string.IsNullOrWhiteSpace(p.Text)))
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
            var outbound = SeedRestore(parts);
            var result = await session.PromptAsync(outbound, progress, promptCts.Token).ConfigureAwait(false);
            RememberTurn(parts, result);
            return result;
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

    public Task ResetAsync() => ResetAsync(userId: null);

    public async Task ResetAsync(long? userId)
    {
        _promptCts?.Cancel();
        var targetUser = userId ?? _activeUserId;
        if (targetUser is long id)
        {
            _activeUserId = id;
            _activeChatId = _transcript.StartNew(id, _currentRepoPath).Id;
        }

        _seedRestore = false;
        await DropSessionAsync().ConfigureAwait(false);
    }

    public async Task<ChatActionResult> ContinueChatAsync(
        long userId,
        string selector,
        CancellationToken cancellationToken)
    {
        if (!await _mutex.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ChatActionResult.WasBusy();
        }

        try
        {
            if (!_transcript.TrySelect(userId, selector, out var chat))
            {
                return ChatActionResult.Fail("Чат не найден. Открой /chats и используй его id.");
            }

            if (!Directory.Exists(chat.Repo))
            {
                return ChatActionResult.Fail($"Репозиторий чата больше не существует: {chat.Repo}");
            }

            await DropSessionAsync().ConfigureAwait(false);
            _activeUserId = userId;
            _activeChatId = chat.Id;
            _currentRepoPath = Path.GetFullPath(chat.Repo);
            _memory.Remember(userId, _currentRepoPath);
            _seedRestore = false;
            return ChatActionResult.Ok(chat);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ChatActionResult> DeleteChatAsync(
        long userId,
        string selector,
        CancellationToken cancellationToken)
    {
        if (!await _mutex.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ChatActionResult.WasBusy();
        }

        try
        {
            if (!_transcript.TryDelete(userId, selector, out var deleted))
            {
                return ChatActionResult.Fail("Чат не найден. Открой /chats и используй его id.");
            }

            if (deleted.IsCurrent || deleted.Id == _activeChatId)
            {
                await DropSessionAsync().ConfigureAwait(false);
                _activeUserId = userId;
                _activeChatId = _transcript.StartNew(userId, _currentRepoPath).Id;
                _seedRestore = false;
            }

            return ChatActionResult.Ok(deleted);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ChatActionResult> ClearCurrentChatAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var current = EnsureActiveChat(userId);
        return await DeleteChatAsync(userId, current.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ICursorAgentSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is { Activity: not AgentActivity.Faulted } existing)
        {
            return existing;
        }

        await DropSessionAsync().ConfigureAwait(false);
        _session = await _host.CreateSessionAsync(_currentRepoPath, cancellationToken).ConfigureAwait(false);
        _seedRestore = true;
        return _session;
    }

    public Task<RepoSwitchResult> SwitchRepoAsync(string selector, CancellationToken cancellationToken) =>
        SwitchRepoAsync(userId: null, selector, cancellationToken);

    public async Task<RepoSwitchResult> ActivateUserAsync(long userId, CancellationToken cancellationToken)
    {
        if (!await _mutex.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return RepoSwitchResult.WasBusy(_currentRepoPath);
        }

        try
        {
            var previousUser = _activeUserId;
            var target = ResolvePathForUser(userId);
            var changed = !RepoSelector.PathsEqual(target, _currentRepoPath);
            if (changed || (previousUser is long previous && previous != userId))
            {
                await DropSessionAsync().ConfigureAwait(false);
            }

            _activeUserId = userId;
            _currentRepoPath = target;
            _activeChatId = _transcript.GetOrCreateCurrent(userId, target).Id;
            if (!string.IsNullOrWhiteSpace(target))
            {
                _memory.Remember(userId, target);
            }

            return RepoSwitchResult.Ok(target, changed);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<RepoSwitchResult> SwitchRepoAsync(
        long? userId,
        string selector,
        CancellationToken cancellationToken)
    {
        if (!await _mutex.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return RepoSwitchResult.WasBusy(_currentRepoPath);
        }

        try
        {
            if (userId is long id)
            {
                _activeUserId = id;
            }

            if (!RepoSelector.TryResolve(OptionsFor(userId), selector, out var path, out var error, ExtraRepos(userId)))
            {
                return RepoSwitchResult.Fail(error, _currentRepoPath);
            }

            if (RepoSelector.PathsEqual(path, _currentRepoPath))
            {
                RememberIfNeeded(userId, path);
                if (userId is long sameUser)
                {
                    _activeChatId = _transcript.GetOrCreateCurrent(sameUser, path).Id;
                }
                return RepoSwitchResult.Ok(path, changed: false);
            }

            await DropSessionAsync().ConfigureAwait(false);
            _currentRepoPath = path;
            RememberIfNeeded(userId, path);
            if (userId is long switchedUser)
            {
                _activeChatId = _transcript.GetOrCreateCurrent(switchedUser, path).Id;
            }
            _logger.LogInformation("Switched repo cwd to {Path}", path);
            return RepoSwitchResult.Ok(path, changed: true);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private IReadOnlyList<RepoEntry> ExtraRepos(long? userId)
    {
        var extras = new List<RepoEntry>();
        if (userId is long id)
        {
            foreach (var path in _memory.ListHistory(id))
            {
                extras.Add(new RepoEntry(CursorWorkspaceCatalog.ShortName(path), path, false, "recent"));
            }
        }

        if (userId is long cursorUser && !_telegram.UsesPersonalWorkspace(cursorUser))
        {
            foreach (var (name, path) in CursorWorkspaceCatalog.Discover())
            {
                extras.Add(new RepoEntry(name, path, false, "cursor"));
            }
        }

        return extras;
    }

    private CursorAgentOptions OptionsFor(long? userId)
    {
        if (userId is null || !_telegram.UsesPersonalWorkspace(userId.Value))
        {
            return _options;
        }

        return new CursorAgentOptions
        {
            RepoPath = EnsurePersonalWorkspace(userId.Value),
            Model = _options.Model,
            Mode = _options.Mode,
            PermissionMode = _options.PermissionMode
        };
    }

    private string CurrentPathFor(long? userId) =>
        userId is long id && _telegram.UsesPersonalWorkspace(id)
            ? EnsurePersonalWorkspace(id)
            : _currentRepoPath;

    private string ResolvePathForUser(long userId)
    {
        if (_memory.TryGet(userId, out var stored) && Directory.Exists(stored))
        {
            return Path.GetFullPath(stored);
        }

        foreach (var path in _memory.ListHistory(userId))
        {
            if (Directory.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        if (!_telegram.UsesPersonalWorkspace(userId))
        {
            return _currentRepoPath;
        }

        var created = EnsurePersonalWorkspace(userId);
        _memory.Remember(userId, created);
        _logger.LogInformation("Created personal workspace for a new Telegram user");
        return created;
    }

    private string EnsurePersonalWorkspace(long userId) =>
        UserWorkspace.Ensure(userId, _telegram.EffectiveUserWorkspaceRoot());

    private IReadOnlyList<PromptPart> SeedRestore(IReadOnlyList<PromptPart> parts)
    {
        if (!_seedRestore)
        {
            return parts;
        }

        _seedRestore = false;
        if (_activeUserId is not long userId)
        {
            return parts;
        }

        var chat = EnsureActiveChat(userId);
        var recap = ChatTranscriptRestore.Format(_transcript.List(userId, chat.Id), _currentRepoPath);
        if (string.IsNullOrWhiteSpace(recap))
        {
            return parts;
        }

        _logger.LogInformation("Restored on-disk chat turns for Telegram user {UserId}", userId);
        var seeded = new List<PromptPart>(parts.Count + 1) { PromptPart.FromText(recap) };
        seeded.AddRange(parts);
        return seeded;
    }

    private void RememberTurn(IReadOnlyList<PromptPart> parts, PromptResult result)
    {
        if (result.Busy || _activeUserId is not long userId)
        {
            return;
        }

        var user = ChatTranscriptRestore.DescribeParts(parts);
        if (string.IsNullOrWhiteSpace(user))
        {
            return;
        }

        var assistant = ChatTranscriptRestore.Truncate(
            string.IsNullOrWhiteSpace(result.Text) ? result.Error ?? "" : result.Text,
            ChatTranscriptRestore.MaxFieldChars);
        var chat = EnsureActiveChat(userId);
        _transcript.Append(new ChatTurn(
            userId,
            chat.Id,
            _currentRepoPath,
            user,
            assistant,
            DateTimeOffset.UtcNow));
    }

    private ChatThread EnsureActiveChat(long userId)
    {
        if (!string.IsNullOrWhiteSpace(_activeChatId))
        {
            var selected = _transcript.ListChats(userId)
                .FirstOrDefault(chat => chat.Id == _activeChatId);
            if (selected is not null)
            {
                return selected;
            }
        }

        var chat = _transcript.GetOrCreateCurrent(userId, _currentRepoPath);
        _activeChatId = chat.Id;
        return chat;
    }

    private void RememberIfNeeded(long? userId, string path)
    {
        if (userId is long id && !string.IsNullOrWhiteSpace(path))
        {
            _memory.Remember(id, path);
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

public sealed record ChatActionResult(
    bool Success,
    bool Busy,
    string? Error,
    ChatThread? Chat)
{
    public static ChatActionResult Ok(ChatThread chat) => new(true, false, null, chat);

    public static ChatActionResult Fail(string error) => new(false, false, error, null);

    public static ChatActionResult WasBusy() =>
        new(false, true, "Агент уже работает. Сначала /cancel.", null);
}
