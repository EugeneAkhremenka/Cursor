using System.Globalization;
using System.Text.RegularExpressions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Cursor.Telegram;

internal readonly record struct TelegramInboundRef(
    string FileId,
    string FileName,
    string? MimeType,
    long? DeclaredSize,
    bool PreferImagePrompt);

internal static partial class TelegramInboundAttachment
{
    public const string InboxFolder = "telegram-inbox";
    public const long MaxDownloadBytes = 20L * 1024 * 1024;
    public const long MaxEmbeddedImageBytes = 4L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif"
    };

    public static bool TryDescribe(Message message, out TelegramInboundRef attachment, out string error)
    {
        attachment = default;
        error = "";
        switch (message.Type)
        {
            case MessageType.Photo when message.Photo is { Length: > 0 } photos:
                var photo = photos[^1];
                attachment = new TelegramInboundRef(
                    photo.FileId,
                    "photo.jpg",
                    "image/jpeg",
                    photo.FileSize,
                    PreferImagePrompt: true);
                return true;
            case MessageType.Document when message.Document is { } doc:
                attachment = new TelegramInboundRef(
                    doc.FileId,
                    PreferFileName(doc.FileName, "document.bin"),
                    doc.MimeType,
                    doc.FileSize,
                    PreferImagePrompt: IsImageName(doc.FileName) || IsImageMime(doc.MimeType));
                return true;
            case MessageType.Audio when message.Audio is { } audio:
                attachment = new TelegramInboundRef(
                    audio.FileId,
                    PreferFileName(audio.FileName, ExtensionFromMime(audio.MimeType, "audio.ogg")),
                    audio.MimeType,
                    audio.FileSize,
                    PreferImagePrompt: false);
                return true;
            case MessageType.Voice when message.Voice is { } voice:
                attachment = new TelegramInboundRef(
                    voice.FileId,
                    "voice.ogg",
                    voice.MimeType ?? "audio/ogg",
                    voice.FileSize,
                    PreferImagePrompt: false);
                return true;
            case MessageType.Video when message.Video is { } video:
                attachment = new TelegramInboundRef(
                    video.FileId,
                    PreferFileName(video.FileName, ExtensionFromMime(video.MimeType, "video.mp4")),
                    video.MimeType,
                    video.FileSize,
                    PreferImagePrompt: false);
                return true;
            case MessageType.Animation when message.Animation is { } animation:
                attachment = new TelegramInboundRef(
                    animation.FileId,
                    PreferFileName(animation.FileName, ExtensionFromMime(animation.MimeType, "animation.mp4")),
                    animation.MimeType,
                    animation.FileSize,
                    PreferImagePrompt: IsImageMime(animation.MimeType));
                return true;
            case MessageType.VideoNote when message.VideoNote is { } note:
                attachment = new TelegramInboundRef(
                    note.FileId,
                    "video_note.mp4",
                    "video/mp4",
                    note.FileSize,
                    PreferImagePrompt: false);
                return true;
            case MessageType.Sticker when message.Sticker is { IsAnimated: false, IsVideo: false } sticker:
                attachment = new TelegramInboundRef(
                    sticker.FileId,
                    "sticker.webp",
                    "image/webp",
                    sticker.FileSize,
                    PreferImagePrompt: true);
                return true;
            default:
                error = "Этот тип сообщения не принимаю. Пришлите текст, фото или документ.";
                return false;
        }
    }

    public static string ResolveSavePath(string repoPath, string fileName)
    {
        var safe = SanitizeFileName(fileName);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var inbox = Path.Combine(repoPath, InboxFolder);
        Directory.CreateDirectory(inbox);
        return Path.Combine(inbox, stamp + "-" + safe);
    }

    public static IReadOnlyList<Cursor.Agent.PromptPart> BuildPromptParts(
        string caption,
        string absolutePath,
        string repoPath,
        string fileName,
        string? mimeType,
        bool preferImagePrompt,
        byte[] bytes)
    {
        var relative = ToRelative(repoPath, absolutePath);
        var uri = new Uri(absolutePath).AbsoluteUri;
        var text = BuildPromptText(caption, relative);
        var parts = new List<Cursor.Agent.PromptPart> { Cursor.Agent.PromptPart.FromText(text) };

        if (preferImagePrompt
            && bytes.Length > 0
            && bytes.Length <= MaxEmbeddedImageBytes
            && TryImageMime(fileName, mimeType, out var imageMime))
        {
            parts.Add(Cursor.Agent.PromptPart.FromImage(imageMime, Convert.ToBase64String(bytes), uri));
        }
        else
        {
            parts.Add(Cursor.Agent.PromptPart.FromResourceLink(uri, fileName, mimeType));
        }

        return parts;
    }

    public static string BuildPromptText(string? caption, string relativePath)
    {
        var body = string.IsNullOrWhiteSpace(caption)
            ? "Пользователь прислал файл через Telegram. Посмотри его и учти в ответе."
            : caption.Trim();
        return body + "\n\nФайл сохранён в репозитории: " + relativePath;
    }

    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "file.bin" : fileName.Trim());
        name = InvalidFileNameChars().Replace(name, "_");
        name = name.Trim(' ', '.');
        if (name.Length == 0 || name.All(static c => c == '_'))
        {
            return "file.bin";
        }

        return name.Length <= 120 ? name : name[..120];
    }

    private static string PreferFileName(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

    private static string ExtensionFromMime(string? mime, string fallbackName)
    {
        return mime?.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "audio.mp3",
            "audio/ogg" => "audio.ogg",
            "audio/wav" or "audio/x-wav" => "audio.wav",
            "video/mp4" => "video.mp4",
            "video/webm" => "video.webm",
            "image/png" => "image.png",
            "image/jpeg" => "image.jpg",
            "image/webp" => "image.webp",
            "image/gif" => "image.gif",
            _ => fallbackName
        };
    }

    private static bool IsImageName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ImageExtensions.Contains(Path.GetExtension(name));

    private static bool IsImageMime(string? mime) =>
        !string.IsNullOrWhiteSpace(mime)
        && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !mime.Contains("svg", StringComparison.OrdinalIgnoreCase);

    private static bool TryImageMime(string fileName, string? mimeType, out string mime)
    {
        if (IsImageMime(mimeType))
        {
            mime = mimeType!;
            return true;
        }

        mime = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => ""
        };
        return mime.Length > 0;
    }

    private static string ToRelative(string repoPath, string absolutePath)
    {
        try
        {
            var relative = Path.GetRelativePath(repoPath, absolutePath);
            return string.IsNullOrWhiteSpace(relative) ? absolutePath : relative.Replace('\\', '/');
        }
        catch
        {
            return absolutePath;
        }
    }

    [GeneratedRegex(@"[^\w\.\- \(\)\+]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidFileNameChars();
}
