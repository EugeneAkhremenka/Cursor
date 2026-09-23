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

    [Fact]
    public void CreateStartInfo_PassesModelAndMode()
    {
        var start = AgentProcess.CreateStartInfo("/usr/bin/agent", "/tmp/repo", "", "composer-2.5", "plan");
        Assert.Equal(["--model", "composer-2.5", "--mode", "plan", "acp"], start.ArgumentList.ToArray());
    }

    [Fact]
    public void CreateStartInfo_CmdWrapper_KeepsModelInsideSQuotes()
    {
        var cmd = @"C:\Users\me\AppData\Local\cursor-agent\agent.cmd";
        var start = AgentProcess.CreateStartInfo(cmd, @"C:\repo", "key", "auto", "agent");
        Assert.Equal(
            @"/d /s /c """"C:\Users\me\AppData\Local\cursor-agent\agent.cmd"" --model auto acp""",
            start.Arguments);
    }

    [Fact]
    public void CreateStartInfo_OmitsDefaultAgentMode()
    {
        var start = AgentProcess.CreateStartInfo("/usr/bin/agent", "/tmp/repo", "", "auto", "agent");
        Assert.Equal(["--model", "auto", "acp"], start.ArgumentList.ToArray());
    }

    [Fact]
    public void BuildAcpArguments_AddsEndlessRetriesBeforeAcp()
    {
        Assert.Equal(
            ["--model", "auto", "--endless-retries", "acp"],
            AgentProcess.BuildAcpArguments("auto", "agent", endlessRetries: true).ToArray());
    }

    [Fact]
    public void BuildAcpArguments_OmitsEndlessRetriesByDefault()
    {
        Assert.DoesNotContain("--endless-retries", AgentProcess.BuildAcpArguments("auto", "agent"));
    }
}
