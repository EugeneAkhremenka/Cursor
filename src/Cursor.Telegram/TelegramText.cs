using System.Net;
using System.Text;

namespace Cursor.Telegram;

public static class TelegramText
{
    public const int MaxMessageLength = 4000;

    public static string AccessDenied() => "Access denied.";

    public static string GroupsDisabled() =>
        "Групповые чаты выключены. Включите Telegram:AllowGroupChats.";

    public static string Help() =>
        """
        Локальный Cursor-агент (ACP), без Cloud Agents.

        /start — эта справка
        /task <текст> — задача в текущей сессии
        /new — сбросить сессию
        /status — состояние агента
        /cancel — остановить текущий run
        /repo — текущий, недавние и репо из Cursor
        /repo <имя|путь> — переключить репозиторий (запоминается по Telegram id)
        /diff — git status и diff --stat
        /files [фильтр] — свежие файлы списком с номерами
        /send <номер|last|алиас|путь> — прислать файл (jpg/png/webp — фото)
        /aliases — короткие имена из Telegram:SendAliases

        Обычное текстовое сообщение тоже уходит агенту как промпт.
        Пока агент работает, бот обновляет статус: thinking и вызовы тулов.
        Группы по умолчанию выключены (Telegram:AllowGroupChats).
        """;

    public static string Escape(string text) => WebUtility.HtmlEncode(text);

    public static IReadOnlyList<string> Split(string text, int maxLength = MaxMessageLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [""];
        }

        if (text.Length <= maxLength)
        {
            return [text];
        }

        var parts = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var length = Math.Min(maxLength, text.Length - offset);
            if (offset + length < text.Length)
            {
                var window = text.AsSpan(offset, length);
                var breakAt = window.LastIndexOf('\n');
                if (breakAt < maxLength / 4)
                {
                    breakAt = window.LastIndexOf(' ');
                }

                if (breakAt >= maxLength / 4)
                {
                    length = breakAt + 1;
                }
            }

            parts.Add(text.Substring(offset, length));
            offset += length;
        }

        return parts;
    }

    public static string FormatPromptResult(PromptResultEnvelope result)
    {
        if (result.Busy)
        {
            return "Агент уже работает. /cancel чтобы остановить.";
        }

        if (result.Cancelled)
        {
            return string.IsNullOrWhiteSpace(result.Text)
                ? "Остановлено."
                : "Остановлено.\n\n" + result.Text;
        }

        if (!result.Success)
        {
            return string.IsNullOrWhiteSpace(result.Error)
                ? "Агент вернул ошибку."
                : "Ошибка: " + result.Error;
        }

        return string.IsNullOrWhiteSpace(result.Text)
            ? "(агент ничего не ответил)"
            : result.Text;
    }

    public static string FormatStatus(string workingDirectory, string? sessionId, string activity, bool hasSession)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"cwd: {workingDirectory}");
        sb.AppendLine($"session: {(hasSession ? sessionId : "(нет)")}");
        sb.AppendLine($"activity: {activity}");
        return sb.ToString().TrimEnd();
    }
}

public readonly record struct PromptResultEnvelope(
    bool Success,
    string Text,
    string? Error,
    bool Cancelled,
    bool Busy);
