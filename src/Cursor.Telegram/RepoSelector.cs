using System.Diagnostics.CodeAnalysis;

namespace Cursor.Telegram;

public static class RepoSelector
{
    public static IReadOnlyList<RepoEntry> List(Cursor.Agent.CursorAgentOptions options, string currentPath)
    {
        var entries = new List<RepoEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, rawPath) in EnumerateConfigured(options))
        {
            if (!TryNormalize(rawPath, out var path, out _))
            {
                continue;
            }

            if (!seen.Add(path))
            {
                continue;
            }

            entries.Add(new RepoEntry(name, path, PathsEqual(path, currentPath)));
        }

        if (!string.IsNullOrWhiteSpace(currentPath)
            && TryNormalize(currentPath, out var current, out _)
            && seen.Add(current))
        {
            entries.Insert(0, new RepoEntry("current", current, true));
        }

        return entries;
    }

    public static bool TryResolve(
        Cursor.Agent.CursorAgentOptions options,
        string selector,
        [NotNullWhen(true)] out string? path,
        [NotNullWhen(false)] out string? error)
    {
        path = null;
        error = null;
        if (string.IsNullOrWhiteSpace(selector))
        {
            error = "Укажите имя из /repo или абсолютный путь.";
            return false;
        }

        var trimmed = selector.Trim().Trim('"');
        foreach (var (name, rawPath) in EnumerateConfigured(options))
        {
            if (!string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryNormalize(rawPath, out path, out error))
            {
                return false;
            }

            return Directory.Exists(path) || FailMissing(path, out error);
        }

        if (!LooksLikePath(trimmed))
        {
            error = $"Неизвестное имя «{trimmed}». /repo покажет список.";
            return false;
        }

        if (!TryNormalize(trimmed, out path, out error))
        {
            return false;
        }

        return Directory.Exists(path) || FailMissing(path, out error);
    }

    public static bool TryNormalize(string rawPath, [NotNullWhen(true)] out string? path, [NotNullWhen(false)] out string? error)
    {
        path = null;
        error = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "Путь к репозиторию пустой.";
            return false;
        }

        var expanded = ExpandHome(rawPath.Trim().Trim('"'));
        try
        {
            path = Path.GetFullPath(expanded);
            return true;
        }
        catch (Exception ex)
        {
            error = "Некорректный путь: " + ex.Message;
            return false;
        }
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string FormatList(IReadOnlyList<RepoEntry> entries, string currentPath)
    {
        var lines = new List<string>
        {
            $"Текущий: {(string.IsNullOrWhiteSpace(currentPath) ? "(не задан)" : currentPath)}",
            "",
            "Репозитории:"
        };

        if (entries.Count == 0)
        {
            lines.Add("(пусто — задайте Cursor:RepoPath / Cursor:Repos или /repo /path)");
        }
        else
        {
            foreach (var entry in entries)
            {
                var mark = entry.IsCurrent ? "*" : " ";
                lines.Add($"{mark} {entry.Name}  {entry.Path}");
            }
        }

        lines.Add("");
        lines.Add("/repo <имя|путь> — переключить checkout и сбросить сессию.");
        return string.Join('\n', lines);
    }

    private static IEnumerable<(string Name, string Path)> EnumerateConfigured(Cursor.Agent.CursorAgentOptions options)
    {
        if (options.Repos is not null)
        {
            foreach (var pair in options.Repos)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                yield return (pair.Key.Trim(), pair.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.RepoPath))
        {
            yield return ("default", options.RepoPath);
        }
    }

    private static bool LooksLikePath(string selector) =>
        selector.StartsWith('/')
        || selector.StartsWith('~')
        || selector.StartsWith('.')
        || selector.Contains(Path.DirectorySeparatorChar)
        || selector.Contains(Path.AltDirectorySeparatorChar)
        || (selector.Length >= 2 && char.IsLetter(selector[0]) && selector[1] == ':');

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/") || path.StartsWith("~\\"))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return path;
    }

    private static bool FailMissing(string path, out string? error)
    {
        error = "Каталог не найден: " + path;
        return false;
    }
}

public readonly record struct RepoEntry(string Name, string Path, bool IsCurrent);
