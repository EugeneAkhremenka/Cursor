namespace Cursor.Telegram;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = "";

    public long[] AllowedUserIds { get; set; } = [];
}
