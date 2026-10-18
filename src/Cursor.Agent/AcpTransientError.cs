namespace Cursor.Agent;

internal static class AcpTransientError
{
    public static bool IsRetriable(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return false;
        }

        return IsRetriable(exception.Message);
    }

    public static bool IsRetriable(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return Contains(message, "RetriableError")
            || Contains(message, "ECONNRESET")
            || Contains(message, "ECONNABORTED")
            || Contains(message, "ETIMEDOUT")
            || Contains(message, "ENETUNREACH")
            || Contains(message, "EAI_AGAIN")
            || Contains(message, "UND_ERR_")
            || Contains(message, "socket hang up")
            || Contains(message, "[aborted]");
    }

    private static bool Contains(string message, string token) =>
        message.Contains(token, StringComparison.OrdinalIgnoreCase);
}
