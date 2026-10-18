namespace Cursor.Agent;

public sealed record AgentSessionStatus(
    string? SessionId,
    string WorkingDirectory,
    AgentActivity Activity,
    bool HasSession,
    string Model = "",
    string Mode = "");
