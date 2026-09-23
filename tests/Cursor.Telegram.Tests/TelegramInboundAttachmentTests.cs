using Cursor.Agent;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Cursor.Telegram.Tests;

public sealed class TelegramInboundAttachmentTests
{
    [Fact]
    public void TryDescribe_Photo_UsesLargestSize()
    {
        var message = new Message
        {
            Photo =
            [
                new PhotoSize { FileId = "small", Width = 90, Height = 90, FileSize = 100 },
                new PhotoSize { FileId = "large", Width = 1280, Height = 720, FileSize = 50_000 }
            ]
        };

        Assert.True(TelegramInboundAttachment.TryDescribe(message, out var inbound, out _));
        Assert.Equal("large", inbound.FileId);
        Assert.True(inbound.PreferImagePrompt);
        Assert.Equal("image/jpeg", inbound.MimeType);
    }

    [Fact]
    public void TryDescribe_Document_KeepsName()
    {
        var message = new Message
        {
            Document = new Document
            {
                FileId = "doc-1",
                FileName = "notes.pdf",
                MimeType = "application/pdf",
                FileSize = 1234
            }
        };

        Assert.True(TelegramInboundAttachment.TryDescribe(message, out var inbound, out _));
        Assert.Equal("notes.pdf", inbound.FileName);
        Assert.False(inbound.PreferImagePrompt);
    }

    [Fact]
    public void TryDescribe_Text_Fails()
    {
        var message = new Message { Text = "hi" };
        Assert.False(TelegramInboundAttachment.TryDescribe(message, out _, out var error));
        Assert.Contains("текст", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveSavePath_CreatesInboxAndUniqueName()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = TelegramInboundAttachment.ResolveSavePath(root, "../evil name?.png");
            Assert.StartsWith(Path.Combine(root, TelegramInboundAttachment.InboxFolder), path);
            Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
            Assert.DoesNotContain("..", Path.GetFileName(path));
            Assert.EndsWith(".png", path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildPromptParts_EmbedsSmallImage()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var absolute = Path.Combine(repo, "telegram-inbox", "shot.png");
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            var bytes = new byte[] { 1, 2, 3, 4 };
            System.IO.File.WriteAllBytes(absolute, bytes);

            var parts = TelegramInboundAttachment.BuildPromptParts(
                "что на фото?",
                absolute,
                repo,
                "shot.png",
                "image/png",
                preferImagePrompt: true,
                bytes);

            Assert.Equal(2, parts.Count);
            Assert.Equal(PromptPartKind.Text, parts[0].Kind);
            Assert.Contains("что на фото?", parts[0].Text);
            Assert.Contains("telegram-inbox/shot.png", parts[0].Text!.Replace('\\', '/'));
            Assert.Equal(PromptPartKind.Image, parts[1].Kind);
            Assert.Equal("image/png", parts[1].MimeType);
            Assert.Equal(Convert.ToBase64String(bytes), parts[1].DataBase64);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void BuildPromptParts_LargeImage_UsesResourceLink()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var absolute = Path.Combine(repo, "telegram-inbox", "big.png");
            var bytes = new byte[TelegramInboundAttachment.MaxEmbeddedImageBytes + 1];
            var parts = TelegramInboundAttachment.BuildPromptParts(
                "",
                absolute,
                repo,
                "big.png",
                "image/png",
                preferImagePrompt: true,
                bytes);

            Assert.Equal(PromptPartKind.ResourceLink, parts[1].Kind);
            Assert.Contains("Пользователь прислал файл", parts[0].Text);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void SanitizeFileName_StripsPathAndBadChars()
    {
        Assert.Equal("b.txt", TelegramInboundAttachment.SanitizeFileName(@"C:\temp\a\b.txt"));
        Assert.Equal("report_v1.pdf", TelegramInboundAttachment.SanitizeFileName("report:v1.pdf"));
        Assert.Equal("file.bin", TelegramInboundAttachment.SanitizeFileName("???"));
    }
}
