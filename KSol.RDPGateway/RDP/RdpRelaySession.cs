using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Bridges a browser WebSocket to a target RDP host for the in-browser RDP console.
///
/// The gateway is a thin relay for the RDP <i>PDU</i> stream: the shared <see cref="RdpHostConnection"/>
/// performs the X.224 <c>PROTOCOL_HYBRID</c> (NLA) negotiation, terminates TLS to the host, and runs the
/// CredSSP client handshake (<see cref="CredSspClient"/>) using the VM credentials. Once the host is
/// pre-authenticated, the decrypted RDP byte stream is relayed verbatim in both directions — the browser
/// JS owns all MCS/PDU/fastpath/bitmap/input logic.
///
/// Everything after TLS+CredSSP is opaque bytes to the relay.
/// </summary>
public sealed class RdpRelaySession
{
    private readonly WebSocket _ws;
    private readonly string _host;
    private readonly int _port;
    private readonly KerberosAuth? _kerberos;
    private readonly VmCredentials? _presuppliedCreds;
    private readonly IRdpMediaSink? _mediaSink;
    private readonly ILogger _logger;

    // Optional live structural decode + recording of the decrypted RDP stream (shared RdpWire engine).
    // Enabled when RDPGW_DUMP_DIR is set (diagnostics) or a media sink is supplied (session recording).
    // One recorder per session, fed by both pumps.
    private RdpStreamRecorder? _recorder;

    public RdpRelaySession(WebSocket ws, string host, int port, KerberosAuth? kerberos, ILogger logger,
        VmCredentials? presuppliedCreds = null, IRdpMediaSink? mediaSink = null)
    {
        _ws = ws;
        _host = host;
        _port = port;
        _kerberos = kerberos;
        _presuppliedCreds = presuppliedCreds;
        _mediaSink = mediaSink;
        _logger = logger;
    }

    /// <summary>
    /// VM credentials for the NLA/CredSSP handshake. Supplied either by the browser (first WS frame,
    /// manual login) or by the controller from the per-(user, resource) stored credentials (SSO).
    /// </summary>
    public sealed record VmCredentials(string user, string password, string? domain);

