namespace Cursor.Telegram;

internal static class TelegramChatPolicy
{
    public static bool AllowsChat(
        bool isPrivate,
        bool isShared,
        bool allowGroupChats,
        long chatId,
        long? userId)
    {
        if (isPrivate)
        {
            return userId is long uid && chatId == uid;
        }

        return isShared && allowGroupChats;
    }
}
