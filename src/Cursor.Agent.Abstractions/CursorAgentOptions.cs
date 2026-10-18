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
    /// Cursor CLI model id, passed as <c>agent --model</c>. Empty omits the flag (CLI default is auto).
    /// User-facing picker is tech debt — see TECHDEBT.md.
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Cursor CLI mode: plan or ask. Empty or agent omits the flag (CLI default is full agent).
    /// User-facing picker is tech debt — see TECHDEBT.md.
    /// </summary>
    public string Mode { get; set; } = "";

    /// <summary>
    /// ACP tool permission: allow-always, allow-once, or reject-once.
    /// </summary>
    public string PermissionMode { get; set; } = "allow-always";

    /// <summary>
    /// HTTP template for client-side image generation. <c>{prompt}</c> is URI-escaped.
    /// Empty uses the built-in Pollinations Flux URL.
    /// </summary>
    public string ImageApiUrl { get; set; } = "";

    /// <summary>
    /// Extra <c>session/prompt</c> attempts after a transient model-transport error
    /// (RetriableError / ECONNRESET). 0 disables retries.
    /// </summary>
    public int PromptRetries { get; set; } = 2;

    /// <summary>
    /// Delay between prompt retries.
    /// </summary>
    public int PromptRetryDelayMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Pass <c>--endless-retries</c> to the CLI so transport drops are resumed inside the agent
    /// instead of ending the turn. Bounded by <see cref="PromptIdleTimeoutSeconds"/>.
    /// </summary>
    public bool EndlessRetries { get; set; } = true;

    /// <summary>
    /// Cancel a turn after this many seconds without any activity (assistant text, thought, tool call).
    /// This is the upper bound on endless retries. 0 disables the watchdog.
    /// </summary>
    public int PromptIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Transport the CLI uses to reach the Cursor backend: <c>auto</c> (leave the CLI config as is),
    /// <c>http1</c>, or <c>http2</c>. HTTP/2 streams are the ones that die mid-turn with
    /// <c>[aborted] read ECONNRESET</c> on some networks; <c>http1</c> is the workaround, at the cost
    /// of a known CLI bug that inflates requests and trips the server's 50 MB limit in large repos.
    /// The worker writes this into the CLI's own config file, which is the only place it is read from.
    /// </summary>
    public string AgentHttpProtocol { get; set; } = "auto";
}
