using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace KSol.ZeroVDI.Connector;

/// <summary>
/// The connector agent. Registers with the gateway (one-time token → long-lived auth token), then holds an
/// outbound control WebSocket open and, on request, dials TCP targets and pumps them over per-request data
/// WebSockets. Also answers reachability/RTT probes. Reconnects with backoff.
/// </summary>
public sealed class Agent
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AgentConfig _config;

    public Agent(AgentConfig config) => _config = config;

    /// <summary>POST /agent/register — exchange a registration token for a long-lived auth token.</summary>
    public static async Task<AgentConfig> RegisterAsync(string gatewayUrl, string registrationToken)
    {
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var u) || u.Scheme != "https")
            throw new InvalidOperationException("the gateway URL must be https:// — connectors use TLS (wss://) only");
        using var http = new HttpClient { BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/") };
        var resp = await http.PostAsJsonAsync("agent/register", new { registrationToken });
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"registration failed ({(int)resp.StatusCode}): {body}");
        }
        var result = await resp.Content.ReadFromJsonAsync<RegisterResponse>()
            ?? throw new InvalidOperationException("empty registration response");
        var cfg = new AgentConfig { GatewayUrl = gatewayUrl.TrimEnd('/'), AuthToken = result.AuthToken, ConnectorId = result.ConnectorId };
        cfg.Save();
        return cfg;
    }

    private sealed record RegisterResponse(string ConnectorId, string AuthToken);

    /// <summary>Runs the control loop forever, reconnecting with backoff.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndServeAsync(ct);
                backoff = TimeSpan.FromSeconds(1); // clean exit resets backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[control] disconnected: {ex.Message}; retrying in {backoff.TotalSeconds:0}s");
                try { await Task.Delay(backoff, ct); } catch { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private Uri WsUri(string path)
    {
        var b = new UriBuilder(_config.GatewayUrl.TrimEnd('/') + path);
        // Enforce TLS: connectors carry the auth token and relay RDP/API traffic, so the transport must be
        // encrypted end-to-end. Only wss:// (from an https:// gateway URL) is allowed.
        if (b.Scheme != "https")
            throw new InvalidOperationException($"insecure gateway URL '{_config.GatewayUrl}': an https:// URL is required (connections use wss:// only)");
        b.Scheme = "wss";
        // Also carry the token in the query: some reverse proxies strip the Authorization header on the
        // WebSocket upgrade. The gateway accepts either.
        var tokenParam = "token=" + Uri.EscapeDataString(_config.AuthToken);
        b.Query = string.IsNullOrEmpty(b.Query) ? tokenParam : b.Query.TrimStart('?') + "&" + tokenParam;
        return b.Uri;
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        using var control = new ClientWebSocket();
        control.Options.SetRequestHeader("Authorization", "Bearer " + _config.AuthToken);
        control.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await control.ConnectAsync(WsUri("/agent/control"), ct);
        Console.WriteLine($"[control] connected to {_config.GatewayUrl}");

        var sendLock = new SemaphoreSlim(1, 1);
        _ = HeartbeatAsync(control, sendLock, ct);

        var buffer = new byte[16 * 1024];
        var frame = new List<byte>();
        while (control.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            frame.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await control.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                frame.AddRange(buffer.AsSpan(0, result.Count).ToArray());
            } while (!result.EndOfMessage);

            if (frame.Count == 0) continue;
            ConnectorMessage? msg;
            try { msg = JsonSerializer.Deserialize<ConnectorMessage>(frame.ToArray(), Json); }
            catch { continue; }
            if (msg == null) continue;

            // Handle each request off the receive loop so one slow dial doesn't stall others.
            _ = HandleRequestAsync(control, sendLock, msg, ct);
        }
    }

    private async Task HeartbeatAsync(ClientWebSocket control, SemaphoreSlim sendLock, CancellationToken ct)
    {
        try
        {
            while (control.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                await SendAsync(control, sendLock, new ConnectorMessage { Type = ConnectorMessageType.Ping }, ct);
            }
        }
        catch { /* loop ends when the socket closes */ }
    }

    private async Task HandleRequestAsync(ClientWebSocket control, SemaphoreSlim sendLock, ConnectorMessage msg, CancellationToken ct)
    {
        switch (msg.Type)
        {
            case ConnectorMessageType.Probe:
                await HandleProbeAsync(control, sendLock, msg, ct);
                break;
            case ConnectorMessageType.OpenTcp:
                await HandleOpenTcpAsync(control, sendLock, msg, ct);
                break;
        }
    }

    private async Task HandleProbeAsync(ClientWebSocket control, SemaphoreSlim sendLock, ConnectorMessage msg, CancellationToken ct)
    {
        var ack = new ConnectorMessage { Type = ConnectorMessageType.Ack, Id = msg.Id };
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var sw = Stopwatch.StartNew();
            await tcp.ConnectAsync(msg.Host!, msg.Port, cts.Token);
            sw.Stop();
            ack.Ok = tcp.Connected;
            ack.RttMs = sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            ack.Ok = false;
        }
        await SendAsync(control, sendLock, ack, ct);
    }

    private async Task HandleOpenTcpAsync(ClientWebSocket control, SemaphoreSlim sendLock, ConnectorMessage msg, CancellationToken ct)
    {
        TcpClient tcp;
        try
        {
            tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(msg.Host!, msg.Port, cts.Token);
        }
        catch (Exception ex)
        {
            await SendAsync(control, sendLock,
                new ConnectorMessage { Type = ConnectorMessageType.Ack, Id = msg.Id, Ok = false, Error = ex.Message }, ct);
            return;
        }

        // Ack success first so the gateway starts awaiting the data channel, then dial the data WebSocket.
        await SendAsync(control, sendLock,
            new ConnectorMessage { Type = ConnectorMessageType.Ack, Id = msg.Id, Ok = true }, ct);

        ClientWebSocket data;
        try
        {
            data = new ClientWebSocket();
            data.Options.SetRequestHeader("Authorization", "Bearer " + _config.AuthToken);
            await data.ConnectAsync(WsUri("/agent/data/" + msg.ChanId), ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[data] failed to open channel {msg.ChanId}: {ex.Message}");
            tcp.Dispose();
            return;
        }

        Console.WriteLine($"[data] tunnelling {msg.Host}:{msg.Port} (chan {msg.ChanId})");
        await PumpAsync(tcp, data, ct);
    }

    /// <summary>Bidirectionally relays between the TCP socket and the data WebSocket until either closes.</summary>
    private static async Task PumpAsync(TcpClient tcp, ClientWebSocket ws, CancellationToken ct)
    {
        using (tcp)
        using (ws)
        {
            var stream = tcp.GetStream();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tcpToWs = TcpToWsAsync(stream, ws, linked.Token);
            var wsToTcp = WsToTcpAsync(ws, stream, linked.Token);
            await Task.WhenAny(tcpToWs, wsToTcp);
            linked.Cancel();
            try { await Task.WhenAll(tcpToWs, wsToTcp); } catch { }
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch { }
        }
    }

    private static async Task TcpToWsAsync(NetworkStream tcp, ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await tcp.ReadAsync(buf, ct)) > 0)
                await ws.SendAsync(buf.AsMemory(0, n), WebSocketMessageType.Binary, true, ct);
        }
        catch { }
    }

    private static async Task WsToTcpAsync(ClientWebSocket ws, NetworkStream tcp, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0)
                    await tcp.WriteAsync(buf.AsMemory(0, result.Count), ct);
            }
        }
        catch { }
    }

    private async Task SendAsync(ClientWebSocket ws, SemaphoreSlim sendLock, ConnectorMessage msg, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
        await sendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { sendLock.Release(); }
    }
}
