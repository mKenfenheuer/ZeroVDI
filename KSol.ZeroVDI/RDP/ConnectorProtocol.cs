using System.Text.Json.Serialization;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Wire protocol shared by the gateway's <see cref="ConnectorHub"/> and the connector agent. All control
/// messages are single JSON text frames on the control WebSocket; TCP payload flows as binary frames on
/// separate per-request data WebSockets. Kept deliberately tiny and dependency-free so the agent project
/// can carry an identical copy.
///
/// Message flow:
///  - Gateway → agent: <c>open-tcp</c> (asks the agent to dial host:port and open a data channel tagged
///    <c>chanId</c>) and <c>probe</c> (asks for a TCP-connect RTT).
///  - Agent → gateway: <c>ack</c> (correlated by <c>id</c>, reports success/failure of an open-tcp/probe),
///    <c>ping</c> (heartbeat).
/// </summary>
public sealed class ConnectorMessage
{
    /// <summary>Message kind: open-tcp | probe | ack | ping.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    /// <summary>Correlation id for request/response pairing. Set on requests, echoed on the matching ack.</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }

    /// <summary>Data-channel id the agent must open (<c>/agent/data/{chanId}</c>) for an open-tcp request.</summary>
    [JsonPropertyName("chanId")] public string? ChanId { get; set; }

    /// <summary>On an ack: whether the requested action succeeded.</summary>
    [JsonPropertyName("ok")] public bool Ok { get; set; }

    /// <summary>On a probe ack: round-trip time in milliseconds when reachable.</summary>
    [JsonPropertyName("rttMs")] public double? RttMs { get; set; }

    /// <summary>On a failed ack: human-readable reason.</summary>
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Message type constants (avoid stringly-typed drift between hub and agent).</summary>
public static class ConnectorMessageType
{
    public const string OpenTcp = "open-tcp";
    public const string Probe = "probe";
    public const string Ack = "ack";
    public const string Ping = "ping";
}
