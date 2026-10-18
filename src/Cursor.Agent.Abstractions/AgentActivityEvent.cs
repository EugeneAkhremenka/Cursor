namespace Cursor.Agent;

public enum AgentActivityKind
{
    Thought,
    Tool,
    Assistant,
    Plan,
    Image
}

public sealed record AgentActivityEvent(
    AgentActivityKind Kind,
    string Text,
    bool IsChunk = false,
    string? Id = null,
    string? Status = null);
