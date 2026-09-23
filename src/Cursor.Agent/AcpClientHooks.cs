namespace Cursor.Agent;

internal sealed class AcpClientHooks
{
    public IImageGenerator? Images { get; init; }

    public string WorkingDirectory { get; set; } = "";

    public IProgress<AgentActivityEvent>? Progress { get; set; }
}
