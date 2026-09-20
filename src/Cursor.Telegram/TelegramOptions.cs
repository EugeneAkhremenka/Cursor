namespace Cursor.Telegram;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = "";

    public long[] AllowedUserIds { get; set; } = [];

    /// <summary>
    /// When false (default), only private chats are accepted.
    /// Shared groups/supergroups require an explicit opt-in.
    /// </summary>
    public bool AllowGroupChats { get; set; }

    /// <summary>
    /// Short names for /send, e.g. "avatar" -> file, "shots" -> directory (newest file wins).
    /// </summary>
    public Dictionary<string, string> SendAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extra directories scanned by /files besides the current checkout.
    /// </summary>
    public string[] SendRoots { get; set; } = [];

    /// <summary>
    /// JSON file that remembers last /repo per Telegram user id. Empty = LocalAppData default.
    /// </summary>
    public string RepoMemoryPath { get; set; } = "";
}
