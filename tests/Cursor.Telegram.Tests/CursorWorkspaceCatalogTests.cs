using System.Text.Json;
using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class CursorWorkspaceCatalogTests
{
    [Fact]
    public void TryFolderUriToPath_DecodesWindowsFileUri()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var uri = new Uri(root).AbsoluteUri;
        Assert.True(CursorWorkspaceCatalog.TryFolderUriToPath(uri, out var path));
        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(path));
    }

    [Fact]
    public void Discover_ReadsStorageAndWorkspaceJson()
    {
        var userDir = Directory.CreateTempSubdirectory().FullName;
        var folderA = Directory.CreateTempSubdirectory().FullName;
        var folderB = Directory.CreateTempSubdirectory().FullName;
        var storageDir = Path.Combine(userDir, "globalStorage");
        var workspaceDir = Path.Combine(userDir, "workspaceStorage", "abc");
        Directory.CreateDirectory(storageDir);
        Directory.CreateDirectory(workspaceDir);
        File.WriteAllText(
            Path.Combine(storageDir, "storage.json"),
            JsonSerializer.Serialize(new
            {
                profileAssociations = new
                {
                    workspaces = new Dictionary<string, string>
                    {
                        [new Uri(folderA).AbsoluteUri] = "__default__profile__"
                    }
                }
            }));
        File.WriteAllText(
            Path.Combine(workspaceDir, "workspace.json"),
            JsonSerializer.Serialize(new { folder = new Uri(folderB).AbsoluteUri }));

        var found = CursorWorkspaceCatalog.Discover(userDir);
        Assert.Contains(found, item => PathsEqual(item.Path, folderA));
        Assert.Contains(found, item => PathsEqual(item.Path, folderB));
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
