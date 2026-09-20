using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Cursor.Telegram;

public sealed class TelegramBotWorker : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly TelegramUpdateHandler _handler;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramBotWorker> _logger;

    public TelegramBotWorker(
        ITelegramBotClient bot,
        TelegramUpdateHandler handler,
        IOptions<TelegramOptions> options,
        ILogger<TelegramBotWorker> logger)
    {
        _bot = bot;
        _handler = handler;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            throw new InvalidOperationException("Telegram:BotToken is required.");
        }

        if (_options.AllowedUserIds is not { Length: > 0 })
        {
            throw new InvalidOperationException("Telegram:AllowedUserIds must contain at least one numeric user id.");
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = true
        };

        _logger.LogInformation("Telegram polling started for {UserCount} allowed user(s)", _options.AllowedUserIds.Length);
        await _bot.ReceiveAsync(HandleUpdateAsync, HandleErrorAsync, receiverOptions, stoppingToken)
            .ConfigureAwait(false);
    }

    private Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken) =>
        _handler.HandleUpdateAsync(update, cancellationToken);

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return Task.CompletedTask;
        }

        _logger.LogError(exception, "Telegram polling error");
        return Task.CompletedTask;
    }
}
