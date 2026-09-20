using Cursor.Agent;
using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class RepoSelectorTests
{
    [Fact]
    public void TryResolve_ByConfiguredName()
    {
        var root = CreateTempDir();
        var options = new CursorAgentOptions
        {
            Repos = { ["app"] = root }
        };

        Assert.True(RepoSelector.TryResolve(options, "APP", out var path, out var error));
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(root), path);
    }

    [Fact]
    public void TryResolve_UnknownName_Fails()
    {
        var options = new CursorAgentOptions { RepoPath = "/tmp" };
        Assert.False(RepoSelector.TryResolve(options, "nope", out _, out var error));
        Assert.Contains("Неизвестное имя", error);
    }

    [Fact]
    public void TryResolve_ExistingAbsolutePath()
    {
        var root = CreateTempDir();
        var options = new CursorAgentOptions();
        Assert.True(RepoSelector.TryResolve(options, root, out var path, out _));
        Assert.Equal(Path.GetFullPath(root), path);
    }

    [Fact]
    public void FormatList_MarksCurrent()
    {
        var app = CreateTempDir();
        var infra = CreateTempDir();
        var options = new CursorAgentOptions
        {
            Repos =
            {
                ["app"] = app,
                ["infra"] = infra
            }
        };

        var list = RepoSelector.List(options, Path.GetFullPath(infra));
        var text = RepoSelector.FormatList(list, Path.GetFullPath(infra));
        Assert.Contains("* infra", text);
        Assert.Contains("app", text);
        Assert.Contains("Настроенные:", text);
    }

    [Fact]
    public void FormatList_ShowsRecentAndCursorGroups()
    {
        var recent = CreateTempDir();
        var cursor = CreateTempDir();
        var extras = new[]
        {
            new RepoEntry(Path.GetFileName(recent), recent, true, "recent"),
            new RepoEntry(Path.GetFileName(cursor), cursor, false, "cursor")
        };

        var text = RepoSelector.FormatList(
            RepoSelector.List(new CursorAgentOptions(), recent, extras),
            recent);
        Assert.Contains("Недавние:", text);
        Assert.Contains("Из Cursor:", text);
        Assert.Contains(Path.GetFileName(recent), text);
    }

    [Fact]
    public void TryResolve_ExtraName()
    {
        var extraPath = CreateTempDir();
        var extras = new[] { new RepoEntry("shots", extraPath, false, "recent") };
        Assert.True(RepoSelector.TryResolve(new CursorAgentOptions(), "shots", out var path, out _, extras));
        Assert.Equal(Path.GetFullPath(extraPath), path);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "cursor-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
