namespace Cursor.Telegram;

internal sealed class InMemoryUserRepoMemory : IUserRepoMemory
{
    private const int MaxHistory = 20;
    private readonly Dictionary<long, string> _repos = [];
    private readonly Dictionary<long, List<string>> _history = [];
    private long? _lastUserId;

    public bool TryGet(long userId, out string path) => _repos.TryGetValue(userId, out path!);

    public bool TryGetLast(out long userId, out string path)
    {
        path = "";
        userId = 0;
        if (_lastUserId is not long id || !_repos.TryGetValue(id, out var stored))
        {
            return false;
        }

        userId = id;
        path = stored;
        return true;
    }

    public void Remember(long userId, string path)
    {
        _repos[userId] = path;
        _lastUserId = userId;
        if (!_history.TryGetValue(userId, out var list))
        {
            list = [];
            _history[userId] = list;
        }

        list.RemoveAll(item => PathsEqual(item, path));
        list.Insert(0, path);
        if (list.Count > MaxHistory)
        {
            list.RemoveRange(MaxHistory, list.Count - MaxHistory);
        }
    }

    public void Forget(long userId)
    {
        _repos.Remove(userId);
        if (_lastUserId == userId)
        {
            _lastUserId = _repos.Count == 0 ? null : _repos.Keys.Last();
        }
    }

    public IReadOnlyList<string> ListHistory(long userId) =>
        _history.TryGetValue(userId, out var list) ? list.ToArray() : [];

    public IReadOnlyDictionary<long, string> Snapshot() => new Dictionary<long, string>(_repos);

    public IReadOnlyDictionary<long, IReadOnlyList<string>> SnapshotHistory() =>
        _history.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray());

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
