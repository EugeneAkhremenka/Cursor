namespace Cursor.Telegram;

public interface IUserRepoMemory
{
    bool TryGet(long userId, out string path);

    bool TryGetLast(out long userId, out string path);

    void Remember(long userId, string path);

    void Forget(long userId);

    IReadOnlyList<string> ListHistory(long userId);
}
