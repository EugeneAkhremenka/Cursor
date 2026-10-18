using Cursor.Agent;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Cursor.Telegram;

internal sealed class TelegramActivityPublisher : IProgress<AgentActivityEvent>, IAsyncDisposable
{
    private static readonly TimeSpan FlushPeriod = TimeSpan.FromMilliseconds(1200);

    private readonly ITelegramBotClient _bot;
    private readonly long _chatId;
    private readonly string _repoPath;
    private readonly ActivityTranscript _transcript = new();
    private readonly object _gate = new();
    private readonly List<Task> _uploads = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly PeriodicTimer _timer = new(FlushPeriod);
    private readonly CancellationToken _outer;
    private Task? _loop;
    private int? _messageId;
    private string _lastSent = "";
    private bool _dirty;
    private int _revision;
    private int _stopped;

    public TelegramActivityPublisher(
        ITelegramBotClient bot,
        long chatId,
        CancellationToken outer,
        string? repoPath = null)
    {
        _bot = bot;
        _chatId = chatId;
        _repoPath = repoPath ?? "";
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
            _revision++;
            if (value.Kind == AgentActivityKind.Image
                && value.Status == "completed"
                && !string.IsNullOrWhiteSpace(value.Text))
            {
                _uploads.Add(SendGeneratedAsync(value.Text));
            }
        }
    }

    public async Task FinishAsync(PromptResult result)
    {
        await StopLoopAsync().ConfigureAwait(false);
        await AwaitUploadsAsync().ConfigureAwait(false);
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
        int revision;
        lock (_gate)
        {
            if (!_dirty && header == "⏳ Работаю…" && _lastSent.Length > 0)
            {
                return;
            }

            text = _transcript.Render(header);
            revision = _revision;
        }

        if (text == _lastSent || _messageId is not int messageId)
        {
            return;
        }

        try
        {
            await _bot.EditMessageText(_chatId, messageId, text, cancellationToken: _outer)
                .ConfigureAwait(false);
            lock (_gate)
            {
                _lastSent = text;
                if (_revision == revision)
                {
                    _dirty = false;
                }
            }
        }
        catch (ApiRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AwaitUploadsAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = _uploads.ToArray();
            _uploads.Clear();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task SendGeneratedAsync(string path)
    {
        var repo = string.IsNullOrWhiteSpace(_repoPath)
            ? Path.GetDirectoryName(path) ?? ""
            : _repoPath;
        if (!TelegramOutboundFile.TryPrepare(repo, path, out var prepared, out _))
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(
                prepared.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var input = InputFile.FromStream(stream, prepared.FileName);
            if (prepared.Kind == TelegramSendKind.Photo)
            {
                await _bot.SendChatAction(_chatId, ChatAction.UploadPhoto, cancellationToken: _outer)
                    .ConfigureAwait(false);
                await _bot.SendPhoto(
                        _chatId,
                        input,
                        caption: prepared.FileName,
                        cancellationToken: _outer)
                    .ConfigureAwait(false);
                return;
            }

            await _bot.SendChatAction(_chatId, ChatAction.UploadDocument, cancellationToken: _outer)
                .ConfigureAwait(false);
            await _bot.SendDocument(
                    _chatId,
                    input,
                    caption: prepared.FileName,
                    cancellationToken: _outer)
                .ConfigureAwait(false);
        }
        catch (Exception)
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
