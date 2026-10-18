namespace Cursor.Telegram;

internal enum TelegramSendKind
{
    Photo,
    Document
}

internal readonly record struct TelegramPreparedFile(
    string Path,
    string FileName,
    TelegramSendKind Kind,
    long Length);

internal static class TelegramOutboundFile
{
    public const long MaxDocumentBytes = 49L * 1024 * 1024;
    public const long MaxPhotoBytes = 10L * 1024 * 1024;

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    public static bool TryPrepare(
        string repoPath,
        string? rawPath,
        out TelegramPreparedFile file,
        out string error)
    {
        file = default;
        error = "";
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = Usage();
            return false;
        }

        var trimmed = rawPath.Trim().Trim('"').Trim('\'');
        if (!RepoSelector.TryNormalize(ResolveAgainstRepo(repoPath, trimmed), out var fullPath, out var normalizeError))
        {
            error = normalizeError ?? "Некорректный путь.";
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            error = "Это каталог. Укажите файл.";
            return false;
        }

        if (!System.IO.File.Exists(fullPath))
        {
            error = "Файл не найден: " + fullPath;
            return false;
        }

        var info = new FileInfo(fullPath);
        var length = info.Length;
        if (length <= 0)
        {
            error = "Файл пустой.";
            return false;
        }

        if (length > MaxDocumentBytes)
        {
            error = $"Файл больше лимита Telegram для бота (50 МБ): {info.Name}.";
            return false;
        }

        var kind = IsPhoto(info.Name) && length <= MaxPhotoBytes
            ? TelegramSendKind.Photo
            : TelegramSendKind.Document;

        file = new TelegramPreparedFile(fullPath, info.Name, kind, length);
        return true;
    }

    public static string Usage() =>
        """
        /files — список свежих файлов с номерами
        /send <номер> — прислать из списка
        /send last — самый свежий
        /send <алиас> — из Telegram:SendAliases (/aliases)
        /send <путь> — относительный от репо или абсолютный
        """;

    private static string ResolveAgainstRepo(string repoPath, string path)
    {
        if (Path.IsPathRooted(path)
            || path.StartsWith('~')
            || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))
        {
            return path;
        }

        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return path;
        }

        return Path.Combine(repoPath, path);
    }

    private static bool IsPhoto(string fileName) =>
        PhotoExtensions.Contains(Path.GetExtension(fileName));
}
