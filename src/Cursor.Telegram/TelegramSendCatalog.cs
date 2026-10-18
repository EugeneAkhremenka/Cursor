using System.Text;

namespace Cursor.Telegram;

internal readonly record struct SendCandidate(string Path, string FileName, long Length, DateTime LastWriteUtc);

internal static class TelegramSendCatalog
{
    public const int DefaultTake = 15;

    private const int MaxFilesExamined = 20000;

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "node_modules",
        "packages",
        "dist",
        "target"
    };

    public static IReadOnlyList<SendCandidate> Scan(
        IEnumerable<string> roots,
        string? filter,
        int take = DefaultTake)
    {
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var found = new List<SendCandidate>();
        var examined = 0;

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && examined < MaxFilesExamined)
            {
                var directory = pending.Pop();
                foreach (var child in SafeDirectories(directory))
                {
                    if (!SkipDirectories.Contains(Path.GetFileName(child)))
                    {
                        pending.Push(child);
                    }
                }

                foreach (var path in SafeFiles(directory))
                {
                    examined++;
                    var name = Path.GetFileName(path);
                    if (!Matches(name, filter) || !seen.Add(path))
                    {
                        continue;
                    }

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(path);
                        if (info.Length <= 0 || info.Length > TelegramOutboundFile.MaxDocumentBytes)
                        {
                            continue;
                        }
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    found.Add(new SendCandidate(path, name, info.Length, info.LastWriteTimeUtc));
                }
            }
        }

        return found
            .OrderByDescending(x => x.LastWriteUtc)
            .Take(Math.Max(1, take))
            .ToList();
    }

    public static string Format(IReadOnlyList<SendCandidate> items, string? filter)
    {
        if (items.Count == 0)
        {
            return string.IsNullOrWhiteSpace(filter)
                ? "Свежих файлов не нашёл. Проверьте /repo или Telegram:SendRoots."
                : $"По «{filter}» ничего нет. /files без фильтра покажет свежие.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(filter) ? "Свежие файлы:" : $"Файлы по «{filter}»:");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            sb.AppendLine($"{i + 1}. {item.FileName}  ({FormatSize(item.Length)}, {FormatAge(item.LastWriteUtc)})");
        }

        sb.AppendLine();
        sb.Append("/send <номер> — прислать. /send last — самый свежий.");
        return sb.ToString();
    }

    public static bool TryParseIndex(string token, out int index) =>
        int.TryParse(token, out index) && index > 0;

    public static bool IsLatestToken(string token) =>
        token.Equals("last", StringComparison.OrdinalIgnoreCase)
        || token.Equals("последний", StringComparison.OrdinalIgnoreCase)
        || token.Equals("свежий", StringComparison.OrdinalIgnoreCase);

    public static bool TryResolveAlias(
        IReadOnlyDictionary<string, string>? aliases,
        string token,
        out string path)
    {
        path = "";
        if (aliases is null || aliases.Count == 0)
        {
            return false;
        }

        foreach (var pair in aliases)
        {
            if (!string.Equals(pair.Key.Trim(), token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!RepoSelector.TryNormalize(pair.Value, out var full, out _))
            {
                return false;
            }

            if (Directory.Exists(full))
            {
                var newest = Scan([full], filter: null, take: 1);
                if (newest.Count == 0)
                {
                    return false;
                }

                path = newest[0].Path;
                return true;
            }

            path = full;
            return true;
        }

        return false;
    }

    public static string FormatAliases(IReadOnlyDictionary<string, string>? aliases)
    {
        if (aliases is null || aliases.Count == 0)
        {
            return "Алиасы не заданы (Telegram:SendAliases).";
        }

        var sb = new StringBuilder("Алиасы:");
        sb.AppendLine();
        foreach (var pair in aliases)
        {
            sb.AppendLine($"{pair.Key}  {pair.Value}");
        }

        return sb.ToString().TrimEnd();
    }

    private static bool Matches(string name, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.#} МБ"
            : $"{Math.Max(1, bytes / 1024)} КБ";

    private static string FormatAge(DateTime lastWriteUtc)
    {
        var age = DateTime.UtcNow - lastWriteUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 1)
        {
            return "только что";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes} мин назад";
        }

        return age.TotalHours < 24
            ? $"{(int)age.TotalHours} ч назад"
            : $"{(int)age.TotalDays} дн назад";
    }
}