    public async Task RunAsync(CancellationToken ct)
    {
        // 1) Obtain VM credentials. With SSO (per-(user,resource) stored credentials), the controller
        // pre-supplies them and the browser sends NO credentials frame. Otherwise the first WS frame is
        // JSON VM credentials (over the already-HTTPS browser connection).
        VmCredentials creds;
        if (_presuppliedCreds != null)
        {
            creds = _presuppliedCreds;
        }
        else
        {
            try
            {
                creds = await ReadCredentialsAsync(ct);
            }
            catch (Exception ex)
            {
                await SendStatusAsync("error", "missing or invalid credentials", ct);
                _logger.LogWarning(ex, "RDP relay: failed to read credentials frame");
                return;
            }
        }

        await SendStatusAsync("connecting", null, ct);

        // 2-4) TCP connect, X.224 HYBRID negotiation, TLS termination, and the CredSSP (NLA) client
        // handshake are all done by the shared host-connect helper. It yields the decrypted RDP stream.
        RdpHostConnection.Connected host;
        try
        {
            host = await new RdpHostConnection(_host, _port, _kerberos, _logger).ConnectAsync(creds, ct: ct);
        }
        catch (RdpHostConnection.ConnectException ex)
        {
            await SendStatusAsync("error", ex.Status, ct);
            return;
        }
        using var _host_owned = host;
        var ssl = host.Stream;

        await SendStatusAsync("ready", null, ct);
        _logger.LogInformation("RDP relay: bridging {Host}:{Port}", _host, _port);

        // 5) Relay the decrypted RDP stream both ways until either side closes.
        // Optional: live decode + record both directions through the shared RdpWire engine.
        _recorder = RdpStreamRecorder.TryCreate(_logger, _mediaSink);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toWs = PumpSslToWsAsync(ssl, linked.Token);
        var toRdp = PumpWsToSslAsync(ssl, linked.Token);
        // Whichever pump finishes first ends the session. If the host→ws pump ended, the target closed
        // or died; tell the browser explicitly before closing so it doesn't sit on a frozen screen.
        var finished = await Task.WhenAny(toWs, toRdp);
        linked.Cancel();
        try { await Task.WhenAll(toWs, toRdp); } catch { /* shutdown races are expected */ }
        _recorder?.Dispose();

        if (finished == toWs)
        {
            _logger.LogWarning("RDP relay: target {Host}:{Port} ended the connection", _host, _port);
            await SendStatusAsync("error", "remote desktop disconnected", CancellationToken.None);
        }

        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None); }
            catch { /* ignore */ }
        }
    }

    private async Task<VmCredentials> ReadCredentialsAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("client closed before sending credentials");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(ms.ToArray());
        var creds = JsonSerializer.Deserialize<VmCredentials>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (creds == null || string.IsNullOrEmpty(creds.user))
            throw new InvalidOperationException("credentials missing 'user'");
        return creds;
    }

    // DEBUG: when RDPGW_DUMP_DIR is set, tee the decrypted host→browser stream to <dir>/our_s2c.bin so we
    // can diff our session's GFX frame flow against an mstsc/macOS-RD MITM capture. Remove after debugging.
    private static FileStream? OpenDump(string name)
    {
        var dir = Environment.GetEnvironmentVariable("RDPGW_DUMP_DIR");
        if (string.IsNullOrEmpty(dir)) return null;
        try { Directory.CreateDirectory(dir); return new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.Write, FileShare.Read); }
        catch { return null; }
    }

    // Host→browser relay. CRITICAL: the host paces its GFX stream on the gateway draining its TCP socket.
    // If we read-then-send in one loop, a slow browser WebSocket send back-pressures the host's TCP window
    // and the host PAUSES MID-FRAME (observed: it stops at a fixed ~11KB into a 22KB frame and never
    // resumes). A working mstsc receives 110KB frames in one uninterrupted burst — because its receiver
    // drains promptly. So we split: a READER that drains the host SSL into a bounded in-memory queue as
    // fast as the host sends, and a WRITER that forwards to the browser at the browser's pace. The host
    // never sees our WebSocket latency. The bound caps memory; if the browser falls hopelessly behind we
    // fail the session rather than buffer without limit.
    private async Task PumpSslToWsAsync(SslStream ssl, CancellationToken ct)
    {
        // Bounded so a stuck browser can't OOM us. ~256 chunks * 16KB ≈ 4MB max in flight.
        var pipe = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new System.Threading.Channels.BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });
        long total = 0, chunks = 0;

        // READER: drain host SSL promptly into the pipe. This is what keeps the host streaming.
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            using var dump = OpenDump("our_s2c.bin");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await ssl.ReadAsync(buffer, ct);
                    if (n == 0) { _logger.LogWarning("RDP relay: host→ws reader: host closed (0 bytes) after {Total} bytes / {Chunks} chunks", total, chunks); break; }
                    total += n;
                    chunks++;
                    var slice = buffer.AsMemory(0, n).ToArray();
                    if (dump != null) { await dump.WriteAsync(slice, ct); await dump.FlushAsync(ct); }
                    _recorder?.Feed(RdpDir.ServerToClient, slice);
                    await pipe.Writer.WriteAsync(slice, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "RDP relay: host→ws reader FAILED after {Total} bytes", total); }
            finally { pipe.Writer.TryComplete(); }
        }, ct);

        // WRITER: forward to the browser at the browser's pace (may block on a slow WS without affecting
        // the reader / the host).
        try
        {
            await foreach (var slice in pipe.Reader.ReadAllAsync(ct))
                await _ws.SendAsync(slice, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "RDP relay: host→ws writer FAILED"); }
        finally { try { await reader; } catch { } }
    }

    private async Task PumpWsToSslAsync(SslStream ssl, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var dump = OpenDump("our_c2s.bin");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count == 0) continue;
                // Browser sends raw RDP bytes as binary frames; write straight to the host. (A frame
                // may be partial; RDP framing is the browser's concern, so just forward bytes.)
                if (dump != null) { await dump.WriteAsync(buffer.AsMemory(0, result.Count), ct); await dump.FlushAsync(ct); }
                _recorder?.Feed(RdpDir.ClientToServer, buffer.AsSpan(0, result.Count));
                await ssl.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                await ssl.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "RDP relay: ws→host pump ended"); }
    }

    private async Task SendStatusAsync(string status, string? message, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(new { status, message });
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RDP relay: failed to send status {Status}", status);
        }
    }
}
