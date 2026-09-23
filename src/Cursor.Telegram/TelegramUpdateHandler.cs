using Cursor.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Cursor.Telegram;

public sealed class TelegramUpdateHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly ChatSessionBroker _broker;
    private readonly TelegramOptions _telegram;
    private readonly ILogger<TelegramUpdateHandler> _logger;
    private readonly Dictionary<long, IReadOnlyList<SendCandidate>> _listings = [];
    private readonly Dictionary<long, IReadOnlyList<RepoEntry>> _repoListings = [];

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
        if (update.CallbackQuery is { } query)
        {
            await HandleCallbackAsync(query, cancellationToken).ConfigureAwait(false);
            return;
        }

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

        if (message.Type == MessageType.Text)
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                return;
            }

            await HandleTextAsync(message.Chat.Id, userId.Value, message.Text, cancellationToken, isPrivate)
                .ConfigureAwait(false);
            return;
        }

        await HandleAttachmentAsync(message, userId.Value, cancellationToken, isPrivate)
            .ConfigureAwait(false);
    }

    private async Task HandleAttachmentAsync(
        Message message,
        long userId,
        CancellationToken cancellationToken,
        bool privateChat)
    {
        var chatId = message.Chat.Id;
        if (!TelegramInboundAttachment.TryDescribe(message, out var inbound, out var describeError))
        {
            await ReplyAsync(chatId, describeError, cancellationToken, privateChat).ConfigureAwait(false);
            return;
        }

        if (inbound.DeclaredSize is long declared && declared > TelegramInboundAttachment.MaxDownloadBytes)
        {
            await ReplyAsync(
                    chatId,
                    "Файл больше 20 МБ — Telegram Bot API столько не отдаёт.",
                    cancellationToken,
                    privateChat)
                .ConfigureAwait(false);
            return;
        }

        var activated = await _broker.ActivateUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (activated.Busy)
        {
            await ReplyAsync(chatId, activated.Error ?? "Агент уже работает.", cancellationToken, privateChat)
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_broker.RepoPath) || !Directory.Exists(_broker.RepoPath))
        {
            await ReplyAsync(
                    chatId,
                    "Сначала выберите репозиторий: /repo",
                    cancellationToken,
                    privateChat)
                .ConfigureAwait(false);
            return;
        }

        await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        byte[] bytes;
        string savePath;
        try
        {
            savePath = TelegramInboundAttachment.ResolveSavePath(_broker.RepoPath, inbound.FileName);
            await using (var stream = System.IO.File.Create(savePath))
            {
                await _bot.GetInfoAndDownloadFile(inbound.FileId, stream, cancellationToken)
                    .ConfigureAwait(false);
            }

            bytes = await System.IO.File.ReadAllBytesAsync(savePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                TryDelete(savePath);
                await ReplyAsync(chatId, "Пустой файл, пропускаю.", cancellationToken, privateChat)
                    .ConfigureAwait(false);
                return;
            }

            if (bytes.Length > TelegramInboundAttachment.MaxDownloadBytes)
            {
                TryDelete(savePath);
                await ReplyAsync(
                        chatId,
                        "Файл больше 20 МБ — Telegram Bot API столько не отдаёт.",
                        cancellationToken,
                        privateChat)
                    .ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download Telegram file {FileId}", inbound.FileId);
            await ReplyAsync(chatId, "Не удалось скачать файл: " + ex.Message, cancellationToken, privateChat)
                .ConfigureAwait(false);
            return;
        }

        var parts = TelegramInboundAttachment.BuildPromptParts(
            message.Caption ?? "",
            savePath,
            _broker.RepoPath,
            Path.GetFileName(savePath),
            inbound.MimeType,
            inbound.PreferImagePrompt,
            bytes);

        await RunPromptAsync(chatId, parts, cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch
        {
        }
    }

    internal async Task HandleTextAsync(
        long chatId,
        long userId,
        string text,
        CancellationToken cancellationToken,
        bool privateChat = true)
    {
        var command = CommandParser.Parse(text);
        if (command is { Name: "cancel" })
        {
            await _broker.CancelAsync(cancellationToken).ConfigureAwait(false);
            await ReplyAsync(chatId, "Отправляю cancel текущему run.", cancellationToken, privateChat)
                .ConfigureAwait(false);
            return;
        }

        var activated = await _broker.ActivateUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (activated.Busy)
        {
            await ReplyAsync(chatId, activated.Error ?? "Агент уже работает.", cancellationToken, privateChat)
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
                await ReplyAsync(chatId, TelegramText.Help(), cancellationToken, privateChat).ConfigureAwait(false);
                return;
            case "status":
                var status = _broker.Status();
                await ReplyAsync(
                    chatId,
                    TelegramText.FormatStatus(
                        status.WorkingDirectory,
                        status.SessionId,
                        status.Activity.ToString(),
                        status.HasSession,
                        status.Model,
                        status.Mode),
                    cancellationToken,
                    privateChat).ConfigureAwait(false);
                return;
            case "new":
                await _broker.ResetAsync(userId).ConfigureAwait(false);
                await ReplyAsync(
                        chatId,
                        "Новый чат создан. Предыдущий сохранён в /chats.",
                        cancellationToken,
                        privateChat)
                    .ConfigureAwait(false);
                return;
            case "chats":
                var chats = _broker.ListChats(userId);
                await ReplyAsync(
                        chatId,
                        TelegramText.FormatChats(chats),
                        cancellationToken,
                        privateChat,
                        TelegramCommandUi.ChatsKeyboard(chats))
                    .ConfigureAwait(false);
                return;
            case "continue":
                if (string.IsNullOrWhiteSpace(command.Value.Arguments))
                {
                    await ReplyAsync(chatId, "Использование: /continue <id>", cancellationToken, privateChat)
                        .ConfigureAwait(false);
                    return;
                }

                var continued = await _broker
                    .ContinueChatAsync(userId, command.Value.Arguments, cancellationToken)
                    .ConfigureAwait(false);
                await ReplyAsync(
                        chatId,
                        continued.Success
                            ? $"Продолжаю чат {continued.Chat!.Id}: {continued.Chat.Title}"
                            : continued.Error ?? "Не удалось открыть чат.",
                        cancellationToken,
                        privateChat)
                    .ConfigureAwait(false);
                return;
            case "deletechat":
                if (string.IsNullOrWhiteSpace(command.Value.Arguments))
                {
                    await ReplyAsync(chatId, "Использование: /deletechat <id>", cancellationToken, privateChat)
                        .ConfigureAwait(false);
                    return;
                }

                var deleted = await _broker
                    .DeleteChatAsync(userId, command.Value.Arguments, cancellationToken)
                    .ConfigureAwait(false);
                await ReplyAsync(
                        chatId,
                        deleted.Success
                            ? $"Чат {deleted.Chat!.Id} удалён."
                            : deleted.Error ?? "Не удалось удалить чат.",
                        cancellationToken,
                        privateChat)
                    .ConfigureAwait(false);
                return;
            case "clear":
                var cleared = await _broker.ClearCurrentChatAsync(userId, cancellationToken).ConfigureAwait(false);
                await ReplyAsync(
                        chatId,
                        cleared.Success
                            ? "Текущий чат удалён. Начат чистый чат."
                            : cleared.Error ?? "Не удалось очистить чат.",
                        cancellationToken,
                        privateChat)
                    .ConfigureAwait(false);
                return;
            case "repo":
            case "repos":
                await HandleRepoAsync(chatId, userId, command.Value.Arguments, cancellationToken, privateChat)
                    .ConfigureAwait(false);
                return;
            case "diff":
                var diff = await GitWorkingTree.DescribeAsync(_broker.RepoPath, cancellationToken).ConfigureAwait(false);
                await ReplyAsync(chatId, diff, cancellationToken, privateChat).ConfigureAwait(false);
                return;
            case "files":
            case "ls":
                await HandleFilesAsync(chatId, command.Value.Arguments, cancellationToken, privateChat)
                    .ConfigureAwait(false);
                return;
            case "send":
            case "file":
                await HandleSendAsync(chatId, command.Value.Arguments, cancellationToken, privateChat)
                    .ConfigureAwait(false);
                return;
            case "aliases":
                await ReplyAsync(
                    chatId,
                    TelegramSendCatalog.FormatAliases(_telegram.SendAliases),
                    cancellationToken,
                    privateChat).ConfigureAwait(false);
                return;
            case "task":
                if (string.IsNullOrWhiteSpace(command.Value.Arguments))
                {
                    await ReplyAsync(chatId, "Использование: /task <промпт>", cancellationToken, privateChat)
                        .ConfigureAwait(false);
                    return;
                }

                await RunPromptAsync(chatId, command.Value.Arguments, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await ReplyAsync(chatId, "Неизвестная команда. /help", cancellationToken, privateChat)
                    .ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery query, CancellationToken cancellationToken)
    {
        var chat = query.Message?.Chat;
        var userId = query.From.Id;
        if (chat is null)
        {
            await AnswerCallbackAsync(query.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var isPrivate = chat.Type == ChatType.Private;
        var isShared = chat.Type is ChatType.Group or ChatType.Supergroup;
        if (!TelegramChatPolicy.AllowsChat(
                isPrivate,
                isShared,
                _telegram.AllowGroupChats,
                chat.Id,
                userId))
        {
            var notice = isShared && IsAllowed(userId) ? TelegramText.GroupsDisabled() : null;
            await AnswerCallbackAsync(query.Id, cancellationToken, notice).ConfigureAwait(false);
            return;
        }

        if (!IsAllowed(userId))
        {
            await AnswerCallbackAsync(query.Id, cancellationToken, TelegramText.AccessDenied())
                .ConfigureAwait(false);
            return;
        }

        await AnswerCallbackAsync(query.Id, cancellationToken).ConfigureAwait(false);
        if (!TelegramCommandUi.TryParseCallback(query.Data, out var action, out var argument))
        {
            return;
        }

        switch (action)
        {
            case "cmd":
                await HandleTextAsync(chat.Id, userId, "/" + argument, cancellationToken, isPrivate)
                    .ConfigureAwait(false);
                return;
            case "repo":
                if (!TryResolveRepoTarget(chat.Id, argument, out var repoName, out var repoError))
                {
                    await ReplyAsync(chat.Id, repoError, cancellationToken, isPrivate).ConfigureAwait(false);
                    return;
                }

                await HandleRepoAsync(chat.Id, userId, repoName, cancellationToken, isPrivate)
                    .ConfigureAwait(false);
                return;
            case "send":
                await HandleSendAsync(chat.Id, argument, cancellationToken, isPrivate).ConfigureAwait(false);
                return;
            case "chat":
                await HandleTextAsync(
                        chat.Id,
                        userId,
                        "/continue " + argument,
                        cancellationToken,
                        isPrivate)
                    .ConfigureAwait(false);
                return;
            case "delchat":
                await HandleTextAsync(
                        chat.Id,
                        userId,
                        "/deletechat " + argument,
                        cancellationToken,
                        isPrivate)
                    .ConfigureAwait(false);
                return;
        }
    }

    private async Task AnswerCallbackAsync(string callbackId, CancellationToken cancellationToken, string? text = null)
    {
        try
        {
            await _bot.AnswerCallbackQuery(callbackId, text, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task HandleFilesAsync(
        long chatId,
        string filter,
        CancellationToken cancellationToken,
        bool privateChat)
    {
        var items = TelegramSendCatalog.Scan(SendRoots(), filter);
        lock (_listings)
        {
            _listings[chatId] = items;
        }

        await ReplyAsync(
                chatId,
                TelegramSendCatalog.Format(items, filter),
                cancellationToken,
                privateChat,
                TelegramCommandUi.FilesKeyboard(items))
            .ConfigureAwait(false);
    }

    private async Task HandleSendAsync(
        long chatId,
        string arguments,
        CancellationToken cancellationToken,
        bool privateChat)
    {
        if (!TryResolveSendTarget(chatId, arguments.Trim(), out var target, out var resolveError))
        {
            await ReplyAsync(chatId, resolveError, cancellationToken, privateChat).ConfigureAwait(false);
            return;
        }

        if (!TelegramOutboundFile.TryPrepare(_broker.RepoPath, target, out var prepared, out var error))
        {
            await ReplyAsync(chatId, error, cancellationToken, privateChat).ConfigureAwait(false);
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
            await ReplyAsync(chatId, "Telegram не принял файл: " + ex.Message, cancellationToken, privateChat)
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

    private bool TryResolveRepoTarget(long chatId, string token, out string target, out string error)
    {
        target = "";
        error = "";
        if (!TelegramSendCatalog.TryParseIndex(token, out var index))
        {
            error = "Некорректный выбор репозитория.";
            return false;
        }

        IReadOnlyList<RepoEntry>? listing;
        lock (_repoListings)
        {
            _repoListings.TryGetValue(chatId, out listing);
        }

        if (listing is null || listing.Count == 0)
        {
            error = "Списка репозиториев пока нет. Сначала 📁 Репо.";
            return false;
        }

        if (index > listing.Count)
        {
            error = $"В списке {listing.Count} репозитори(ев).";
            return false;
        }

        target = listing[index - 1].Name;
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
        CancellationToken cancellationToken,
        bool privateChat)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            var entries = _broker.ListRepos(userId);
            lock (_repoListings)
            {
                _repoListings[chatId] = entries;
            }

            await ReplyAsync(
                    chatId,
                    RepoSelector.FormatList(entries, _broker.RepoPath),
                    cancellationToken,
                    privateChat,
                    TelegramCommandUi.RepoKeyboard(entries))
                .ConfigureAwait(false);
            return;
        }

        var result = await _broker.SwitchRepoAsync(userId, arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await ReplyAsync(
                    chatId,
                    result.Error ?? "Не удалось переключить репозиторий.",
                    cancellationToken,
                    privateChat)
                .ConfigureAwait(false);
            return;
        }

        var message = result.Changed
            ? $"Репозиторий: {result.Path}\nСессия сброшена, следующий промпт откроет агента здесь."
            : $"Уже этот checkout:\n{result.Path}";
        await ReplyAsync(chatId, message, cancellationToken, privateChat).ConfigureAwait(false);
    }

    private async Task RunPromptAsync(long chatId, string prompt, CancellationToken cancellationToken) =>
        await RunPromptAsync(chatId, [PromptPart.FromText(prompt)], cancellationToken).ConfigureAwait(false);

    private async Task RunPromptAsync(
        long chatId,
        IReadOnlyList<PromptPart> parts,
        CancellationToken cancellationToken)
    {
        await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var live = new TelegramActivityPublisher(
            _bot,
            chatId,
            cancellationToken,
            _broker.RepoPath);
        await live.StartAsync().ConfigureAwait(false);
        var result = await _broker.PromptAsync(parts, live, cancellationToken).ConfigureAwait(false);
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

    private async Task ReplyAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken,
        bool privateChat = false,
        ReplyMarkup? markup = null)
    {
        ReplyMarkup? first = markup ?? new ReplyKeyboardRemove();
        foreach (var part in TelegramText.Split(text))
        {
            await _bot.SendMessage(chatId, part, replyMarkup: first, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            first = null;
        }
    }
}
