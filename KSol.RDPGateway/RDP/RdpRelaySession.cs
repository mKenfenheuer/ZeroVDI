using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Bridges a browser WebSocket to a target RDP host for the in-browser RDP console.
///
/// The gateway is a thin relay for the RDP <i>PDU</i> stream: it performs the X.224
/// <c>PROTOCOL_HYBRID</c> (NLA) negotiation, terminates TLS to the host, and runs the CredSSP client
/// handshake (<see cref="CredSspClient"/>) using the VM credentials the browser supplied. Once the
/// host is pre-authenticated, the decrypted RDP byte stream is relayed verbatim in both directions —
/// the browser JS owns all MCS/PDU/fastpath/bitmap/input logic.
///
/// Protocol framing the relay itself understands is limited to the X.224 connection request/confirm;
/// everything after TLS+CredSSP is opaque bytes.
/// </summary>
public sealed class RdpRelaySession
{
    // X.224 RDP Negotiation request/response protocol flags ([MS-RDPBCGR] 2.2.1.1.1 / 2.2.1.2.1).
    private const uint PROTOCOL_SSL = 0x00000001;
    private const uint PROTOCOL_HYBRID = 0x00000002;
    private const byte TYPE_RDP_NEG_REQ = 0x01;
    private const byte TYPE_RDP_NEG_RSP = 0x02;
    private const byte TYPE_RDP_NEG_FAILURE = 0x03;

    private readonly WebSocket _ws;
    private readonly string _host;
    private readonly int _port;
    private readonly KerberosAuth? _kerberos;
    private readonly ILogger _logger;

    // Optional live structural decode + recording of the decrypted RDP stream (shared RdpWire engine).
    // Enabled when RDPGW_DUMP_DIR is set. One recorder per session, fed by both pumps.
    private RdpRecorder? _recorder;

    public RdpRelaySession(WebSocket ws, string host, int port, KerberosAuth? kerberos, ILogger logger)
    {
        _ws = ws;
        _host = host;
        _port = port;
        _kerberos = kerberos;
        _logger = logger;
    }

    private sealed record VmCredentials(string user, string password, string? domain);

