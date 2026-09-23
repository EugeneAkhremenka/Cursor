using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cursor.Agent;

namespace Cursor.Telegram;

public sealed record ChatTurn(
    long UserId,
    string ChatId,
    string Repo,
    string User,
    string Assistant,
    DateTimeOffset At);

public sealed record ChatThread(
    string Id,
    string Repo,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int TurnCount,
    bool IsCurrent);

public interface IChatTranscript
{
    ChatThread GetOrCreateCurrent(long userId, string repo);

    ChatThread StartNew(long userId, string repo);

    IReadOnlyList<ChatThread> ListChats(long userId);

    bool TrySelect(long userId, string selector, out ChatThread chat);

    bool TryDelete(long userId, string selector, out ChatThread chat);

    void Append(ChatTurn turn);

    IReadOnlyList<ChatTurn> List(long userId, string chatId);
}

internal sealed class NullChatTranscript : IChatTranscript
{
    public static readonly NullChatTranscript Instance = new();

    public ChatThread GetOrCreateCurrent(long userId, string repo) => Empty(repo);

    public ChatThread StartNew(long userId, string repo) => Empty(repo);

    public IReadOnlyList<ChatThread> ListChats(long userId) => [];

    public bool TrySelect(long userId, string selector, out ChatThread chat)
    {
        chat = Empty("");
        return false;
    }

    public bool TryDelete(long userId, string selector, out ChatThread chat)
    {
        chat = Empty("");
        return false;
    }

    public void Append(ChatTurn turn)
    {
    }

    public IReadOnlyList<ChatTurn> List(long userId, string chatId) => [];

    private static ChatThread Empty(string repo)
    {
        var now = DateTimeOffset.UtcNow;
        return new ChatThread("", repo, "Новый чат", now, now, 0, true);
    }
}

internal sealed class InMemoryChatTranscript : IChatTranscript
{
    private readonly Dictionary<long, UserChatState> _states = [];
    private readonly object _gate = new();

    public ChatThread GetOrCreateCurrent(long userId, string repo)
    {
        lock (_gate)
        {
            var state = State(userId);
            var current = state.Chats.FirstOrDefault(chat =>
                chat.Id == state.CurrentChatId && PathsEqual(chat.Repo, repo));
            current ??= state.Chats
                .Where(chat => PathsEqual(chat.Repo, repo))
                .OrderByDescending(chat => chat.UpdatedAt)
                .FirstOrDefault();
            if (current is null)
            {
                current = NewState(repo);
                state.Chats.Add(current);
            }

            state.CurrentChatId = current.Id;
            return Describe(current, true);
        }
    }

    public ChatThread StartNew(long userId, string repo)
    {
        lock (_gate)
        {
            var state = State(userId);
            var chat = NewState(repo);
            state.Chats.Add(chat);
            state.CurrentChatId = chat.Id;
            return Describe(chat, true);
        }
    }

    public IReadOnlyList<ChatThread> ListChats(long userId)
    {
        lock (_gate)
        {
            var state = State(userId);
            return state.Chats
                .OrderByDescending(chat => chat.UpdatedAt)
                .Select(chat => Describe(chat, chat.Id == state.CurrentChatId))
                .ToArray();
        }
    }

    public bool TrySelect(long userId, string selector, out ChatThread chat)
    {
        lock (_gate)
        {
            var state = State(userId);
            var found = Find(state, selector);
            if (found is null)
            {
                chat = default!;
                return false;
            }

            state.CurrentChatId = found.Id;
            chat = Describe(found, true);
            return true;
        }
    }

    public bool TryDelete(long userId, string selector, out ChatThread chat)
    {
        lock (_gate)
        {
            var state = State(userId);
            var found = Find(state, selector);
            if (found is null)
            {
                chat = default!;
                return false;
            }

            chat = Describe(found, found.Id == state.CurrentChatId);
            state.Chats.Remove(found);
            if (state.CurrentChatId == found.Id)
            {
                state.CurrentChatId = state.Chats
                    .OrderByDescending(item => item.UpdatedAt)
                    .FirstOrDefault()?.Id;
            }

            return true;
        }
    }

    public void Append(ChatTurn turn)
    {
        lock (_gate)
        {
            var state = State(turn.UserId);
            var chat = state.Chats.FirstOrDefault(item => item.Id == turn.ChatId);
            if (chat is null)
            {
                chat = NewState(turn.Repo, turn.ChatId);
                state.Chats.Add(chat);
            }

            chat.Turns.Add(turn);
            if (chat.Turns.Count > 200)
            {
                chat.Turns.RemoveRange(0, chat.Turns.Count - 200);
            }

            chat.Repo = turn.Repo;
            chat.UpdatedAt = turn.At;
            chat.Title = MakeTitle(chat.Turns[0].User);
            state.CurrentChatId = chat.Id;
        }
    }

