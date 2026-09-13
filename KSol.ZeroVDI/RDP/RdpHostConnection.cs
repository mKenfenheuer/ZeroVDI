using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Opens the gateway's <i>host-side</i> half of an RDP connection: TCP connect, X.224
/// <c>PROTOCOL_HYBRID</c> negotiation, TLS termination, and the CredSSP (NLA) client handshake with the
/// VM credentials. On success it yields the decrypted <see cref="SslStream"/> positioned at the start of
/// the (NLA-pre-authenticated) RDP connection sequence — the same point both the browser console relay
/// (<see cref="RdpRelaySession"/>) and the native MITM relay (<see cref="MitmRdpStream"/>) take over and
/// relay the opaque RDP byte stream.
///
/// This was lifted verbatim out of <see cref="RdpRelaySession"/> so the two relay paths share one
/// host-connect implementation; the X.224 framing details live here.
/// </summary>
public sealed class RdpHostConnection
{
    // X.224 RDP Negotiation request/response protocol flags ([MS-RDPBCGR] 2.2.1.1.1 / 2.2.1.2.1).
    private const uint PROTOCOL_SSL = 0x00000001;
    private const uint PROTOCOL_HYBRID = 0x00000002;
    private const uint PROTOCOL_HYBRID_EX = 0x00000008;
    private const byte TYPE_RDP_NEG_REQ = 0x01;
    private const byte TYPE_RDP_NEG_RSP = 0x02;
    private const byte TYPE_RDP_NEG_FAILURE = 0x03;

    private readonly string _host;
    private readonly int _port;
    private readonly KerberosAuth? _kerberos;
    private readonly ILogger _logger;
    private readonly IHostTransport _transport;
    private readonly Func<X509Certificate2, CancellationToken, Task<string?>>? _certCheck;

    /// <param name="transport">
    /// How to obtain the raw byte stream to the host. Defaults to <see cref="DirectTcpTransport"/> (a plain
    /// TCP connect); pass a <see cref="ConnectorTcpTransport"/> to tunnel through a connector.
    /// </param>
    /// <param name="certCheck">
    /// Host certificate policy (see <see cref="HostCertificatePolicy"/>): invoked with the certificate the
    /// host presented, after the TLS handshake and before any credential is sent; a non-null result is a
    /// user-facing refusal reason. Null = accept any certificate.
    /// </param>
    public RdpHostConnection(string host, int port, KerberosAuth? kerberos, ILogger logger,
        IHostTransport? transport = null, Func<X509Certificate2, CancellationToken, Task<string?>>? certCheck = null)
    {
        _host = host;
        _port = port;
        _kerberos = kerberos;
        _logger = logger;
        _transport = transport ?? new DirectTcpTransport(logger);
        _certCheck = certCheck;
    }

