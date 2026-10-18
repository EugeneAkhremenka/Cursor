namespace Cursor.Telegram;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = "";

    public long[] AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Telegram user who may see Cursor IDE checkouts. 0 = first <see cref="AllowedUserIds"/> entry.
    /// Everyone else gets a personal empty workspace on first visit.
    /// </summary>
    public long OwnerUserId { get; set; }

    /// <summary>
    /// Root for per-user empty repos. Empty = LocalAppData/Cursor.TelegramHost/workspaces.
    /// </summary>
    public string UserWorkspaceRoot { get; set; } = "";

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

    /// <summary>
    /// Directory for per-user JSONL chat journals. Empty = LocalAppData/Cursor.TelegramHost/chats.
    /// </summary>
    public string ChatLogDirectory { get; set; } = "";

    public long EffectiveOwnerUserId()
    {
        if (OwnerUserId != 0)
        {
            return OwnerUserId;
        }

        return AllowedUserIds is { Length: > 0 } ? AllowedUserIds[0] : 0;
    }

    public bool CanSeeCursorRepos(long userId)
    {
        var owner = EffectiveOwnerUserId();
        return owner == 0 || userId == owner;
    }

    public bool UsesPersonalWorkspace(long userId)
    {
        var owner = EffectiveOwnerUserId();
        return owner != 0 && userId != owner;
    }

    public string EffectiveUserWorkspaceRoot() =>
        string.IsNullOrWhiteSpace(UserWorkspaceRoot) ? UserWorkspace.DefaultRoot() : UserWorkspaceRoot.Trim();
}