    public IReadOnlyList<ChatTurn> List(long userId, string chatId)
    {
        lock (_gate)
        {
            return State(userId).Chats
                .FirstOrDefault(chat => chat.Id == chatId)?
                .Turns.ToArray() ?? [];
        }
    }

    internal UserChatState Snapshot(long userId)
    {
        lock (_gate)
        {
            return Clone(State(userId));
        }
    }

    internal void Replace(long userId, UserChatState state)
    {
        lock (_gate)
        {
            _states[userId] = Clone(state);
        }
    }

    private UserChatState State(long userId)
    {
        if (!_states.TryGetValue(userId, out var state))
        {
            state = new UserChatState();
            _states[userId] = state;
        }

        return state;
    }

    internal static ChatState NewState(string repo, string? id = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ChatState
        {
            Id = id ?? Guid.NewGuid().ToString("N")[..8],
            Repo = repo,
            Title = "Новый чат",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    internal static ChatThread Describe(ChatState chat, bool current) =>
        new(
            chat.Id,
            chat.Repo,
            chat.Title,
            chat.CreatedAt,
            chat.UpdatedAt,
            chat.Turns.Count,
            current);

    internal static ChatState? Find(UserChatState state, string selector)
    {
        var value = selector?.Trim() ?? "";
        if (value.Length == 0)
        {
            return null;
        }

        var matches = state.Chats
            .Where(chat => chat.Id.Equals(value, StringComparison.OrdinalIgnoreCase)
                           || chat.Id.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    internal static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static string MakeTitle(string text)
    {
        var oneLine = string.Join(' ', (text ?? "").Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (oneLine.Length == 0)
        {
            return "Новый чат";
        }

        return oneLine.Length <= 60 ? oneLine : oneLine[..59] + "…";
    }

    private static UserChatState Clone(UserChatState source) =>
        new()
        {
            CurrentChatId = source.CurrentChatId,
            Chats = source.Chats.Select(chat => new ChatState
            {
                Id = chat.Id,
                Repo = chat.Repo,
                Title = chat.Title,
                CreatedAt = chat.CreatedAt,
                UpdatedAt = chat.UpdatedAt,
                Turns = chat.Turns.ToList()
            }).ToList()
        };
}

internal sealed class UserChatState
{
    public string? CurrentChatId { get; set; }

    public List<ChatState> Chats { get; set; } = [];
}

internal sealed class ChatState
{
    public string Id { get; set; } = "";

    public string Repo { get; set; } = "";

    public string Title { get; set; } = "Новый чат";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ChatTurn> Turns { get; set; } = [];
}

internal static class ChatTranscriptRestore
{
    public const int MaxTurns = 24;
    public const int MaxChars = 16_000;
    public const int MaxFieldChars = 8_000;

    public static string DescribeParts(IReadOnlyList<PromptPart> parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return "";
        }

        var chunks = new List<string>();
        foreach (var part in parts)
        {
            switch (part.Kind)
            {
                case PromptPartKind.Text when !string.IsNullOrWhiteSpace(part.Text):
                    chunks.Add(part.Text.Trim());
                    break;
                case PromptPartKind.Image:
                    chunks.Add(string.IsNullOrWhiteSpace(part.Uri)
                        ? "[фото]"
                        : "[фото " + Path.GetFileName(part.Uri) + "]");
                    break;
                case PromptPartKind.ResourceLink:
                    chunks.Add("[файл " + (part.Name ?? part.Uri ?? "") + "]");
                    break;
            }
        }

        return Truncate(string.Join("\n", chunks), MaxFieldChars);
    }

    public static string? Format(IReadOnlyList<ChatTurn> turns, string repoPath)
    {
        if (turns is null || turns.Count == 0 || string.IsNullOrWhiteSpace(repoPath))
        {
            return null;
        }

        var repo = Path.GetFullPath(repoPath);
        var matching = turns
            .Where(turn => PathsEqual(turn.Repo, repo))
            .TakeLast(MaxTurns)
            .ToList();
        if (matching.Count == 0)
        {
            return null;
        }

        while (matching.Count > 1 && Estimate(matching) > MaxChars)
        {
            matching.RemoveAt(0);
        }

        var sb = new StringBuilder();
        sb.AppendLine("Предыдущие ходы этой Telegram-сессии восстановлены с диска воркера (процесс агента новый). Учти факты, журнал не пересказывай.");
        sb.AppendLine();
        foreach (var turn in matching)
        {
            sb.AppendLine("Пользователь:");
            sb.AppendLine(turn.User);
            sb.AppendLine();
            sb.AppendLine("Агент:");
            sb.AppendLine(string.IsNullOrWhiteSpace(turn.Assistant) ? "(нет текста)" : turn.Assistant);
            sb.AppendLine();
        }

        sb.Append("Дальше — новое сообщение пользователя.");
        return Truncate(sb.ToString().TrimEnd(), MaxChars);
    }

    private static int Estimate(IReadOnlyList<ChatTurn> turns) =>
        turns.Sum(turn => 40 + turn.User.Length + turn.Assistant.Length);

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text ?? "";
        }

        return text[..max] + "…";
    }
}

public sealed class FileChatTranscript : IChatTranscript
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly InMemoryChatTranscript _inner = new();
    private readonly HashSet<long> _loaded = [];

    public FileChatTranscript(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public static string DefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cursor.TelegramHost",
            "chats");

    public ChatThread GetOrCreateCurrent(long userId, string repo)
    {
        lock (_gate)
        {
            Load(userId);
            var chat = _inner.GetOrCreateCurrent(userId, repo);
            Save(userId);
            return chat;
        }
    }

    public ChatThread StartNew(long userId, string repo)
    {
        lock (_gate)
        {
            Load(userId);
            var chat = _inner.StartNew(userId, repo);
            Save(userId);
            return chat;
        }
    }

    public IReadOnlyList<ChatThread> ListChats(long userId)
    {
        lock (_gate)
        {
            Load(userId);
            return _inner.ListChats(userId);
        }
    }

    public bool TrySelect(long userId, string selector, out ChatThread chat)
    {
        lock (_gate)
        {
            Load(userId);
            var found = _inner.TrySelect(userId, selector, out chat);
            if (found)
            {
                Save(userId);
            }

            return found;
        }
    }

    public bool TryDelete(long userId, string selector, out ChatThread chat)
    {
        lock (_gate)
        {
            Load(userId);
            var found = _inner.TryDelete(userId, selector, out chat);
            if (found)
            {
                Save(userId);
            }

            return found;
        }
    }

    public void Append(ChatTurn turn)
    {
        lock (_gate)
        {
            Load(turn.UserId);
            _inner.Append(turn);
            Save(turn.UserId);
        }
    }

    public IReadOnlyList<ChatTurn> List(long userId, string chatId)
    {
        lock (_gate)
        {
            Load(userId);
            return _inner.List(userId, chatId);
        }
    }

    private void Load(long userId)
    {
        if (!_loaded.Add(userId))
        {
            return;
        }

        var path = FilePath(userId);
        if (File.Exists(path))
        {
            try
            {
                var state = JsonSerializer.Deserialize<UserChatState>(File.ReadAllText(path), JsonOptions);
                if (state is not null)
                {
                    state.Chats ??= [];
                    _inner.Replace(userId, state);
                    return;
                }
            }
            catch (JsonException)
            {
                // Try the previous JSONL format below.
            }
        }

        MigrateJsonLines(userId);
    }

    private void MigrateJsonLines(long userId)
    {
        var legacyPath = Path.Combine(
            _directory,
            userId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
        if (!File.Exists(legacyPath))
        {
            return;
        }

        var records = new List<LegacyTurn>();
        foreach (var line in File.ReadLines(legacyPath))
        {
            try
            {
                var record = JsonSerializer.Deserialize<LegacyTurn>(line, JsonOptions);
                if (record is not null && !string.IsNullOrWhiteSpace(record.User))
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // Skip a broken legacy line.
            }
        }

        if (records.Count == 0)
        {
            return;
        }

        var repo = records.Last().Repo ?? "";
        var chat = _inner.StartNew(userId, repo);
        foreach (var record in records)
        {
            _inner.Append(new ChatTurn(
                userId,
                chat.Id,
                record.Repo ?? repo,
                record.User!,
                record.Assistant ?? "",
                record.At == default ? DateTimeOffset.UtcNow : record.At));
        }

        Save(userId);
        File.Move(legacyPath, legacyPath + ".migrated", overwrite: true);
    }

    private void Save(long userId)
    {
        Directory.CreateDirectory(_directory);
        var path = FilePath(userId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_inner.Snapshot(userId), JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private string FilePath(long userId) =>
        Path.Combine(
            _directory,
            userId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");

    private sealed class LegacyTurn
    {
        public DateTimeOffset At { get; set; }

        public string? Repo { get; set; }

        public string? User { get; set; }

        public string? Assistant { get; set; }
    }
}
