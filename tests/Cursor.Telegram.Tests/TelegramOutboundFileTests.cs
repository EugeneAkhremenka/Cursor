using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class TelegramOutboundFileTests
{
    [Fact]
    public void EmptyPath_ShowsUsage()
    {
        Assert.False(TelegramOutboundFile.TryPrepare(@"D:\repo", "  ", out _, out var error));
        Assert.Contains("/send", error);
    }

    [Fact]
    public void MissingFile()
    {
        var repo = Path.Combine(Path.GetTempPath(), "tg-send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Assert.False(TelegramOutboundFile.TryPrepare(repo, "nope.png", out _, out var error));
            Assert.Contains("не найден", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void DirectoryRejected()
    {
        var repo = Path.Combine(Path.GetTempPath(), "tg-send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Assert.False(TelegramOutboundFile.TryPrepare(repo, repo, out _, out var error));
            Assert.Contains("каталог", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void RelativePng_IsPhoto()
    {
        var repo = Path.Combine(Path.GetTempPath(), "tg-send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var file = Path.Combine(repo, "pic.png");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            Assert.True(TelegramOutboundFile.TryPrepare(repo, "pic.png", out var prepared, out _));
            Assert.Equal(TelegramSendKind.Photo, prepared.Kind);
            Assert.Equal("pic.png", prepared.FileName);
            Assert.True(PathsEqual(file, prepared.Path));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void QuotedAbsoluteTxt_IsDocument()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tg-send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "notes.txt");
        File.WriteAllText(file, "hello");
        try
        {
            Assert.True(TelegramOutboundFile.TryPrepare(@"D:\other", "\"" + file + "\"", out var prepared, out _));
            Assert.Equal(TelegramSendKind.Document, prepared.Kind);
            Assert.Equal("notes.txt", prepared.FileName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LargePng_FallsBackToDocument()
    {
        var repo = Path.Combine(Path.GetTempPath(), "tg-send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var file = Path.Combine(repo, "big.png");
        using (var stream = File.Create(file))
        {
            stream.SetLength(TelegramOutboundFile.MaxPhotoBytes + 1);
        }

        try
        {
            Assert.True(TelegramOutboundFile.TryPrepare(repo, file, out var prepared, out _));
            Assert.Equal(TelegramSendKind.Document, prepared.Kind);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
