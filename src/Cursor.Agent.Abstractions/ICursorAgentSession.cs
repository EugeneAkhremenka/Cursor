namespace Cursor.Agent;

public interface ICursorAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    string WorkingDirectory { get; }

    AgentActivity Activity { get; }

    Task<PromptResult> PromptAsync(
        string text,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}
