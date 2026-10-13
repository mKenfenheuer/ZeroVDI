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
    /// portable measure; it goes through <see cref="RdpPortProbe"/>, never a bare connect-and-close).
    /// Returns null when the host is unreachable or the probe timed out. Used by the relay's
    /// connection-quality sampler to report the gateway→host leg to the browser.
    /// </summary>
    Task<TimeSpan?> ProbeRttAsync(string host, int port, CancellationToken ct);

    /// <summary>
    /// The kernel's round-trip estimate for the most recent stream this transport opened, read passively
    /// from the live socket (no extra connection), or null when the transport can't provide one. The
    /// relay prefers this over <see cref="ProbeRttAsync"/>: a probe is a real connection attempt that
    /// RDP hosts log and some firewalls rate-limit.
    /// </summary>
    TimeSpan? LiveRtt => null;
}

/// <summary>
/// Reads TCP's own RTT estimate from a connected socket: <c>TCP_INFO</c> on Linux (the container) and
/// <c>SIO_TCP_INFO</c> on Windows 10 1703+. Reports the connection's minimum observed RTT — the path's
/// base latency, the same thing a timed connect measures — because the smoothed RTT on an RDP socket is
/// inflated by delayed ACKs whenever the desktop is idle and would read as a slow link.
/// </summary>
internal static class TcpRttReader
{
    private const int IPPROTO_TCP = 6;
    private const int TCP_INFO = 11;              // Linux getsockopt
    private const int SIO_TCP_INFO = unchecked((int)0xD8000027); // Windows WSAIoctl

    public static TimeSpan? Read(Socket? socket)
    {
        if (socket == null || !socket.Connected) return null;
        try
        {
            uint us = 0;
            if (OperatingSystem.IsLinux())
            {
                // struct tcp_info: tcpi_rtt (smoothed, µs) at 68; tcpi_min_rtt (µs) at 148 on kernels ≥ 4.6.
                Span<byte> info = stackalloc byte[232];
                int len = socket.GetRawSocketOption(IPPROTO_TCP, TCP_INFO, info);
                if (len >= 152) us = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(148, 4));
                if (us == 0 && len >= 72) us = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(68, 4));
            }
            else if (OperatingSystem.IsWindows())
            {
                // TCP_INFO_v0: RttUs at 20, MinRttUs at 24. Input is the requested struct version (0).
                var output = new byte[128];
                socket.IOControl(SIO_TCP_INFO, new byte[4], output);
                us = BitConverter.ToUInt32(output, 24);
                if (us == 0) us = BitConverter.ToUInt32(output, 20);
            }
            // 0 or the kernel's "no sample yet" sentinel (all ones) mean unknown.
            return us is 0 or uint.MaxValue ? null : TimeSpan.FromTicks(us * 10L);
        }
        catch { return null; }
    }
}

/// <summary>Direct TCP transport (today's behaviour): a <see cref="TcpClient"/> with keepalive tuning.</summary>
public sealed class DirectTcpTransport : IHostTransport
{
    private readonly ILogger _logger;
    private Socket? _socket; // the latest stream's socket, for LiveRtt
    public DirectTcpTransport(ILogger logger) => _logger = logger;

    public TimeSpan? LiveRtt => TcpRttReader.Read(_socket);

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            _socket = tcp.Client;
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

    public Task<TimeSpan?> ProbeRttAsync(string host, int port, CancellationToken ct)
        => RdpPortProbe.ProbeAsync(host, port, TimeSpan.FromSeconds(2), ct);
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
