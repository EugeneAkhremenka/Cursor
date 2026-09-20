using Cursor.Agent;

namespace Cursor.Agent.Tests;

public sealed class AgentProcessTests
{
    [Fact]
    public void ResolveAgentPath_FindsExplicitFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"agent-fake-{Guid.NewGuid():N}");
        File.WriteAllText(file, "#!/bin/sh\n");
        try
        {
            var resolved = AgentProcess.ResolveAgentPath(file);
            Assert.Equal(Path.GetFullPath(file), resolved);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
