using System.Text.Json.Serialization;

namespace KSol.ZeroVDI.Connector;

/// <summary>
/// Wire protocol shared with the gateway's ConnectorHub. Kept byte-identical to the gateway's copy so the
/// two agree on control-frame shapes. Control frames are JSON text on the control WebSocket; TCP payload is
/// binary frames on per-request data WebSockets.
/// </summary>
public sealed class ConnectorMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("chanId")] public string? ChanId { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("rttMs")] public double? RttMs { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public static class ConnectorMessageType
{
    public const string OpenTcp = "open-tcp";
    public const string Probe = "probe";
    public const string Ack = "ack";
    public const string Ping = "ping";
}
