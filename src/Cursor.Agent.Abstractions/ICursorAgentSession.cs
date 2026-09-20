namespace Cursor.Agent;

public interface ICursorAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    string WorkingDirectory { get; }

    AgentActivity Activity { get; }

    Task<PromptResult> PromptAsync(
        string text,
        IProgress<AgentActivityEvent>? progress,
        CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}
