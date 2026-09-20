using System.Text;
using Cursor.Agent;

namespace Cursor.Telegram;

internal sealed class ActivityTranscript
{
    private const int MaxThoughtChars = 700;
    private const int MaxToolLines = 20;
    private const int MaxMessageChars = 3500;

    private readonly StringBuilder _thought = new();
    private readonly List<(string Id, string Line)> _tools = [];
    private string? _plan;

    public void Add(AgentActivityEvent ev)
    {
        switch (ev.Kind)
        {
            case AgentActivityKind.Thought:
                if (!ev.IsChunk)
                {
                    _thought.Clear();
                }

                _thought.Append(ev.Text);
                break;
            case AgentActivityKind.Plan:
                _plan = ev.Text;
                break;
            case AgentActivityKind.Tool:
                UpsertTool(ev);
                break;
        }
    }

    public string Render(string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);

        if (_thought.Length > 0)
        {
            sb.AppendLine();
            sb.Append("💭 ");
            sb.AppendLine(Tail(_thought.ToString(), MaxThoughtChars));
        }

        if (!string.IsNullOrWhiteSpace(_plan))
        {
            sb.AppendLine();
            sb.AppendLine("📋 План");
            sb.AppendLine(_plan);
        }

        if (_tools.Count > 0)
        {
            sb.AppendLine();
            foreach (var tool in _tools.TakeLast(MaxToolLines))
            {
                sb.AppendLine(tool.Line);
            }
        }

        return TrimTo(sb.ToString().TrimEnd(), MaxMessageChars);
    }

    private void UpsertTool(AgentActivityEvent ev)
    {
        var id = ev.Id ?? $"anon-{_tools.Count}";
        var line = $"{ToolIcon(ev.Status)} {ev.Text}";
        var index = _tools.FindIndex(item => item.Id == id);
        if (index >= 0)
        {
            var previous = _tools[index].Line;
            if (string.IsNullOrWhiteSpace(ev.Text) || ev.Text == "tool")
            {
                var previousText = StripIcon(previous);
                line = $"{ToolIcon(ev.Status)} {previousText}";
            }

            _tools[index] = (id, line);
            return;
        }

        _tools.Add((id, line));
    }

    private static string ToolIcon(string? status) => status switch
    {
        "completed" => "✅",
        "failed" => "❌",
        "pending" => "⏳",
        _ => "🔧"
    };

    private static string StripIcon(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? line : line[(space + 1)..];
    }

    private static string Tail(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : "…" + trimmed[^max..];
    }

    private static string TrimTo(string text, int max) =>
        text.Length <= max ? text : text[^max..];
}
