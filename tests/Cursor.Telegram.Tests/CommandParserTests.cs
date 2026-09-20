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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData(" /not-because-space")]
    public void Parse_NonCommands(string? text)
    {
        Assert.Null(CommandParser.Parse(text));
    }
}
