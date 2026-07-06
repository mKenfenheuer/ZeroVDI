using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Runtime registry and message pump for connected connector agents. Each enrolled agent holds one
/// <i>control</i> WebSocket (accepted at <c>/agent/control</c>) registered here; the gateway sends it
/// <c>open-tcp</c> / <c>probe</c> requests and awaits correlated <c>ack</c> replies. For an <c>open-tcp</c>
/// the agent dials out a fresh <i>data</i> WebSocket (<c>/agent/data/{chanId}</c>) which the hub matches
/// back to the pending request and surfaces as a duplex <see cref="Stream"/> (see <see cref="WebSocketStream"/>).
///
/// This is the transport backend for <see cref="ConnectorPathSelector"/> (RTT probing + connector TCP) and
/// for connector-proxied Proxmox HTTP.
/// </summary>
public sealed class ConnectorHub
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ConnectorHub> _logger;
    private readonly ConcurrentDictionary<string, ConnectorConnection> _connections = new();

    public ConnectorHub(ILogger<ConnectorHub> logger) => _logger = logger;

    /// <summary>True if the given connector currently has a live control channel.</summary>
    public bool IsOnline(string connectorId) => _connections.ContainsKey(connectorId);

    /// <summary>Snapshot of currently-online connector ids (for the admin status view).</summary>
    public IReadOnlySet<string> OnlineIds => _connections.Keys.ToHashSet();

    // ── Control channel lifecycle ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the receive loop for a newly-accepted control WebSocket until it closes. One connection per
    /// connector; a reconnect replaces the previous. Returns when the socket ends.
    /// </summary>
    public async Task RunControlChannelAsync(string connectorId, WebSocket socket, CancellationToken ct)
    {
        var conn = new ConnectorConnection(socket);
        // Replace any stale connection for this connector (agent reconnected).
        if (_connections.TryGetValue(connectorId, out var old))
            old.Fault(new IOException("replaced by a new control channel"));
        _connections[connectorId] = conn;
        _logger.LogInformation("Connector {Id}: control channel online", connectorId);

        var buffer = new byte[16 * 1024];
        var frame = new List<byte>();
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                frame.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    frame.AddRange(buffer.AsSpan(0, result.Count).ToArray());
                } while (!result.EndOfMessage);

                if (frame.Count == 0) continue;
                ConnectorMessage? msg;
                try { msg = JsonSerializer.Deserialize<ConnectorMessage>(frame.ToArray(), Json); }
                catch (Exception ex) { _logger.LogWarning(ex, "Connector {Id}: bad control frame", connectorId); continue; }
                if (msg != null) HandleControlMessage(connectorId, conn, msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _logger.LogInformation("Connector {Id}: control channel closed ({Msg})", connectorId, ex.Message); }
        finally
        {
            _connections.TryRemove(new KeyValuePair<string, ConnectorConnection>(connectorId, conn));
            conn.Fault(new IOException("control channel closed"));
            _logger.LogInformation("Connector {Id}: control channel offline", connectorId);
        }
    }

    private void HandleControlMessage(string connectorId, ConnectorConnection conn, ConnectorMessage msg)
    {
        switch (msg.Type)
        {
            case ConnectorMessageType.Ack when msg.Id != null:
                conn.CompleteRequest(msg);
                break;
            case ConnectorMessageType.Ping:
                break; // heartbeat; LastSeen is refreshed by the endpoint layer
            default:
                _logger.LogDebug("Connector {Id}: unhandled control message '{Type}'", connectorId, msg.Type);
                break;
        }
    }

    /// <summary>
    /// Fulfils a pending data-channel request when the agent's data WebSocket arrives. On success returns
    /// the stream's completion task so the accepting HTTP request can stay alive until the relay is done
    /// (returns null when there is no matching pending request).
    /// </summary>
    public Task? TryAttachDataChannel(string connectorId, string chanId, WebSocket socket)
    {
        if (_connections.TryGetValue(connectorId, out var conn))
        {
            var stream = new WebSocketStream(socket);
            if (conn.TryAttachData(chanId, stream))
                return stream.Completion;
        }
        return null;
    }

    // ── Requests to the agent ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the connector to open a TCP connection to host:port and returns a duplex stream over the data
    /// channel it opens back. Throws if the connector is offline or the dial fails.
    /// </summary>
    public async Task<Stream> OpenTcpAsync(string connectorId, string host, int port, CancellationToken ct)
    {
        if (!_connections.TryGetValue(connectorId, out var conn))
            throw new IOException($"connector {connectorId} is offline");

        var chanId = Guid.NewGuid().ToString("N");
        var dataReady = conn.RegisterDataChannel(chanId);
        var req = new ConnectorMessage
        {
            Type = ConnectorMessageType.OpenTcp, Id = Guid.NewGuid().ToString("N"),
            Host = host, Port = port, ChanId = chanId,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var response = await conn.SendRequestAsync(req, this, timeout.Token);
            if (!response.Ok)
            {
                conn.CancelDataChannel(chanId);
                throw new IOException(response.Error ?? "connector failed to open TCP");
            }
            return await dataReady.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            conn.CancelDataChannel(chanId);
            throw new IOException($"connector {connectorId} open-tcp to {host}:{port} timed out");
        }
    }

    /// <summary>
    /// Asks the connector to TCP-probe host:port and returns the round-trip time, or null if unreachable /
    /// offline. Never throws for an ordinary unreachable result.
    /// </summary>
    public async Task<TimeSpan?> ProbeAsync(string connectorId, string host, int port, CancellationToken ct)
    {
        if (!_connections.TryGetValue(connectorId, out var conn))
            return null;

        var req = new ConnectorMessage
        {
            Type = ConnectorMessageType.Probe, Id = Guid.NewGuid().ToString("N"),
            Host = host, Port = port,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var response = await conn.SendRequestAsync(req, this, timeout.Token);
            if (response.Ok && response.RttMs is { } ms) return TimeSpan.FromMilliseconds(ms);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (IOException) { return null; }
    }

    internal async Task SendControlAsync(WebSocket socket, ConnectorMessage msg, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    // ── Per-connector connection state ───────────────────────────────────────────────────────────

    private sealed class ConnectorConnection
    {
        private readonly WebSocket _control;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ConnectorMessage>> _pending = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocketStream>> _dataChannels = new();

        public ConnectorConnection(WebSocket control) => _control = control;

        public async Task<ConnectorMessage> SendRequestAsync(ConnectorMessage req, ConnectorHub hub, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ConnectorMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[req.Id!] = tcs;
            await _sendLock.WaitAsync(ct);
            try { await hub.SendControlAsync(_control, req, ct); }
            finally { _sendLock.Release(); }
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
                return await tcs.Task;
        }

        public void CompleteRequest(ConnectorMessage ack)
        {
            if (_pending.TryRemove(ack.Id!, out var tcs)) tcs.TrySetResult(ack);
        }

        public TaskCompletionSource<WebSocketStream> RegisterDataChannel(string chanId)
        {
            var tcs = new TaskCompletionSource<WebSocketStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dataChannels[chanId] = tcs;
            return tcs;
        }

        public bool TryAttachData(string chanId, WebSocketStream stream)
            => _dataChannels.TryRemove(chanId, out var tcs) && tcs.TrySetResult(stream);

        public void CancelDataChannel(string chanId)
        {
            if (_dataChannels.TryRemove(chanId, out var tcs)) tcs.TrySetCanceled();
        }

        public void Fault(Exception ex)
        {
            foreach (var kv in _pending) kv.Value.TrySetException(ex);
            foreach (var kv in _dataChannels) kv.Value.TrySetException(ex);
            _pending.Clear();
            _dataChannels.Clear();
        }
    }
}
