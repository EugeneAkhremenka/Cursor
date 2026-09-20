using System.Text.Json;

namespace Cursor.Agent;

public sealed class AcpRpcException : Exception
{
    public int Code { get; }

    public JsonElement? ErrorData { get; }

    public AcpRpcException(int code, string message, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }
}
