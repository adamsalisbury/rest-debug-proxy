using System.Text.Json;
using System.Text.Json.Serialization;

namespace RestDebugProxy;

public sealed class CannedResponse
{
    public string Method { get; init; } = string.Empty;

    public string Route { get; init; } = string.Empty;

    public CannedResponseBody Response { get; init; } = new();
}

public sealed class CannedResponseBody
{
    public int Delay { get; init; }

    public CannedPayload Payload { get; init; } = new();
}

public sealed class CannedPayload
{
    public JsonElement Body { get; init; }

    [JsonPropertyName("respCode")]
    public int RespCode { get; init; } = 200;

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
