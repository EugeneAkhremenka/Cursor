using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class TelegramSendCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tg-cat-" + Guid.NewGuid().ToString("N"));

    public TelegramSendCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Scan_NewestFirst_AndSkipsBuildOutput()
    {
        var old = Write("old.png", DateTime.UtcNow.AddHours(-3));
        var fresh = Write("fresh.png", DateTime.UtcNow);
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Write(Path.Combine("bin", "ignored.png"), DateTime.UtcNow);

        var items = TelegramSendCatalog.Scan([_root], filter: null);

        Assert.Equal([Path.GetFileName(fresh), Path.GetFileName(old)], items.Select(x => x.FileName));
    }

    [Fact]
    public void Scan_FiltersBySubstring()
    {
        Write("avatar.png", DateTime.UtcNow);
        Write("notes.txt", DateTime.UtcNow);

        var items = TelegramSendCatalog.Scan([_root], "ava");

        Assert.Single(items);
        Assert.Equal("avatar.png", items[0].FileName);
    }

    [Fact]
    public void Format_EmptyListing_MentionsRoots()
    {
        var text = TelegramSendCatalog.Format([], filter: null);
        Assert.Contains("SendRoots", text);
    }

    [Fact]
    public void Format_NumbersEntries()
    {
        Write("a.png", DateTime.UtcNow);
        var items = TelegramSendCatalog.Scan([_root], filter: null);

        var text = TelegramSendCatalog.Format(items, filter: null);

        Assert.Contains("1. a.png", text);
        Assert.Contains("/send last", text);
    }

    [Theory]
    [InlineData("last")]
    [InlineData("LAST")]
    [InlineData("последний")]
    public void IsLatestToken_Accepted(string token) =>
        Assert.True(TelegramSendCatalog.IsLatestToken(token));

    [Theory]
    [InlineData("2", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("pic.png", false)]
    public void TryParseIndex(string token, bool expected) =>
        Assert.Equal(expected, TelegramSendCatalog.TryParseIndex(token, out _));

    [Fact]
    public void TryResolveAlias_FileTarget()
    {
        var file = Write("avatar.png", DateTime.UtcNow);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["avatar"] = file };

        Assert.True(TelegramSendCatalog.TryResolveAlias(aliases, "AVATAR", out var resolved));
        Assert.Equal(file, resolved, ignoreCase: true);
    }

    [Fact]
    public void TryResolveAlias_DirectoryTarget_PicksNewest()
    {
        Write("old.png", DateTime.UtcNow.AddDays(-1));
        var fresh = Write("fresh.png", DateTime.UtcNow);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["shots"] = _root };

        Assert.True(TelegramSendCatalog.TryResolveAlias(aliases, "shots", out var resolved));
        Assert.Equal(fresh, resolved, ignoreCase: true);
    }

    [Fact]
    public void TryResolveAlias_Unknown()
    {
        Assert.False(TelegramSendCatalog.TryResolveAlias(null, "nope", out _));
        Assert.False(TelegramSendCatalog.TryResolveAlias(new Dictionary<string, string>(), "nope", out _));
    }

    private string Write(string relative, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_root, relative);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
