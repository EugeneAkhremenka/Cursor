namespace Cursor.Telegram;

public readonly record struct ParsedCommand(string Name, string Arguments);

public static class CommandParser
{
    public static ParsedCommand? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
        {
            return null;
        }

        var trimmed = text.Trim();
        var space = trimmed.IndexOf(' ');
        var head = space < 0 ? trimmed : trimmed[..space];
        var name = head.TrimStart('/');
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            name = name[..at];
        }

        if (name.Length == 0)
        {
            return null;
        }

        var args = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        return new ParsedCommand(name.ToLowerInvariant(), args);
    }
}
