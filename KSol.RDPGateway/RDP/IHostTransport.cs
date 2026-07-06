using System.Net.Sockets;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Abstracts <i>how</i> the gateway obtains the raw duplex byte stream to an RDP host — directly over a
/// <see cref="TcpClient"/>, or tunnelled through a connector's WebSocket data channel. <see cref="RdpHostConnection"/>
/// layers X.224 / TLS / CredSSP over whatever stream this yields, so the two paths are otherwise identical.
/// </summary>
public interface IHostTransport
{
    /// <summary>Opens a raw byte stream to host:port. Throws on failure to connect.</summary>
    Task<Stream> ConnectAsync(string host, int port, CancellationToken ct);
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
}

/// <summary>Tunnels the host stream through a connector's WebSocket data channel.</summary>
public sealed class ConnectorTcpTransport : IHostTransport
{
    private readonly ConnectorHub _hub;
    private readonly string _connectorId;
    public ConnectorTcpTransport(ConnectorHub hub, string connectorId) { _hub = hub; _connectorId = connectorId; }

    public Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
        => _hub.OpenTcpAsync(_connectorId, host, port, ct);
}
