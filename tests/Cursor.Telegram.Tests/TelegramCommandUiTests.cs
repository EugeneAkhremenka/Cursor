using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class TelegramCommandUiTests
{
    [Fact]
    public void RepoKeyboard_UsesIndexes()
    {
        var entries = new[]
        {
            new RepoEntry("tg", @"D:\cursor\tg", true),
            new RepoEntry("android", @"D:\cursor\android", false)
        };

        var keyboard = TelegramCommandUi.RepoKeyboard(entries);
        Assert.NotNull(keyboard);
        var buttons = keyboard.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Equal("repo:1", buttons[0].CallbackData);
        Assert.StartsWith("• ", buttons[0].Text);
        Assert.Equal("repo:2", buttons[1].CallbackData);
    }

    [Fact]
    public void FilesKeyboard_AddsLastButton()
    {
        var items = new[]
        {
            new SendCandidate(@"D:\a\logo.png", "logo.png", 100, DateTime.UtcNow)
        };

        var keyboard = TelegramCommandUi.FilesKeyboard(items);
        Assert.NotNull(keyboard);
        var data = keyboard.InlineKeyboard.SelectMany(row => row).Select(button => button.CallbackData).ToList();
        Assert.Contains("send:1", data);
        Assert.Contains("send:last", data);
    }

    [Fact]
    public void ChatsKeyboard_ContinuesById()
    {
        var now = DateTimeOffset.UtcNow;
        var chats = new[]
        {
            new ChatThread("abc12345", @"D:\repo", "Fix tests", now, now, 2, true)
        };

        var keyboard = TelegramCommandUi.ChatsKeyboard(chats);
        Assert.NotNull(keyboard);
        var buttons = keyboard.InlineKeyboard.SelectMany(row => row).ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Equal("chat:abc12345", buttons[0].CallbackData);
        Assert.Equal("delchat:abc12345", buttons[1].CallbackData);
        Assert.StartsWith("• ", buttons[0].Text);
    }

    [Theory]
    [InlineData("repo:2", "repo", "2")]
    [InlineData("send:last", "send", "last")]
    [InlineData("cmd:status", "cmd", "status")]
    [InlineData("chat:abc12345", "chat", "abc12345")]
    [InlineData("delchat:abc12345", "delchat", "abc12345")]
    public void TryParseCallback_Valid(string data, string action, string argument)
    {
        Assert.True(TelegramCommandUi.TryParseCallback(data, out var parsedAction, out var parsedArgument));
        Assert.Equal(action, parsedAction);
        Assert.Equal(argument, parsedArgument);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("repo")]
    [InlineData("repo:")]
    [InlineData("other:1")]
    public void TryParseCallback_Invalid(string? data)
    {
        Assert.False(TelegramCommandUi.TryParseCallback(data, out _, out _));
    }
}
