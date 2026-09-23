namespace Cursor.Agent;

public enum PromptPartKind
{
    Text,
    Image,
    ResourceLink
}

/// <summary>
/// One block of a multimodal ACP <c>session/prompt</c>.
/// </summary>
public sealed record PromptPart(
    PromptPartKind Kind,
    string? Text = null,
    string? MimeType = null,
    string? DataBase64 = null,
    string? Uri = null,
    string? Name = null)
{
    public static PromptPart FromText(string text) =>
        new(PromptPartKind.Text, Text: text);

    public static PromptPart FromImage(string mimeType, string dataBase64, string? uri = null) =>
        new(PromptPartKind.Image, MimeType: mimeType, DataBase64: dataBase64, Uri: uri);

    public static PromptPart FromResourceLink(string uri, string name, string? mimeType = null) =>
        new(PromptPartKind.ResourceLink, Uri: uri, Name: name, MimeType: mimeType);
}