    public async Task RunAsync(CancellationToken ct)
    {
        // 1) First WS frame = JSON VM credentials (over the already-HTTPS browser connection).
        VmCredentials creds;
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

        await SendStatusAsync("connecting", null, ct);

        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_host, _port, ct);
            // Detect a silently dead/half-open target (host crashed, network dropped) so the relay
            // doesn't hang forever feeding a frozen browser. Keepalive probes surface a dead peer as a
            // read error on the SSL pump, which tears down both pumps and closes the WebSocket.
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            try
            {
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "RDP relay: TCP keepalive tuning unavailable"); }
        }
        catch (Exception ex)
        {
            await SendStatusAsync("error", $"cannot reach {_host}:{_port}", ct);
            _logger.LogWarning(ex, "RDP relay: TCP connect to {Host}:{Port} failed", _host, _port);
            return;
        }

        var netStream = tcp.GetStream();

        // 2) X.224 negotiation requesting NLA (HYBRID).
        uint selected;
        try
        {
            selected = await NegotiateX224Async(netStream, ct);
        }
        catch (Exception ex)
        {
            await SendStatusAsync("error", "RDP negotiation failed", ct);
            _logger.LogWarning(ex, "RDP relay: X.224 negotiation failed");
            return;
        }

        // 3) TLS terminate. Accept the host cert (self-signed VMs are normal; CredSSP's public-key
        // binding still detects MITM on the inner auth).
        X509Certificate2? serverCert = null;
        var ssl = new SslStream(netStream, leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, cert, _, _) =>
            {
                if (cert != null) serverCert = new X509Certificate2(cert);
                return true;
            });
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _host,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                    | System.Security.Authentication.SslProtocols.Tls13,
            }, ct);
        }
        catch (Exception ex)
        {
            await SendStatusAsync("error", "TLS handshake failed", ct);
            _logger.LogWarning(ex, "RDP relay: TLS handshake failed");
            return;
        }

        // 4) CredSSP (NLA). Only when the host selected HYBRID; if it (unusually) selected plain SSL,
        // there is no NLA step and we relay straight away.
        if ((selected & PROTOCOL_HYBRID) != 0)
        {
            if (serverCert == null)
            {
                await SendStatusAsync("error", "no server certificate for NLA", ct);
                return;
            }
            // CredSSP binds the *raw* public key, i.e. the SubjectPublicKey BIT STRING contents (for
            // RSA, the DER RSAPublicKey) — the same bytes OpenSSL's i2d_PublicKey produces and what
            // FreeRDP/Windows hash. This is NOT the full SubjectPublicKeyInfo. EncodedKeyValue.RawData
            // is exactly that inner key.
            var publicKey = serverCert.PublicKey.EncodedKeyValue.RawData;
            var credssp = new CredSspClient(ssl, publicKey, creds.user, creds.password,
                creds.domain ?? string.Empty, _host, _kerberos, _logger);
            var result = await credssp.AuthenticateAsync(ct);
            if (!result.Success)
            {
                await SendStatusAsync("error", result.Error ?? "NLA authentication failed", ct);
                return;
            }
        }

        await SendStatusAsync("ready", null, ct);
        _logger.LogInformation("RDP relay: bridging {Host}:{Port} (protocol=0x{Proto:X})", _host, _port, selected);

        // 5) Relay the decrypted RDP stream both ways until either side closes.
        // Optional: live decode + record both directions through the shared RdpWire engine.
        _recorder = RdpRecorder.TryCreate(_logger);
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

    /// <summary>
    /// Sends the X.224 Connection Request with an RDP Negotiation Request asking for HYBRID|SSL, reads
    /// the Connection Confirm, and returns the protocol the server selected. Throws on a negotiation
    /// failure response.
    /// </summary>
    private async Task<uint> NegotiateX224Async(NetworkStream net, CancellationToken ct)
    {
        // RDP Negotiation Request (8 bytes): type(1)=0x01, flags(1)=0, length(2)=8, requestedProtocols(4).
        var neg = new byte[8];
        neg[0] = TYPE_RDP_NEG_REQ;
        neg[1] = 0;
        neg[2] = 8; neg[3] = 0;
        BitConverter.GetBytes(PROTOCOL_HYBRID | PROTOCOL_SSL).CopyTo(neg, 4);

        // X.224 Connection Request (CR) TPDU: len(1) CR-CDT(1)=0xE0 dst-ref(2)=0 src-ref(2)=0 class(1)=0
        // followed by the negotiation request as user data.
        var x224 = new byte[7 + neg.Length];
        x224[0] = (byte)(x224.Length - 1); // LI: length indicator (excludes this byte)
        x224[1] = 0xE0;                     // CR
        // dst-ref, src-ref, class option all zero
        neg.CopyTo(x224, 7);

        // TPKT header (4 bytes): version(1)=3, reserved(1)=0, length(2, big-endian) over the whole PDU.
        int total = 4 + x224.Length;
        var pdu = new byte[total];
        pdu[0] = 3; pdu[1] = 0;
        pdu[2] = (byte)(total >> 8); pdu[3] = (byte)(total & 0xff);
        x224.CopyTo(pdu, 4);

        await net.WriteAsync(pdu, ct);
        await net.FlushAsync(ct);

        // Read TPKT header then the rest.
        var tpkt = await ReadExactAsync(net, 4, ct);
        if (tpkt[0] != 3) throw new IOException("bad TPKT version in connection confirm");
        int respLen = (tpkt[2] << 8) | tpkt[3];
        var body = await ReadExactAsync(net, respLen - 4, ct);

        // body = X.224 CC: LI(1) CC-CDT(1)=0xD0 dst-ref(2) src-ref(2) class(1) [optional nego rsp/failure]
        if (body.Length < 7) throw new IOException("short X.224 connection confirm");
        int li = body[0];
        // The negotiation response (8 bytes) follows the 7-byte fixed CC header, if present.
        if (li >= 14 && body.Length >= 15)
        {
            byte negType = body[7];
            if (negType == TYPE_RDP_NEG_FAILURE)
            {
                uint failureCode = BitConverter.ToUInt32(body, 11);
                throw new IOException($"RDP negotiation failure code {failureCode}");
            }
            if (negType == TYPE_RDP_NEG_RSP)
            {
                return BitConverter.ToUInt32(body, 11);
            }
        }
        // No negotiation response — server fell back to plain RDP security (no TLS). We required SSL.
        throw new IOException("server did not select TLS/NLA");
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

    // Live structural decode + recording of the decrypted RDP stream via the shared RdpWire engine.
    // Writes <dir>/pdus.log + pdus.jsonl (decoded PDU tree) and <dir>/meta.txt (per-chunk dir+ts+len, so
    // an offline `rdpmitm --replay <dir>` reconstructs true wire order). Both pumps feed one RdpSession
    // (server-learned channel/DVC/GFX state labels client traffic too); a lock serializes the two pumps.
    private sealed class RdpRecorder : IDisposable
    {
        private readonly RdpLogSink _sink;
        private readonly RdpSession _session;
        private readonly StreamWriter? _meta;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private readonly ILogger _logger;
        // The structural decode (RdpSession.Feed) must NEVER run on the relay's hot path — it's diagnostic
        // only, and blocking the s2c pump on a per-chunk decode back-pressures the host (it pauses mid-
        // frame, which we were debugging). So Feed() just enqueues a copy; a single background task drains
        // the queue and decodes serially (RdpSession is stateful/not thread-safe, so one consumer only).
        private readonly System.Threading.Channels.Channel<(RdpDir dir, long ms, byte[] data)> _queue;
        private readonly Task _drain;

        private RdpRecorder(string dir, ILogger logger)
        {
            _logger = logger;
            _sink = new RdpLogSink(dir, echoConsole: false);
            _session = new RdpSession(_sink);
            try { _meta = new StreamWriter(Path.Combine(dir, "meta.txt")) { AutoFlush = true }; } catch { _meta = null; }
            _queue = System.Threading.Channels.Channel.CreateUnbounded<(RdpDir, long, byte[])>(
                new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
            _drain = Task.Run(DrainAsync);
        }

        public static RdpRecorder? TryCreate(ILogger logger)
        {
            var dir = Environment.GetEnvironmentVariable("RDPGW_DUMP_DIR");
            if (string.IsNullOrEmpty(dir)) return null;
            try { Directory.CreateDirectory(dir); return new RdpRecorder(dir, logger); }
            catch (Exception ex) { logger.LogDebug(ex, "RDP recorder: init failed"); return null; }
        }

        // Hot-path: copy + enqueue only. Never blocks on decode. (The copy is required because the
        // caller's buffer is reused after this returns.)
        public void Feed(RdpDir dir, ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return;
            _queue.Writer.TryWrite((dir, _sw.ElapsedMilliseconds, data.ToArray()));
        }

        private async Task DrainAsync()
        {
            try
            {
                await foreach (var (dir, ms, data) in _queue.Reader.ReadAllAsync())
                {
                    try
                    {
                        _meta?.WriteLine($"{ms,8} {(dir == RdpDir.ClientToServer ? "C2S" : "S2C")} {data.Length}");
                        _session.Feed(dir, data);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "RDP recorder: decode error"); }
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "RDP recorder: drain ended"); }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            try { _drain.Wait(TimeSpan.FromSeconds(5)); } catch { }
            try { _meta?.Dispose(); } catch { }
            try { _sink.Dispose(); } catch { }
        }
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

    private static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) throw new IOException("unexpected end of stream");
            read += n;
        }
        return buf;
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
