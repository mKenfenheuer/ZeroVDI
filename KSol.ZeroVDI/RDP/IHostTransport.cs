using System.Net.Sockets;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Abstracts <i>how</i> the gateway obtains the raw duplex byte stream to an RDP host — directly over a
/// <see cref="TcpClient"/>, or tunnelled through a connector's WebSocket data channel. <see cref="RdpHostConnection"/>
/// layers X.224 / TLS / CredSSP over whatever stream this yields, so the two paths are otherwise identical.
/// </summary>
public interface IHostTransport
{
    /// <summary>Opens a raw byte stream to host:port. Throws on failure to connect.</summary>
    Task<Stream> ConnectAsync(string host, int port, CancellationToken ct);

    /// <summary>
    /// Measures the current round-trip time to host:port over this transport's path (a timed TCP
    /// connect — RDP has no client-initiated in-band RTT probe, so a throwaway connect is the only
    /// portable measure). Returns null when the host is unreachable or the probe timed out. Used by
    /// the relay's connection-quality sampler to report the gateway→host leg to the browser.
    /// </summary>
    Task<TimeSpan?> ProbeRttAsync(string host, int port, CancellationToken ct);
}

/// <summary>Direct TCP transport (today's behaviour): a <see cref="TcpClient"/> with keepalive tuning.</summary>
public sealed class DirectTcpTransport : IHostTransport
{
    private readonly ILogger _logger;
    public DirectTcpTransport(ILogger logger) => _logger = logger;

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            // RDP is interactive: disable Nagle so small packets go out immediately instead of being held
            // ~40ms to coalesce (matches the connector path's tuning).
            tcp.NoDelay = true;
            // Detect a silently dead/half-open target so the relay doesn't hang forever.
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            try
            {
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "RDP host: TCP keepalive tuning unavailable"); }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        // NetworkStream owns the socket so disposing the stream tears down the TcpClient.
        return new NetworkStream(tcp.Client, ownsSocket: true);
    }

    public async Task<TimeSpan?> ProbeRttAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await tcp.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return tcp.Connected ? sw.Elapsed : null;
        }
        catch { return null; }
    }
}

/// <summary>Tunnels the host stream through a connector's WebSocket data channel.</summary>
public sealed class ConnectorTcpTransport : IHostTransport
{
    private readonly ConnectorHub _hub;
    private readonly string _connectorId;
    public ConnectorTcpTransport(ConnectorHub hub, string connectorId) { _hub = hub; _connectorId = connectorId; }

    public Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
        => _hub.OpenTcpAsync(_connectorId, host, port, ct);

    // The hub's Probe reply carries only the connector's LOCAL connect time (connector→host), which
    // misses the gateway→connector hop this transport actually traverses. Timing the whole probe
    // round trip (control-channel WS + connector's connect) covers the full gateway→host leg.
    public async Task<TimeSpan?> ProbeRttAsync(string host, int port, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rtt = await _hub.ProbeAsync(_connectorId, host, port, ct);
        sw.Stop();
        return rtt != null ? sw.Elapsed : null;
    }
}
