using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class TelegramChatPolicyTests
{
    [Fact]
    public void PrivateChat_AllowsMatchingUser()
    {
        Assert.True(TelegramChatPolicy.AllowsChat(true, false, allowGroupChats: false, chatId: 137, userId: 137));
    }

    [Fact]
    public void PrivateChat_RejectsMismatchedUser()
    {
        Assert.False(TelegramChatPolicy.AllowsChat(true, false, allowGroupChats: true, chatId: 137, userId: 999));
    }

    [Fact]
    public void SharedChat_RejectedByDefault()
    {
        Assert.False(TelegramChatPolicy.AllowsChat(false, true, allowGroupChats: false, chatId: -100, userId: 137));
    }

    [Fact]
    public void SharedChat_AllowedWhenOptedIn()
    {
        Assert.True(TelegramChatPolicy.AllowsChat(false, true, allowGroupChats: true, chatId: -100, userId: 137));
    }

    [Fact]
    public void Channel_AlwaysRejected()
    {
        Assert.False(TelegramChatPolicy.AllowsChat(false, false, allowGroupChats: true, chatId: -200, userId: 137));
    }
}
