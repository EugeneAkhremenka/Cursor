using System.Text.Json.Nodes;

namespace Cursor.Agent;

internal sealed class AcpNotification
{
    public required string Method { get; init; }

    public JsonNode? Params { get; init; }
}