    /// <summary>
    /// The result of a successful connect: the decrypted RDP stream over its underlying transport,
    /// positioned at the MCS Connect-Initial exchange (where the browser client takes over).
    /// <see cref="Stream"/> is an <see cref="SslStream"/> for the native-RDP path, but is typed
    /// <see cref="System.IO.Stream"/> so a bridge resolver (e.g. VNC) can hand back a plain in-memory
    /// duplex stream — the relay pumps use only base-<see cref="System.IO.Stream"/> members.
    /// </summary>
    public sealed record Connected(Stream Stream, Stream Inner, uint SelectedProtocol = PROTOCOL_HYBRID) : IDisposable
    {
        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { Inner.Dispose(); } catch { }
        }
    }

    /// <summary>Raised with a caller-friendly status when a stage of the connect fails.</summary>
    public sealed class ConnectException(string status) : Exception(status)
    {
        public string Status { get; } = status;
    }

    /// <summary>
    /// Connects and pre-authenticates to the host. Throws <see cref="ConnectException"/> with a
    /// caller-facing message on any failure stage (TCP, negotiation, TLS, NLA).
    /// </summary>
    /// <param name="creds">VM credentials for the CredSSP/NLA handshake.</param>
    /// <param name="requestedProtocols">
    /// The X.224 RDP-negotiation protocols to request from the host. Defaults to <c>HYBRID|SSL</c>. The
    /// MITM relay passes the <i>client's</i> requested protocols through unchanged so the host echoes the
    /// value the client expects in its MCS Connect-Response (otherwise the client aborts with 0x609).
    /// </param>
    /// <param name="routingToken">
    /// Optional X.224 routing token (the raw <c>Cookie: msts=...\r\n</c> bytes) from a Server Redirection.
    /// When set it is prepended to the X.224 Connection Request so a load-balancer / session broker — e.g.
    /// GNOME Remote Desktop's system "Remote Login" mode — routes this reconnect to the redirected session.
    /// </param>
    public async Task<Connected> ConnectAsync(RdpRelaySession.VmCredentials creds,
        uint requestedProtocols = PROTOCOL_HYBRID | PROTOCOL_SSL, CancellationToken ct = default,
        byte[]? routingToken = null)
    {
        Stream netStream;
        try
        {
            netStream = await _transport.ConnectAsync(_host, _port, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller (browser closed the WebSocket / admin force-disconnect) canceled us — this is not
            // a host-reachability failure. Propagate as cancellation so the resolver doesn't misread it as
            // an NLA rejection and retry, and so the log doesn't claim "cannot reach host" for a live host.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RDP host: connect to {Host}:{Port} failed", _host, _port);
            throw new ConnectException($"cannot reach {_host}:{_port}");
        }

        uint selected;
        try
        {
            selected = await NegotiateX224Async(netStream, requestedProtocols, routingToken, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            netStream.Dispose();
            throw; // caller canceled — not a negotiation failure; don't trigger the TLS-only retry.
        }
        catch (Exception ex)
        {
            netStream.Dispose();
            _logger.LogWarning(ex, "RDP host: X.224 negotiation failed");
            throw new ConnectException("RDP negotiation failed");
        }

        // TLS terminate. The handshake accepts whatever certificate the host presents (self-signed VMs are
        // the norm, so CA validation is impossible); the certificate is captured and then judged by the
        // pinning policy below (trust on first use, HostCertificatePolicy) BEFORE any credential is sent,
        // and CredSSP additionally verifies the server's sealed public-key confirmation, which an active
        // man-in-the-middle cannot produce without the password.
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            netStream.Dispose();
            throw; // caller canceled mid-handshake — not a TLS failure.
        }
        catch (Exception ex)
        {
            netStream.Dispose();
            _logger.LogWarning(ex, "RDP host: TLS handshake failed");
            throw new ConnectException("TLS handshake failed");
        }

        // Host certificate policy (pinning): refuse BEFORE CredSSP sends anything, and before a TLS-only
        // (xrdp) session starts relaying the in-band login screen to the user. A policy-hook failure is
        // fail-closed — the message says so, and the policy has an explicit Off mode.
        if (serverCert != null && _certCheck != null)
        {
            string? refusal;
            try { refusal = await _certCheck(serverCert, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { netStream.Dispose(); throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RDP host: certificate policy check failed for {Host}:{Port}", _host, _port);
                refusal = "the host certificate could not be checked against the pinning policy";
            }
            if (refusal != null)
            {
                netStream.Dispose();
                throw new ConnectException(refusal);
            }
        }

        // CredSSP (NLA) when the host selected HYBRID or HYBRID_EX; if it (unusually) selected plain SSL
        // there is no NLA step and we relay straight away.
        if ((selected & (PROTOCOL_HYBRID | PROTOCOL_HYBRID_EX)) != 0)
        {
            if (serverCert == null)
            {
                netStream.Dispose();
                throw new ConnectException("no server certificate for NLA");
            }
            // CredSSP binds the raw public key (the SubjectPublicKey BIT STRING contents), the same bytes
            // FreeRDP/Windows hash — NOT the full SubjectPublicKeyInfo.
            var publicKey = serverCert.PublicKey.EncodedKeyValue.RawData;
            var credssp = new CredSspClient(ssl, publicKey, creds.user, creds.password,
                creds.domain ?? string.Empty, _host, _kerberos, _logger);
            var result = await credssp.AuthenticateAsync(ct);
            if (!result.Success)
            {
                netStream.Dispose();
                throw new ConnectException(result.Error ?? "NLA authentication failed");
            }

            // HYBRID_EX: after CredSSP the server sends a 4-byte Early User Authorization Result PDU
            // ([MS-RDPBCGR] 2.2.10.2) over the TLS channel, before any RDP data. Consume it here so the
            // relay's decrypted stream starts cleanly at the RDP connection sequence. (Our client-facing
            // leg selects plain HYBRID, so we do not forward this PDU to the client.)
            if ((selected & PROTOCOL_HYBRID_EX) != 0)
            {
                var euar = await ReadExactAsync(ssl, 4, ct);
                uint authResult = BitConverter.ToUInt32(euar, 0);
                _logger.LogInformation("RDP host: HYBRID_EX early user auth result = {Result}", authResult);
                if (authResult != 0)
                {
                    netStream.Dispose();
                    throw new ConnectException($"host denied access (early user auth result {authResult})");
                }
            }
        }

        _logger.LogInformation("RDP host: connected {Host}:{Port} (protocol=0x{Proto:X})", _host, _port, selected);
        return new Connected(ssl, netStream, selected);
    }

    /// <summary>
    /// Sends the X.224 Connection Request asking for HYBRID|SSL, reads the Connection Confirm, and
    /// returns the protocol the server selected. Throws on a negotiation failure response.
    /// </summary>
    private async Task<uint> NegotiateX224Async(Stream net, uint requestedProtocols,
        byte[]? routingToken, CancellationToken ct)
    {
        var neg = new byte[8];
        neg[0] = TYPE_RDP_NEG_REQ;
        neg[1] = 0;
        neg[2] = 8; neg[3] = 0;
        BitConverter.GetBytes(requestedProtocols).CopyTo(neg, 4);

        // X.224 CR variable part: optional routing token (Cookie: msts=...\r\n) then the RDP_NEG_REQ.
        // [MS-RDPBCGR] 2.2.1.1: the routing token, when present, precedes the negotiation request.
        var cookie = routingToken ?? Array.Empty<byte>();
        if (cookie.Length > 0)
        {
            int show = Math.Min(cookie.Length, 48);
            var asc = new char[show];
            for (int j = 0; j < show; j++) asc[j] = cookie[j] is >= (byte)0x20 and < (byte)0x7f ? (char)cookie[j] : '.';
            _logger.LogInformation("RDP host: X.224 routing token {Len}B endsCRLF={Crlf} hex={Hex} ascii='{Ascii}'",
                cookie.Length,
                cookie.Length >= 2 && cookie[^2] == 0x0D && cookie[^1] == 0x0A,
                Convert.ToHexString(cookie.AsSpan(0, show)), new string(asc));
        }
        var x224 = new byte[7 + cookie.Length + neg.Length];
        x224[0] = (byte)(x224.Length - 1); // LI
        x224[1] = 0xE0;                     // CR
        cookie.CopyTo(x224, 7);
        neg.CopyTo(x224, 7 + cookie.Length);

        int total = 4 + x224.Length;
        var pdu = new byte[total];
        pdu[0] = 3; pdu[1] = 0;
        pdu[2] = (byte)(total >> 8); pdu[3] = (byte)(total & 0xff);
        x224.CopyTo(pdu, 4);

        await net.WriteAsync(pdu, ct);
        await net.FlushAsync(ct);

        var tpkt = await ReadExactAsync(net, 4, ct);
        if (tpkt[0] != 3) throw new IOException("bad TPKT version in connection confirm");
        int respLen = (tpkt[2] << 8) | tpkt[3];
        var body = await ReadExactAsync(net, respLen - 4, ct);

        if (body.Length < 7) throw new IOException("short X.224 connection confirm");
        int li = body[0];
        if (li >= 14 && body.Length >= 15)
        {
            byte negType = body[7];
            if (negType == TYPE_RDP_NEG_FAILURE)
            {
                uint failureCode = BitConverter.ToUInt32(body, 11);
                throw new IOException($"RDP negotiation failure code {failureCode}");
            }
            if (negType == TYPE_RDP_NEG_RSP)
                return BitConverter.ToUInt32(body, 11);
        }
        throw new IOException("server did not select TLS/NLA");
    }

    internal static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken ct)
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
}
