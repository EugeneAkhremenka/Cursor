namespace Cursor.Agent;

/// <summary>
/// What the agent accepts inside <c>session/prompt</c>. Text and resource links are the ACP baseline.
/// </summary>
internal readonly record struct AcpPromptCapabilities(bool Image, bool EmbeddedContext)
{
    public static AcpPromptCapabilities Unknown => new(true, true);
}
