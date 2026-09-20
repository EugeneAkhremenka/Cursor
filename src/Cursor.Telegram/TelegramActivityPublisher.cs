using Cursor.Agent;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace Cursor.Telegram;

internal sealed class TelegramActivityPublisher : IProgress<AgentActivityEvent>, IAsyncDisposable
{
    private static readonly TimeSpan FlushPeriod = TimeSpan.FromMilliseconds(1200);

    private readonly ITelegramBotClient _bot;
    private readonly long _chatId;
    private readonly ActivityTranscript _transcript = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly PeriodicTimer _timer = new(FlushPeriod);
    private readonly CancellationToken _outer;
    private Task? _loop;
    private int? _messageId;
    private string _lastSent = "";
    private bool _dirty;
    private int _stopped;

    public TelegramActivityPublisher(ITelegramBotClient bot, long chatId, CancellationToken outer)
    {
        _bot = bot;
        _chatId = chatId;
        _outer = outer;
    }

    public async Task StartAsync()
    {
        try
        {
            var sent = await _bot.SendMessage(_chatId, "⏳ Работаю…", cancellationToken: _outer)
                .ConfigureAwait(false);
            _messageId = sent.MessageId;
            _lastSent = "⏳ Работаю…";
        }
        catch (Exception)
        {
            return;
        }

        _loop = PumpAsync();
    }

    public void Report(AgentActivityEvent value)
    {
        if (value.Kind is AgentActivityKind.Assistant)
        {
            return;
        }

        lock (_gate)
        {
            _transcript.Add(value);
            _dirty = true;
        }
    }

    public async Task FinishAsync(PromptResult result)
    {
        await StopLoopAsync().ConfigureAwait(false);
        if (result.Busy)
        {
            await TryDeleteAsync().ConfigureAwait(false);
            return;
        }

        var header = result.Cancelled
            ? "⏹ Остановлено"
            : result.Success
                ? "✅ Готово"
                : "❌ Ошибка";
        await FlushAsync(header).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_outer.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await _bot.SendChatAction(_chatId, ChatAction.Typing, cancellationToken: _outer)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Typing is optional.
                }

                await FlushAsync("⏳ Работаю…").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FlushAsync(string header)
    {
        string text;
        lock (_gate)
        {
            if (!_dirty && header == "⏳ Работаю…" && _lastSent.Length > 0)
            {
                return;
            }

            text = _transcript.Render(header);
            _dirty = false;
        }

        if (text == _lastSent || _messageId is not int messageId)
        {
            return;
        }

        try
        {
            await _bot.EditMessageText(_chatId, messageId, text, cancellationToken: _outer)
                .ConfigureAwait(false);
            _lastSent = text;
        }
        catch (ApiRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TryDeleteAsync()
    {
        if (_messageId is not int messageId)
        {
            return;
        }

        try
        {
            await _bot.DeleteMessage(_chatId, messageId, cancellationToken: _outer).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _messageId = null;
    }

    private async Task StopLoopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _timer.Dispose();
        if (_loop is null)
        {
            return;
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopLoopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
