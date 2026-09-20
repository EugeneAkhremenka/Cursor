using System.Text.Json.Nodes;
using Cursor.Agent;

namespace Cursor.Agent.Tests;

public sealed class CursorAcpProtocolActivityTests
{
    [Fact]
    public void ReadActivity_ThoughtChunk()
    {
        var ev = CursorAcpProtocol.ReadActivity(Update("agent_thought_chunk", """
            {"content":{"type":"text","text":"hmm"}}
            """));

        Assert.NotNull(ev);
        Assert.Equal(AgentActivityKind.Thought, ev.Kind);
        Assert.True(ev.IsChunk);
        Assert.Equal("hmm", ev.Text);
    }

    [Fact]
    public void ReadActivity_ToolCallUsesTitleAndPath()
    {
        var ev = CursorAcpProtocol.ReadActivity(Update("tool_call", """
            {
              "toolCallId":"c1",
              "name":"read",
              "title":"Reading handler",
              "status":"in_progress",
              "locations":[{"path":"D:\\\\repo\\\\Foo.cs"}]
            }
            """));

        Assert.NotNull(ev);
        Assert.Equal(AgentActivityKind.Tool, ev.Kind);
        Assert.Equal("c1", ev.Id);
        Assert.Equal("in_progress", ev.Status);
        Assert.Contains("Reading handler", ev.Text);
        Assert.Contains("Foo.cs", ev.Text);
    }

    [Fact]
    public void ReadActivity_ToolCallFallsBackToCommand()
    {
        var ev = CursorAcpProtocol.ReadActivity(Update("tool_call", """
            {
              "toolCallId":"c2",
              "name":"execute",
              "status":"pending",
              "rawInput":{"command":"git status"}
            }
            """));

        Assert.NotNull(ev);
        Assert.Contains("git status", ev.Text);
    }

    [Fact]
    public void ReadActivity_PlanEntries()
    {
        var ev = CursorAcpProtocol.ReadActivity(Update("plan", """
            {
              "entries":[
                {"content":"Look around","status":"completed"},
                {"content":"Edit file","status":"in_progress"}
              ]
            }
            """));

        Assert.NotNull(ev);
        Assert.Equal(AgentActivityKind.Plan, ev.Kind);
        Assert.Contains("Look around", ev.Text);
        Assert.Contains("Edit file", ev.Text);
    }

    [Fact]
    public void ReadAssistantChunk_StillWorks()
    {
        var chunk = CursorAcpProtocol.ReadAssistantChunk(Update("agent_message_chunk", """
            {"content":{"type":"text","text":"hello"}}
            """));
        Assert.Equal("hello", chunk);
    }

    private static AcpNotification Update(string kind, string updateJson)
    {
        var update = JsonNode.Parse(updateJson)!.AsObject();
        update["sessionUpdate"] = kind;
        return new AcpNotification
        {
            Method = "session/update",
            Params = new JsonObject
            {
                ["sessionId"] = "s1",
                ["update"] = update
            }
        };
    }
}
