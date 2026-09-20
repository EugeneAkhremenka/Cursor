using Cursor.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Cursor.Telegram;

public sealed class TelegramUpdateHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly ChatSessionBroker _broker;
    private readonly TelegramOptions _telegram;
    private readonly ILogger<TelegramUpdateHandler> _logger;

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

        await HandleTextAsync(message.Chat.Id, message.Text, cancellationToken).ConfigureAwait(false);
    }

    internal async Task HandleTextAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var command = CommandParser.Parse(text);
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
            case "cancel":
                await _broker.CancelAsync(cancellationToken).ConfigureAwait(false);
                await ReplyAsync(chatId, "Отправляю cancel текущему run.", cancellationToken).ConfigureAwait(false);
                return;
            case "repo":
            case "repos":
                await HandleRepoAsync(chatId, command.Value.Arguments, cancellationToken).ConfigureAwait(false);
                return;
            case "diff":
                var diff = await GitWorkingTree.DescribeAsync(_broker.RepoPath, cancellationToken).ConfigureAwait(false);
                await ReplyAsync(chatId, diff, cancellationToken).ConfigureAwait(false);
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

    private async Task HandleRepoAsync(long chatId, string arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            await ReplyAsync(
                chatId,
                RepoSelector.FormatList(_broker.ListRepos(), _broker.RepoPath),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await _broker.SwitchRepoAsync(arguments, cancellationToken).ConfigureAwait(false);
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
