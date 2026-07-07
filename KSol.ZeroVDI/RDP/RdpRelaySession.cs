using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace KSol.ZeroVDI.RDP;

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
    private readonly byte[]? _routingToken;
    private readonly VmCredentials? _redirectCreds;
    private readonly Action<RdpServerRedirection>? _onRedirect;
    private readonly IHostTransport? _hostTransport;
    private readonly ILogger _logger;
    private bool _redirected;
    // True once a token-bearing session that the HOST disconnected (GNOME "Remote Login" post-auth
    // handover: DEACTIVATE_ALL + MCS Disconnect Ultimatum, NOT a redirect PDU) has been re-armed for a
    // token-preserving reconnect, so RunAsync signals the browser to reconnect instead of erroring.
    private bool _handoverContinue;

    // Optional live structural decode + recording of the decrypted RDP stream (shared RdpWire engine).
    // Enabled when RDPGW_DUMP_DIR is set (diagnostics) or a media sink is supplied (session recording).
    // One recorder per session, fed by both pumps.
    private RdpStreamRecorder? _recorder;

    public RdpRelaySession(WebSocket ws, string host, int port, KerberosAuth? kerberos, ILogger logger,
        VmCredentials? presuppliedCreds = null, IRdpMediaSink? mediaSink = null,
        byte[]? routingToken = null, VmCredentials? redirectCreds = null,
        Action<RdpServerRedirection>? onRedirect = null, IHostTransport? hostTransport = null)
    {
        _ws = ws;
        _hostTransport = hostTransport;
        _host = host;
        _port = port;
        _kerberos = kerberos;
        _presuppliedCreds = presuppliedCreds;
        _mediaSink = mediaSink;
        _routingToken = routingToken;
        _redirectCreds = redirectCreds;
        _onRedirect = onRedirect;
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
        if (_redirectCreds != null)
        {
            // Reconnect after a Server Redirection: the broker handed back one-time session credentials
            // (e.g. GNOME Remote Desktop "Remote Login"). Authenticate the redirected session with those,
            // NOT the original SSO/login credentials — the redirected target only accepts the one-time pair.
            creds = _redirectCreds;
            // The browser may still send a credentials frame on reconnect (non-SSO); drain+ignore it.
            if (_presuppliedCreds == null) { try { await ReadCredentialsAsync(ct); } catch { /* ignore */ } }
        }
        else if (_presuppliedCreds != null)
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
            host = await new RdpHostConnection(_host, _port, _kerberos, _logger, _hostTransport)
                .ConnectAsync(creds, ct: ct, routingToken: _routingToken);
            if (_routingToken != null)
                _logger.LogInformation("RDP relay: reconnected with redirection routing token ({Len}B)", _routingToken.Length);
        }
        catch (RdpHostConnection.ConnectException ex)
        {
            _logger.LogWarning("RDP relay: host connect failed (status '{Status}', routingToken={HasToken})",
                ex.Status, _routingToken != null);
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

        // Server Redirection: the host→ws pump broke because it detected a redirect. Send the browser the
        // "redirect" control frame NOW, while the WebSocket is still Open — it is full-duplex, so sending
        // alongside the still-pending ws→host receive is safe. We must do this BEFORE linked.Cancel(),
        // because cancelling the ws→host ReceiveAsync ABORTS the WebSocket (then the frame can't be sent).
        if (_redirected)
        {
            _logger.LogInformation("RDP relay: signalling browser to reconnect after redirection (ws state {State})", _ws.State);
            if (_ws.State == WebSocketState.Open)
            {
                var json = JsonSerializer.Serialize(new { status = "redirect", message = (string?)null });
                try
                {
                    await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
                    _logger.LogInformation("RDP relay: redirect frame sent");
                }
                catch (Exception ex) { _logger.LogWarning(ex, "RDP relay: failed to send redirect frame"); }
            }
            else
            {
                _logger.LogWarning("RDP relay: cannot send redirect frame, ws not open ({State})", _ws.State);
            }
        }

        // GNOME "Remote Login" post-auth handover: the host disconnects the token-bearing greeter session
        // (DEACTIVATE_ALL + MCS Disconnect Ultimatum) WITHOUT sending a redirect PDU, expecting the client
        // to reconnect with the SAME routing token to land on the handed-over (logged-in) session — this
        // is what the Windows client does. So if THIS session used a routing token and the host ended it
        // (not our own redirect), re-arm the same token + creds and signal the browser to reconnect.
        if (!_redirected && finished == toWs && _routingToken != null && _onRedirect != null)
        {
            _handoverContinue = true;
            _onRedirect(RdpServerRedirection.FromToken(_routingToken, _redirectCreds?.user, _redirectCreds?.domain, _redirectCreds?.password));
            _logger.LogInformation("RDP relay: host ended token-bearing session -> re-arming handover reconnect (token {Len}B)", _routingToken.Length);
            if (_ws.State == WebSocketState.Open)
            {
                var json = JsonSerializer.Serialize(new { status = "redirect", message = (string?)null });
                try { await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "RDP relay: failed to send handover reconnect frame"); }
            }
        }

        linked.Cancel();
        try { await Task.WhenAll(toWs, toRdp); } catch { /* shutdown races are expected */ }
        _recorder?.Dispose();

        if (!_redirected && !_handoverContinue && finished == toWs)
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

        // Server-redirection scan: the redirection PDU (~3.5KB) can straddle two ssl.ReadAsync reads, so
        // scanning each slice in isolation misses it. Accumulate decrypted bytes into a small rolling
        // buffer and scan that. GNOME Remote Desktop "Remote Login" redirects TWICE: once up front
        // (client → greeter) and again AFTER the user authenticates (greeter → user session). The second
        // redirect arrives after the greeter has already streamed many MB of graphics, so we must keep
        // watching for the whole session — but with a BOUNDED rolling tail (a redirect PDU is tiny and
        // self-contained in one TPKT), so steady-state graphics never balloon memory.
        var scanBuf = (_onRedirect != null) ? new MemoryStream() : null;
        const int RedirectScanTail = 64 * 1024; // keep only the last 64 KB to catch a redirect mid-stream

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

                    // Server Redirection: a session broker (GNOME Remote Desktop "Remote Login") sends a
                    // redirection PDU then cancels. Detect it (across read boundaries via scanBuf), stash
                    // the routing token, ask the browser to reconnect — and do NOT forward the redirect PDU
                    // to the browser (it would choke on it).
                    if (scanBuf != null && !_redirected)
                    {
                        scanBuf.Write(slice, 0, slice.Length);
                        var redir = RdpServerRedirection.TryParse(scanBuf.GetBuffer().AsSpan(0, (int)scanBuf.Length));
                        if (redir?.LoadBalanceInfo != null)
                        {
                            _redirected = true;
                            _logger.LogInformation("RDP relay: server redirection -> reconnecting (token {Len}B, target {Target}, creds={HasCreds})",
                                redir.LoadBalanceInfo.Length, redir.TargetHost ?? _host, redir.Username != null && redir.Password != null);
                            _onRedirect!(redir);
                            // Stop relaying; RunAsync sends the browser the "redirect" control frame once the
                            // pumps have stopped (avoids concurrent WebSocket sends with the writer pump).
                            break;
                        }
                        // Keep watching the WHOLE session (GNOME redirects again after login) but bound the
                        // buffer: retain only the last RedirectScanTail bytes so steady-state graphics don't
                        // balloon memory. A redirect PDU is small and self-contained, so the tail always
                        // holds a complete one even if it straddled reads.
                        if (scanBuf.Length > RedirectScanTail)
                        {
                            var buf = scanBuf.GetBuffer();
                            int len = (int)scanBuf.Length;
                            int keep = RedirectScanTail / 2;
                            Buffer.BlockCopy(buf, len - keep, buf, 0, keep);
                            scanBuf.SetLength(keep);
                            scanBuf.Position = keep;
                        }
                    }

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
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // Connection-quality ping: the browser measures RTT by round-tripping a small JSON
                    // frame carrying its own client-side timestamp. We just echo it back verbatim as
                    // "pong" — the RDP byte stream never sees this, so it can't desync framing/decoders.
                    await HandlePingAsync(buffer.AsMemory(0, result.Count), ct);
                    continue;
                }
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

    // {"type":"ping","t":<client timestamp>} -> {"type":"pong","t":<same timestamp>}. Purely a round-trip
    // echo so the browser can compute RTT = now() - t; the gateway does not interpret or store 't'.
    private async Task HandlePingAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame);
            if (!doc.RootElement.TryGetProperty("type", out var t) || t.GetString() != "ping") return;
            if (!doc.RootElement.TryGetProperty("t", out var tsEl)) return;
            var json = JsonSerializer.Serialize(new { type = "pong", t = tsEl.GetDouble() });
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
        }
        catch (JsonException) { /* not a ping frame; ignore */ }
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
