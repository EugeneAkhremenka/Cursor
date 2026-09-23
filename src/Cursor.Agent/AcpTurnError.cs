namespace Cursor.Agent;

/// <summary>
/// The CLI's ACP adapter never fails a turn: when the model stream dies it appends
/// <c>\n\nError: &lt;message&gt;</c> to the assistant text and still answers
/// <c>session/prompt</c> with <c>end_turn</c>. Only a trailing line counts, so an error
/// quoted inside an answer stays plain text.
/// </summary>
internal static class AcpTurnError
{
    private const string Marker = "Error: ";
    private const int MaxErrorLength = 400;

    public static bool TryReadTrailing(string? text, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimEnd();
        var line = trimmed[(trimmed.LastIndexOf('\n') + 1)..].Trim();
        if (!line.StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        var message = line[Marker.Length..].Trim();
        if (message.Length == 0 || message.Length > MaxErrorLength)
        {
            return false;
        }

        error = message;
        return true;
    }

    public static string StripTrailing(string text)
    {
        var trimmed = text.TrimEnd();
        var lineStart = trimmed.LastIndexOf('\n') + 1;
        return lineStart == 0 ? "" : trimmed[..lineStart].TrimEnd();
    }
}
