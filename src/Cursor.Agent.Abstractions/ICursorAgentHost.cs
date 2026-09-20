namespace Cursor.Agent;

public interface ICursorAgentHost
{
    Task<ICursorAgentSession> CreateSessionAsync(
        string workingDirectory,
        CancellationToken cancellationToken);
}
