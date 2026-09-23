using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class TelegramOptionsTests
{
    [Fact]
    public void FirstAllowedUser_IsOwnerWhenOwnerIdUnset()
    {
        var options = new TelegramOptions { AllowedUserIds = [11, 22] };
        Assert.Equal(11, options.EffectiveOwnerUserId());
        Assert.True(options.CanSeeCursorRepos(11));
        Assert.False(options.CanSeeCursorRepos(22));
        Assert.True(options.UsesPersonalWorkspace(22));
        Assert.False(options.UsesPersonalWorkspace(11));
    }

    [Fact]
    public void ExplicitOwner_WinsOverAllowlistOrder()
    {
        var options = new TelegramOptions { AllowedUserIds = [11, 22], OwnerUserId = 22 };
        Assert.Equal(22, options.EffectiveOwnerUserId());
        Assert.True(options.CanSeeCursorRepos(22));
        Assert.True(options.UsesPersonalWorkspace(11));
    }

    [Fact]
    public void NoOwner_KeepsSharedCatalog()
    {
        var options = new TelegramOptions();
        Assert.Equal(0, options.EffectiveOwnerUserId());
        Assert.True(options.CanSeeCursorRepos(99));
        Assert.False(options.UsesPersonalWorkspace(99));
    }
}
