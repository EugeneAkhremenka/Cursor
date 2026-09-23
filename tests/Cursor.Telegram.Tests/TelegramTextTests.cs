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
    public void Help_MentionsSendAndButtons()
    {
        Assert.Contains("/send", TelegramText.Help());
        Assert.Contains("меню", TelegramText.Help());
        Assert.Contains("telegram-inbox", TelegramText.Help());
        Assert.Contains("/chats", TelegramText.Help());
        Assert.Contains("/continue", TelegramText.Help());
    }

    [Fact]
    public void FormatPromptResult_Failure_KeepsPartialText()
    {
        var text = TelegramText.FormatPromptResult(
            new PromptResultEnvelope(false, "успел написать", "Агент молчал 5 мин — ход прерван.", false, false));

        Assert.Contains("Ошибка: Агент молчал 5 мин", text);
        Assert.Contains("успел написать", text);
    }

    [Fact]
    public void FormatStatus_ShowsModelAndMode()
    {
        var text = TelegramText.FormatStatus(@"D:\repo", "s1", "Idle", true, "composer-2.5", "plan");
        Assert.Contains("model: composer-2.5", text);
        Assert.Contains("mode: plan", text);
    }
}
