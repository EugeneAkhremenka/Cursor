using Cursor.Agent;
using System.Diagnostics;

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

    [Fact]
    public void WellKnownPaths_IncludeWindowsCursorAgent()
    {
        var paths = AgentProcess.WellKnownPaths("agent").ToList();
        Assert.Contains(paths, p => p.Replace('\\', '/').Contains("cursor-agent/agent.cmd"));
    }

    [Fact]
    public void CreateStartInfo_WrapsCmdScripts()
    {
        var cmd = @"C:\Users\me\AppData\Local\cursor-agent\agent.cmd";
        var start = AgentProcess.CreateStartInfo(cmd, @"C:\repo", "key");
        Assert.True(AgentProcess.NeedsCmdWrapper(cmd));
        Assert.Contains("agent.cmd", start.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("acp", start.Arguments, StringComparison.Ordinal);
        Assert.Equal("key", start.Environment["CURSOR_API_KEY"]);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_DirectBinary_UsesArgumentList()
    {
        var start = AgentProcess.CreateStartInfo("/usr/bin/agent", "/tmp/repo", "");
        Assert.Equal("/usr/bin/agent", start.FileName);
        Assert.Equal(["acp"], start.ArgumentList.ToArray());
        Assert.Equal("/tmp/repo", start.WorkingDirectory);
    }
}
