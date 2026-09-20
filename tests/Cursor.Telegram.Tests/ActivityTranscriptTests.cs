using Cursor.Agent;
using Cursor.Telegram;

namespace Cursor.Telegram.Tests;

public sealed class ActivityTranscriptTests
{
    [Fact]
    public void Render_ShowsThoughtToolsAndPlan()
    {
        var log = new ActivityTranscript();
        log.Add(new AgentActivityEvent(AgentActivityKind.Thought, "Look at handler", IsChunk: true));
        log.Add(new AgentActivityEvent(AgentActivityKind.Plan, "✅ Explore\n▶ Edit"));
        log.Add(new AgentActivityEvent(AgentActivityKind.Tool, "read Foo.cs", Id: "t1", Status: "in_progress"));
        log.Add(new AgentActivityEvent(AgentActivityKind.Tool, "", Id: "t1", Status: "completed"));

        var text = log.Render("⏳ Работаю…");

        Assert.Contains("⏳ Работаю…", text);
        Assert.Contains("💭 Look at handler", text);
        Assert.Contains("📋 План", text);
        Assert.Contains("✅ Explore", text);
        Assert.Contains("✅ read Foo.cs", text);
        Assert.DoesNotContain("🔧 read Foo.cs", text);
    }

    [Fact]
    public void Add_IgnoresAssistantChunks()
    {
        var log = new ActivityTranscript();
        log.Add(new AgentActivityEvent(AgentActivityKind.Assistant, "final answer", IsChunk: true));
        var text = log.Render("⏳ Работаю…");
        Assert.Equal("⏳ Работаю…", text);
    }
}
