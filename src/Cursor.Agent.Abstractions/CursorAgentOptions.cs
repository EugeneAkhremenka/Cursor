namespace Cursor.Agent;

public sealed class CursorAgentOptions
{
    public const string SectionName = "Cursor";

    public string AgentPath { get; set; } = "agent";

    public string ApiKey { get; set; } = "";

    public string RepoPath { get; set; } = "";

    /// <summary>
    /// ACP tool permission: allow-always, allow-once, or reject-once.
    /// </summary>
    public string PermissionMode { get; set; } = "allow-always";
}
