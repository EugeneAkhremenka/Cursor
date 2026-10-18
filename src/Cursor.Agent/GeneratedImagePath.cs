namespace Cursor.Agent;

internal static class GeneratedImagePath
{
    public const int MinBytes = 32;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif"
    };

    public static string Resolve(string workingDirectory, string? requested, string description)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        var name = ChooseFileName(requested, description);
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(Path.Combine(cwd, "assets", name));
        }

        var trimmed = requested.Trim().Trim('"', '\'');
        var directoryHint = LooksLikeDirectory(trimmed);
        string candidate;
        if (Path.IsPathRooted(trimmed)
            || (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'))
        {
            candidate = Path.GetFullPath(trimmed);
        }
        else
        {
            candidate = Path.GetFullPath(Path.Combine(cwd, trimmed));
        }

        if (directoryHint)
        {
            candidate = Path.Combine(candidate, name);
        }

        if (!HasImageExtension(candidate))
        {
            candidate += ".png";
        }

        if (!IsUnder(cwd, candidate))
        {
            candidate = Path.GetFullPath(Path.Combine(cwd, "assets", Path.GetFileName(candidate)));
        }

        return candidate;
    }

    public static bool HasContent(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= MinBytes;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static void DeleteIfIncomplete(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length >= MinBytes)
            {
                return;
            }

            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ChooseFileName(string? requested, string description)
    {
        var fromRequest = Path.GetFileName((requested ?? "").Trim().Trim('"', '\''));
        if (!string.IsNullOrWhiteSpace(fromRequest) && fromRequest is not "." and not "..")
        {
            var stem = Path.GetFileNameWithoutExtension(fromRequest);
            var ext = Path.GetExtension(fromRequest);
            if (string.IsNullOrEmpty(ext) || !ImageExtensions.Contains(ext))
            {
                ext = ".png";
            }

            var safe = Sanitize(stem);
            return (string.IsNullOrWhiteSpace(safe) ? "generated" : safe) + ext;
        }

        var slug = Sanitize(description);
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "generated";
        }

        return $"{slug}.png";
    }

    private static bool LooksLikeDirectory(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
        || path.EndsWith(Path.AltDirectorySeparatorChar);

    private static bool HasImageExtension(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    private static bool IsUnder(string root, string candidate)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var chars = text.Trim().ToCharArray();
        var written = 0;
        var dash = false;
        foreach (var ch in chars)
        {
            if (written >= 40)
            {
                break;
            }

            if (char.IsLetterOrDigit(ch))
            {
                chars[written++] = char.ToLowerInvariant(ch);
                dash = false;
                continue;
            }

            if (!dash && written > 0)
            {
                chars[written++] = '-';
                dash = true;
            }
        }

        return written == 0 ? "" : new string(chars, 0, written).Trim('-');
    }
}
