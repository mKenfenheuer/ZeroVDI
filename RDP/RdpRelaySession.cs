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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toWs = PumpSslToWsAsync(ssl, linked.Token);
        var toRdp = PumpWsToSslAsync(ssl, linked.Token);
        // Whichever pump finishes first ends the session. If the host→ws pump ended, the target closed
        // or died; tell the browser explicitly before closing so it doesn't sit on a frozen screen.
        var finished = await Task.WhenAny(toWs, toRdp);
        linked.Cancel();
        try { await Task.WhenAll(toWs, toRdp); } catch { /* shutdown races are expected */ }

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

    private async Task PumpSslToWsAsync(SslStream ssl, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        long total = 0;
        long chunks = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await ssl.ReadAsync(buffer, ct);
                if (n == 0) { _logger.LogWarning("RDP relay: host→ws pump: host closed (0 bytes) after {Total} bytes / {Chunks} chunks", total, chunks); break; }
                total += n;
                chunks++;
                await _ws.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary,
                    endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // This is the pump that feeds the browser; if it dies the screen freezes. Surface the real
            // exception (the macOS AppleCrypto SslStream post-handshake read quirk shows up here).
            _logger.LogError(ex, "RDP relay: host→ws pump FAILED after {Total} bytes", total);
        }
    }

    private async Task PumpWsToSslAsync(SslStream ssl, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count == 0) continue;
                // Browser sends raw RDP bytes as binary frames; write straight to the host. (A frame
                // may be partial; RDP framing is the browser's concern, so just forward bytes.)
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
