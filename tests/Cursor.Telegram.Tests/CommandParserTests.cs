using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class CommandParserTests
{
    [Theory]
    [InlineData("/start", "start", "")]
    [InlineData("/START", "start", "")]
    [InlineData("/task fix the test", "task", "fix the test")]
    [InlineData("/task@MyBot  hello", "task", "hello")]
    [InlineData("/new", "new", "")]
    [InlineData(" /new", "new", "")]
    [InlineData("/send pic.png", "send", "pic.png")]
    [InlineData("/file@MyBot  C:\\a.png", "file", @"C:\a.png")]
    public void Parse_Commands(string text, string name, string args)
    {
        var parsed = CommandParser.Parse(text);
        Assert.NotNull(parsed);
        Assert.Equal(name, parsed.Value.Name);
        Assert.Equal(args, parsed.Value.Arguments);
    }

    [Theory]
    [InlineData(TelegramCommandUi.Help, "help")]
    [InlineData(TelegramCommandUi.Status, "status")]
    [InlineData(TelegramCommandUi.NewSession, "new")]
    [InlineData(TelegramCommandUi.Chats, "chats")]
    [InlineData(TelegramCommandUi.Repo, "repo")]
    [InlineData(TelegramCommandUi.Files, "files")]
    [InlineData(TelegramCommandUi.Aliases, "aliases")]
    [InlineData(TelegramCommandUi.Cancel, "cancel")]
    public void Parse_ButtonLabels(string text, string name)
    {
        var parsed = CommandParser.Parse(text);
        Assert.NotNull(parsed);
        Assert.Equal(name, parsed.Value.Name);
        Assert.Equal("", parsed.Value.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("Справка")]
    public void Parse_NonCommands(string? text)
    {
        Assert.Null(CommandParser.Parse(text));
    }
}
