using Cursor.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Cursor.Telegram;

public sealed class TelegramUpdateHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly ChatSessionBroker _broker;
    private readonly TelegramOptions _telegram;
    private readonly ILogger<TelegramUpdateHandler> _logger;
    private readonly Dictionary<long, IReadOnlyList<SendCandidate>> _listings = [];

    public TelegramUpdateHandler(
        ITelegramBotClient bot,
        ChatSessionBroker broker,
        IOptions<TelegramOptions> telegram,
        ILogger<TelegramUpdateHandler> logger)
    {
        _bot = bot;
        _broker = broker;
        _telegram = telegram.Value;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message)
        {
            return;
        }

        var userId = message.From?.Id;
        var isPrivate = message.Chat.Type == ChatType.Private;
        var isShared = message.Chat.Type is ChatType.Group or ChatType.Supergroup;
        if (!TelegramChatPolicy.AllowsChat(
                isPrivate,
                isShared,
                _telegram.AllowGroupChats,
                message.Chat.Id,
                userId))
        {
            _logger.LogInformation(
                "Ignored Telegram chat {ChatId} type {ChatType}",
                message.Chat.Id,
                message.Chat.Type);
            if (isShared && userId is long ownerId && IsAllowed(ownerId))
            {
                await ReplyAsync(message.Chat.Id, TelegramText.GroupsDisabled(), cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (userId is null || !IsAllowed(userId.Value))
        {
            _logger.LogInformation("Denied Telegram user {UserId}", userId);
            if (userId is not null)
            {
                await ReplyAsync(message.Chat.Id, TelegramText.AccessDenied(), cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (message.Type != MessageType.Text || string.IsNullOrWhiteSpace(message.Text))
        {
            await ReplyAsync(message.Chat.Id, "Пока принимаю только текст.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await HandleTextAsync(message.Chat.Id, userId.Value, message.Text, cancellationToken).ConfigureAwait(false);
    }

    internal async Task HandleTextAsync(long chatId, long userId, string text, CancellationToken cancellationToken)
    {
        var command = CommandParser.Parse(text);
        if (command is { Name: "cancel" })
        {
            await _broker.CancelAsync(cancellationToken).ConfigureAwait(false);
            await ReplyAsync(chatId, "Отправляю cancel текущему run.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var activated = await _broker.ActivateUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (activated.Busy)
        {
            await ReplyAsync(chatId, activated.Error ?? "Агент уже работает.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (command is null)
        {
            await RunPromptAsync(chatId, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (command.Value.Name)
        {
            case "start":
            case "help":
                await ReplyAsync(chatId, TelegramText.Help(), cancellationToken).ConfigureAwait(false);
                return;
            case "status":
                var status = _broker.Status();
                await ReplyAsync(
                    chatId,
                    TelegramText.FormatStatus(
                        status.WorkingDirectory,
                        status.SessionId,
                        status.Activity.ToString(),
                        status.HasSession),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "new":
                await _broker.ResetAsync().ConfigureAwait(false);
                await ReplyAsync(chatId, "Сессия сброшена. Следующее сообщение откроет новую.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "repo":
            case "repos":
                await HandleRepoAsync(chatId, userId, command.Value.Arguments, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "diff":
                var diff = await GitWorkingTree.DescribeAsync(_broker.RepoPath, cancellationToken).ConfigureAwait(false);
                await ReplyAsync(chatId, diff, cancellationToken).ConfigureAwait(false);
                return;
            case "files":
            case "ls":
                await HandleFilesAsync(chatId, command.Value.Arguments, cancellationToken).ConfigureAwait(false);
                return;
            case "send":
            case "file":
                await HandleSendAsync(chatId, command.Value.Arguments, cancellationToken).ConfigureAwait(false);
                return;
            case "aliases":
                await ReplyAsync(
                    chatId,
                    TelegramSendCatalog.FormatAliases(_telegram.SendAliases),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "task":
                if (string.IsNullOrWhiteSpace(command.Value.Arguments))
                {
                    await ReplyAsync(chatId, "Использование: /task <промпт>", cancellationToken).ConfigureAwait(false);
                    return;
                }

                await RunPromptAsync(chatId, command.Value.Arguments, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await ReplyAsync(chatId, "Неизвестная команда. /help", cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleFilesAsync(long chatId, string filter, CancellationToken cancellationToken)
    {
        var items = TelegramSendCatalog.Scan(SendRoots(), filter);
        lock (_listings)
        {
            _listings[chatId] = items;
        }

        await ReplyAsync(chatId, TelegramSendCatalog.Format(items, filter), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSendAsync(long chatId, string arguments, CancellationToken cancellationToken)
    {
        if (!TryResolveSendTarget(chatId, arguments.Trim(), out var target, out var resolveError))
        {
            await ReplyAsync(chatId, resolveError, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TelegramOutboundFile.TryPrepare(_broker.RepoPath, target, out var prepared, out var error))
        {
            await ReplyAsync(chatId, error, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var asPhoto = prepared.Kind == TelegramSendKind.Photo;
            await UploadAsync(chatId, prepared, asPhoto, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to send file {Path}", prepared.Path);
            await ReplyAsync(chatId, "Telegram не принял файл: " + ex.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task UploadAsync(
        long chatId,
        TelegramPreparedFile prepared,
        bool asPhoto,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            prepared.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var input = InputFile.FromStream(stream, prepared.FileName);
        if (asPhoto)
        {
            await _bot.SendChatAction(chatId, ChatAction.UploadPhoto, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await _bot.SendPhoto(
                        chatId,
                        input,
                        caption: prepared.FileName,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (ApiRequestException)
            {
                // Too large / bad dimensions: fall through as a document.
            }
        }

        if (asPhoto)
        {
            await UploadAsync(chatId, prepared, asPhoto: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _bot.SendChatAction(chatId, ChatAction.UploadDocument, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _bot.SendDocument(
                chatId,
                input,
                caption: prepared.FileName,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private bool TryResolveSendTarget(long chatId, string token, out string target, out string error)
    {
        target = "";
        error = "";
        if (token.Length == 0)
        {
            error = TelegramOutboundFile.Usage();
            return false;
        }

        if (TelegramSendCatalog.IsLatestToken(token))
        {
            var newest = TelegramSendCatalog.Scan(SendRoots(), filter: null, take: 1);
            if (newest.Count == 0)
            {
                error = "Свежих файлов не нашёл.";
                return false;
            }

            target = newest[0].Path;
            return true;
        }

        if (TelegramSendCatalog.TryParseIndex(token, out var index))
        {
            IReadOnlyList<SendCandidate>? listing;
            lock (_listings)
            {
                _listings.TryGetValue(chatId, out listing);
            }

            if (listing is null || listing.Count == 0)
            {
                error = "Списка пока нет. Сначала /files.";
                return false;
            }

            if (index > listing.Count)
            {
                error = $"В списке {listing.Count} файл(ов). /files обновит.";
                return false;
            }

            target = listing[index - 1].Path;
            return true;
        }

        if (TelegramSendCatalog.TryResolveAlias(_telegram.SendAliases, token, out var aliased))
        {
            target = aliased;
            return true;
        }

        target = token;
        return true;
    }

    private IReadOnlyList<string> SendRoots()
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(_broker.RepoPath))
        {
            roots.Add(_broker.RepoPath);
        }

        foreach (var raw in _telegram.SendRoots)
        {
            if (RepoSelector.TryNormalize(raw, out var full, out _))
            {
                roots.Add(full);
            }
        }

        foreach (var raw in _telegram.SendAliases.Values)
        {
            if (RepoSelector.TryNormalize(raw, out var full, out _) && Directory.Exists(full))
            {
                roots.Add(full);
            }
        }

        return roots;
    }

    private async Task HandleRepoAsync(
        long chatId,
        long userId,
        string arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            await ReplyAsync(
                chatId,
                RepoSelector.FormatList(_broker.ListRepos(userId), _broker.RepoPath),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await _broker.SwitchRepoAsync(userId, arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await ReplyAsync(chatId, result.Error ?? "Не удалось переключить репозиторий.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var message = result.Changed
            ? $"Репозиторий: {result.Path}\nСессия сброшена, следующий промпт откроет агента здесь."
            : $"Уже этот checkout:\n{result.Path}";
        await ReplyAsync(chatId, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPromptAsync(long chatId, string prompt, CancellationToken cancellationToken)
    {
        await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var live = new TelegramActivityPublisher(_bot, chatId, cancellationToken);
        await live.StartAsync().ConfigureAwait(false);
        var result = await _broker.PromptAsync(prompt, live, cancellationToken).ConfigureAwait(false);
        await live.FinishAsync(result).ConfigureAwait(false);
        var envelope = new PromptResultEnvelope(
            result.Success,
            result.Text,
            result.Error,
            result.Cancelled,
            result.Busy);
        await ReplyAsync(chatId, TelegramText.FormatPromptResult(envelope), cancellationToken).ConfigureAwait(false);
    }

    private bool IsAllowed(long userId) =>
        _telegram.AllowedUserIds.Contains(userId);

    private async Task ReplyAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        foreach (var part in TelegramText.Split(text))
        {
            await _bot.SendMessage(chatId, part, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
