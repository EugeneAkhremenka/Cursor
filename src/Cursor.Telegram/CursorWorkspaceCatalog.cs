using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cursor.Telegram;

internal static class CursorWorkspaceCatalog
{
    public const int MaxEntries = 20;

    public static IReadOnlyList<(string Name, string Path)> Discover(string? cursorUserDir = null)
    {
        var userDir = cursorUserDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor",
            "User");
        var found = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        AddFromStorage(Path.Combine(userDir, "globalStorage", "storage.json"), found, seen);
        AddFromWorkspaceStorage(Path.Combine(userDir, "workspaceStorage"), found, seen);

        return found
            .Take(MaxEntries)
            .Select(path => (ShortName(path), path))
            .ToList();
    }

    internal static bool TryFolderUriToPath(string? uri, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !parsed.IsFile)
        {
            return false;
        }

        path = Path.GetFullPath(Uri.UnescapeDataString(parsed.LocalPath));
        return Directory.Exists(path);
    }

    private static void AddFromStorage(string storagePath, List<string> found, HashSet<string> seen)
    {
        if (!File.Exists(storagePath))
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(storagePath));
        }
        catch (JsonException)
        {
            return;
        }

        if (root?["profileAssociations"]?["workspaces"] is JsonObject workspaces)
        {
            foreach (var key in workspaces.Select(pair => pair.Key))
            {
                TryAdd(key, found, seen);
            }
        }

        if (root?["backupWorkspaces"]?["folders"] is JsonArray folders)
        {
            foreach (var folder in folders)
            {
                TryAdd(folder?["folderUri"]?.GetValue<string>(), found, seen);
            }
        }
    }

    private static void AddFromWorkspaceStorage(string directory, List<string> found, HashSet<string> seen)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "workspace.json", SearchOption.AllDirectories))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file));
                TryAdd(node?["folder"]?.GetValue<string>(), found, seen);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryAdd(string? uri, List<string> found, HashSet<string> seen)
    {
        if (TryFolderUriToPath(uri, out var path) && seen.Add(path))
        {
            found.Add(path);
        }
    }

    internal static string ShortName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
