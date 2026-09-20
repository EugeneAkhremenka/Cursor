using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class TelegramTextTests
{
    [Fact]
    public void Split_ShortText_IsSinglePart()
    {
        var parts = TelegramText.Split("hello", 20);
        Assert.Equal(["hello"], parts);
    }

    [Fact]
    public void Split_BreaksOnNewlineWhenPossible()
    {
        var text = "aaaa\nbbbb\ncccc";
        var parts = TelegramText.Split(text, 8);
        Assert.True(parts.Count >= 2);
        Assert.Equal(text, string.Concat(parts));
    }

    [Fact]
    public void AccessDenied_IsExplicit()
    {
        Assert.Equal("Access denied.", TelegramText.AccessDenied());
    }

    [Fact]
    public void GroupsDisabled_MentionsSetting()
    {
        Assert.Contains("AllowGroupChats", TelegramText.GroupsDisabled());
    }

    [Fact]
    public void FormatPromptResult_Busy()
    {
        var text = TelegramText.FormatPromptResult(new PromptResultEnvelope(false, "", null, false, true));
        Assert.Contains("уже работает", text);
    }

    [Fact]
    public void Help_MentionsSend()
    {
        Assert.Contains("/send", TelegramText.Help());
    }
}
