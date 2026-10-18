using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class UserRepoMemoryTests
{
    [Fact]
    public void FileStore_RemembersPerUser_AndReloadsLast()
    {
        var file = Path.Combine(Path.GetTempPath(), "cursor-tg-repos-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = new FileUserRepoMemory(file);
            first.Remember(111, @"D:\cursor\android");
            first.Remember(111, @"D:\cursor\yt");

            var second = new FileUserRepoMemory(file);
            Assert.True(second.TryGet(111, out var lastPath));
            Assert.Equal(@"D:\cursor\yt", lastPath);
            Assert.True(second.TryGetLast(out var lastUser, out _));
            Assert.Equal(111, lastUser);
            Assert.Equal([@"D:\cursor\yt", @"D:\cursor\android"], second.ListHistory(111));
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".tmp");
        }
    }
}
