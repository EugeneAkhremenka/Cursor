namespace Cursor.Telegram;

public sealed record RepoSwitchResult(
    bool Success,
    string Path,
    string? Error,
    bool Changed,
    bool Busy)
{
    public static RepoSwitchResult Ok(string path, bool changed) =>
        new(true, path, null, changed, false);

    public static RepoSwitchResult Fail(string error, string currentPath) =>
        new(false, currentPath, error, false, false);

    public static RepoSwitchResult WasBusy(string currentPath) =>
        new(false, currentPath, "Агент уже работает. /cancel чтобы сменить репо.", false, true);
}
