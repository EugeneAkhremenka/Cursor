namespace Cursor.Agent;

public sealed class CursorAgentOptions
{
    public const string SectionName = "Cursor";

    public string AgentPath { get; set; } = "agent";

    public string ApiKey { get; set; } = "";

    public string RepoPath { get; set; } = "";

    /// <summary>
    /// Named checkouts that Telegram can switch with /repo &lt;name&gt;.
    /// </summary>
    public Dictionary<string, string> Repos { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ACP tool permission: allow-always, allow-once, or reject-once.
    /// </summary>
    public string PermissionMode { get; set; } = "allow-always";
}
