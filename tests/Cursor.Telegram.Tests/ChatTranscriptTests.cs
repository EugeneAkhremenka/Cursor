using Cursor.Agent;
using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class ChatTranscriptTests
{
    [Fact]
    public void Format_FiltersByRepo_AndKeepsLatestTurns()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        var other = Directory.CreateTempSubdirectory().FullName;
        var turns = new[]
        {
            new ChatTurn(1, "other", other, "other-user", "other-agent", DateTimeOffset.UtcNow),
            new ChatTurn(1, "chat", repo, "hello", "hi", DateTimeOffset.UtcNow)
        };

        var recap = ChatTranscriptRestore.Format(turns, repo);
        Assert.NotNull(recap);
        Assert.Contains("hello", recap);
        Assert.Contains("hi", recap);
        Assert.DoesNotContain("other-user", recap);
    }

    [Fact]
    public void DescribeParts_IncludesImagePlaceholder()
    {
        var text = ChatTranscriptRestore.DescribeParts(
        [
            PromptPart.FromText("see"),
            PromptPart.FromImage("image/png", "AQID", "file:///tmp/shot.png")
        ]);
        Assert.Contains("see", text);
        Assert.Contains("[фото", text);
    }

    [Fact]
    public void FileStore_RoundTripsSelectsAndDeletes()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new FileChatTranscript(dir);
        var chat = store.StartNew(9, @"D:\repo");
        store.Append(new ChatTurn(9, chat.Id, @"D:\repo", "ping", "pong", DateTimeOffset.UtcNow));

        var loaded = store.List(9, chat.Id);
        Assert.Single(loaded);
        Assert.Equal("ping", loaded[0].User);
        Assert.Equal("pong", loaded[0].Assistant);
        Assert.True(store.TrySelect(9, chat.Id, out var selected));
        Assert.Equal(chat.Id, selected.Id);

        Assert.True(store.TryDelete(9, chat.Id, out var deleted));
        Assert.Equal(chat.Id, deleted.Id);
        Assert.Empty(store.List(9, chat.Id));
    }
}
