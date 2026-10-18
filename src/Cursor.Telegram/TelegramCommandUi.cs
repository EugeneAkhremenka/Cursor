using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Cursor.Telegram;

internal static class TelegramCommandUi
{
    public const string Help = "📖 Справка";
    public const string Status = "📊 Статус";
    public const string NewSession = "🆕 Новая";
    public const string Chats = "💬 Чаты";
    public const string Repo = "📁 Репо";
    public const string Files = "📎 Файлы";
    public const string Aliases = "🏷 Алиасы";
    public const string Cancel = "⏹ Стоп";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        [Help] = "help",
        [Status] = "status",
        [NewSession] = "new",
        [Chats] = "chats",
        [Repo] = "repo",
        [Files] = "files",
        [Aliases] = "aliases",
        [Cancel] = "cancel"
    };

    public static bool TryMapLabel(string? text, out string command)
    {
        command = "";
        return !string.IsNullOrWhiteSpace(text) && Labels.TryGetValue(text.Trim(), out command!);
    }

    public static IReadOnlyList<BotCommand> BotCommands() =>
    [
        new("help", "Справка"),
        new("status", "Состояние агента"),
        new("new", "Начать новый чат"),
        new("chats", "Прошлые чаты"),
        new("continue", "Продолжить чат по id"),
        new("deletechat", "Удалить чат по id"),
        new("clear", "Удалить текущий чат"),
        new("cancel", "Остановить текущий run"),
        new("repo", "Список и смена репозитория"),
        new("files", "Свежие файлы"),
        new("send", "Прислать файл"),
        new("aliases", "Алиасы файлов"),
        new("task", "Задача агенту")
    ];

    public static InlineKeyboardMarkup? RepoKeyboard(IReadOnlyList<RepoEntry> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var markup = new InlineKeyboardMarkup();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var mark = entry.IsCurrent ? "• " : "";
            markup.AddNewRow(new InlineKeyboardButton(
                Truncate($"{mark}{entry.Name}", 64),
                $"repo:{i + 1}"));
        }

        return markup;
    }

    public static InlineKeyboardMarkup? FilesKeyboard(IReadOnlyList<SendCandidate> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var markup = new InlineKeyboardMarkup();
        for (var i = 0; i < items.Count; i++)
        {
            markup.AddNewRow(new InlineKeyboardButton(
                Truncate($"{i + 1}. {items[i].FileName}", 64),
                $"send:{i + 1}"));
        }

        markup.AddNewRow(new InlineKeyboardButton("Последний", "send:last"));
        return markup;
    }

    public static InlineKeyboardMarkup? ChatsKeyboard(IReadOnlyList<ChatThread> chats)
    {
        if (chats.Count == 0)
        {
            return null;
        }

        var markup = new InlineKeyboardMarkup();
        foreach (var chat in chats.Take(12))
        {
            var mark = chat.IsCurrent ? "• " : "";
            markup.AddNewRow(
                new InlineKeyboardButton(
                    Truncate($"{mark}{chat.Id} · {chat.Title}", 48),
                    $"chat:{chat.Id}"),
                new InlineKeyboardButton("🗑", $"delchat:{chat.Id}"));
        }

        return markup;
    }

    public static bool TryParseCallback(string? data, out string action, out string argument)
    {
        action = "";
        argument = "";
        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        var split = data.IndexOf(':');
        if (split <= 0 || split == data.Length - 1)
        {
            return false;
        }

        action = data[..split];
        argument = data[(split + 1)..];
        return action is "cmd" or "repo" or "send" or "chat" or "delchat";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
