using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cursor.Telegram;

public sealed class FileUserRepoMemory : IUserRepoMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly InMemoryUserRepoMemory _inner = new();

    public FileUserRepoMemory(string path)
    {
        _path = path;
        Load();
    }

    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cursor.TelegramHost",
            "user-repos.json");

    public bool TryGet(long userId, out string path)
    {
        lock (_gate)
        {
            return _inner.TryGet(userId, out path);
        }
    }

    public bool TryGetLast(out long userId, out string path)
    {
        lock (_gate)
        {
            return _inner.TryGetLast(out userId, out path);
        }
    }

    public void Remember(long userId, string path)
    {
        lock (_gate)
        {
            _inner.Remember(userId, path);
            Save();
        }
    }

    public void Forget(long userId)
    {
        lock (_gate)
        {
            _inner.Forget(userId);
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<UserRepoState>(json, JsonOptions);
            if (state?.Repos is null)
            {
                return;
            }

            foreach (var pair in state.Repos)
            {
                if (long.TryParse(pair.Key, out var userId) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    _inner.Remember(userId, pair.Value);
                }
            }

            if (state.History is not null)
            {
                foreach (var pair in state.History)
                {
                    if (!long.TryParse(pair.Key, out var userId) || pair.Value is null)
                    {
                        continue;
                    }

                    foreach (var item in pair.Value.AsEnumerable().Reverse())
                    {
                        if (!string.IsNullOrWhiteSpace(item))
                        {
                            _inner.Remember(userId, item);
                        }
                    }
                }
            }

            if (state.LastUserId is long last && _inner.TryGet(last, out var lastPath))
            {
                _inner.Remember(last, lastPath);
            }
        }
        catch (Exception)
        {
            // Broken file: start empty, next Remember overwrites.
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var state = Dump();
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Copy(tmp, _path, overwrite: true);
        File.Delete(tmp);
    }

    private UserRepoState Dump()
    {
        var repos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in _inner.Snapshot())
        {
            repos[pair.Key.ToString()] = pair.Value;
        }

        var history = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in _inner.SnapshotHistory())
        {
            history[pair.Key.ToString()] = pair.Value.ToList();
        }

        long? last = _inner.TryGetLast(out var userId, out _) ? userId : null;
        return new UserRepoState { LastUserId = last, Repos = repos, History = history };
    }

    public IReadOnlyList<string> ListHistory(long userId)
    {
        lock (_gate)
        {
            return _inner.ListHistory(userId);
        }
    }

    private sealed class UserRepoState
    {
        public long? LastUserId { get; set; }

        public Dictionary<string, string> Repos { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> History { get; set; } = new(StringComparer.Ordinal);
    }
}
