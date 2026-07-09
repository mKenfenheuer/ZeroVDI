using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// The host-side <see cref="Stream"/> the gateway hands to the rdpgw.net library for a native RDGW
/// session, implementing a full RDP NLA <b>man-in-the-middle</b>. The library relays opaque bytes
/// between the RDP client (through the RDGW tunnel) and this stream; this stream <i>is</i> the RDP
/// front-end the client believes it is talking to:
/// <list type="number">
/// <item>absorbs the client's X.224 <c>PROTOCOL_HYBRID</c> connection request and replies selecting NLA;</item>
/// <item>performs a TLS <i>server</i> handshake presenting the gateway certificate;</item>
/// <item>runs <see cref="CredSspServer"/> over that TLS, validating the client's NTLMv2 against the
///       gateway-login NT hash (the credentials the user already holds — see the SSO design);</item>
/// <item>opens the real host connection with the <i>stored</i> host credentials via
///       <see cref="RdpHostConnection"/> and relays the decrypted RDP stream both ways, teeing it into
///       the optional recorder.</item>
/// </list>
/// From the library's perspective this is just a duplex byte stream: <see cref="Write"/> feeds it the
/// client→gateway bytes and <see cref="Read"/> drains the gateway→client bytes. Internally a background
/// front-end task drives the handshake state machine and then the relay.
/// </summary>
public sealed class MitmRdpStream : Stream
{
    // X.224 negotiation flags ([MS-RDPBCGR] 2.2.1.1.1 / 2.2.1.2.1).
    private const uint PROTOCOL_SSL = 0x00000001;
    private const uint PROTOCOL_HYBRID = 0x00000002;
    private const uint PROTOCOL_HYBRID_EX = 0x00000008;
    private const byte TYPE_RDP_NEG_REQ = 0x01;
    private const byte TYPE_RDP_NEG_RSP = 0x02;

    private readonly string _host;
    private readonly int _port;
    private readonly KerberosAuth? _kerberos;
    private readonly RdpRelaySession.VmCredentials? _hostCreds;  // stored host creds (SSO); null => use client-delegated
    private readonly byte[] _clientExpectedNtHash;              // gateway-login NT hash the client must prove
    private readonly X509Certificate2 _serverCert;
    private readonly IRdpMediaSink? _mediaSink;
    private readonly Action? _onCompleted;
    private readonly ILogger _logger;

    // Client-facing byte pipes. The library writes client→gateway bytes via Write() into _toFrontEnd;
    // it reads gateway→client bytes via Read() out of _fromFrontEnd. The front-end task owns the other
    // ends through ClientFacingStream.
    private readonly Channel<byte[]> _toFrontEnd =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<byte[]> _fromFrontEnd =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _frontEnd;
    private byte[] _readResidual = Array.Empty<byte>();
    private int _readResidualOffset;
    // Routing token (Cookie: msts=...\r\n) extracted from the client's X.224 CR. mstsc includes it on a
    // Server Redirection reconnect; we forward it to the host so the broker hands us to the real session.
    private byte[]? _clientRoutingToken;

    /// <param name="hostCreds">
    /// Stored host credentials to authenticate to the host (SSO). When <c>null</c>, the credentials the
    /// client delegates through CredSSP are used instead (recording without SSO).
    /// </param>
    /// <param name="onCompleted">Invoked once when the session ends (for recording finalization).</param>
    public MitmRdpStream(string host, int port, KerberosAuth? kerberos,
        RdpRelaySession.VmCredentials? hostCreds, byte[] clientExpectedNtHash,
        X509Certificate2 serverCert, IRdpMediaSink? mediaSink, ILogger logger,
        Action? onCompleted = null)
    {
        _host = host;
        _port = port;
        _kerberos = kerberos;
        _hostCreds = hostCreds;
        _clientExpectedNtHash = clientExpectedNtHash;
        _serverCert = serverCert;
        _mediaSink = mediaSink;
        _onCompleted = onCompleted;
        _logger = logger;
        _logger.LogInformation("MITM: stream constructed for {Host}:{Port}; scheduling front-end", _host, _port);
        _frontEnd = Task.Run(() => RunFrontEndAsync(_cts.Token));
        // Surface any unobserved fault from the front-end task (the lib's relay swallows stream errors).
        _ = _frontEnd.ContinueWith(t =>
            _logger.LogError(t.Exception, "MITM: front-end task faulted"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // ============================ Stream surface (library-facing) ============================

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return;
        await _toFrontEnd.Writer.WriteAsync(buffer.ToArray(), LinkTo(ct));
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_readResidualOffset >= _readResidual.Length)
        {
            try
            {
                _readResidual = await _fromFrontEnd.Reader.ReadAsync(LinkTo(ct));
                _readResidualOffset = 0;
            }
            catch (ChannelClosedException) { return 0; }
            catch (OperationCanceledException) { return 0; }
        }
        int n = Math.Min(buffer.Length, _readResidual.Length - _readResidualOffset);
        _readResidual.AsSpan(_readResidualOffset, n).CopyTo(buffer.Span);
        _readResidualOffset += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private CancellationToken LinkTo(CancellationToken ct) =>
        ct.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct).Token : _cts.Token;

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _cts.Cancel(); } catch { }
            _toFrontEnd.Writer.TryComplete();
            _fromFrontEnd.Writer.TryComplete();
            try { _frontEnd.Wait(TimeSpan.FromSeconds(5)); } catch { }
            try { _cts.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }

    // ============================ Front-end state machine ============================

    private async Task RunFrontEndAsync(CancellationToken ct)
    {
        RdpHostConnection.Connected? host = null;
        try
        {
            var clientFacing = new ClientFacingStream(_toFrontEnd.Reader, _fromFrontEnd.Writer, _cts.Token);

            // 1) X.224: read the client's CR (negotiation request), reply with CC selecting HYBRID(_EX).
            _logger.LogInformation("MITM: front-end starting; awaiting client X.224 CR for {Host}:{Port}", _host, _port);
            var (clientRequestedProtocols, selectedProtocol) = await NegotiateX224ServerAsync(clientFacing, ct);
            _logger.LogInformation("MITM: X.224 negotiated (selected 0x{Sel:X}); starting TLS server handshake", selectedProtocol);

            // 2) TLS server handshake presenting the gateway cert.
            var clientTls = new SslStream(clientFacing, leaveInnerStreamOpen: true);
            await clientTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                    | System.Security.Authentication.SslProtocols.Tls13,
            }, ct);
            _logger.LogInformation("MITM: client TLS established ({Proto}); starting CredSSP server", clientTls.SslProtocol);

            // 3) CredSSP server: validate the client's NTLMv2 against the gateway-login NT hash and the
            // public key the client bound (our cert's raw SubjectPublicKey).
            var pubKey = _serverCert.PublicKey.EncodedKeyValue.RawData;
            var credssp = new CredSspServer(clientTls, pubKey, _clientExpectedNtHash, _logger);
            var auth = await credssp.AuthenticateAsync(ct);
            if (!auth.Success)
            {
                _logger.LogWarning("MITM: client NLA failed: {Error}", auth.Error);
                return;
            }

            // HYBRID_EX: mirror the host leg — after CredSSP the server sends the client a 4-byte Early
            // User Authorization Result PDU ([MS-RDPBCGR] 2.2.10.2) over the TLS channel (0 = success),
            // before any RDP data. Without it a HYBRID_EX client waits for / mis-frames the RDP stream.
            if ((selectedProtocol & PROTOCOL_HYBRID_EX) != 0)
            {
                await clientTls.WriteAsync(new byte[4], ct);  // authorizationResult = 0 (AUTHZ_SUCCESS)
                await clientTls.FlushAsync(ct);
                _logger.LogInformation("MITM: sent HYBRID_EX early user auth result to client");
            }

            // 4) Open the real host. For SSO use the stored host credentials (the client's delegated
            // creds are never forwarded); otherwise (recording-only, no SSO) forward the credentials the
            // client just delegated so it reaches the host with the identity it intended.
            // On a Server Redirection reconnect (routing token present) the broker (GNOME "Remote Login")
            // expects the ONE-TIME session credentials it handed back — which mstsc presents via NLA, so
            // they arrive as the client-delegated creds. Prefer those over stored SSO creds in that case;
            // otherwise use stored SSO creds (or delegated creds when there's no SSO).
            var delegated = new RdpRelaySession.VmCredentials(
                auth.DelegatedUser ?? string.Empty, auth.DelegatedPassword ?? string.Empty, auth.DelegatedDomain);
            var hostCreds = (_clientRoutingToken != null && !string.IsNullOrEmpty(auth.DelegatedPassword))
                ? delegated
                : (_hostCreds ?? delegated);
            // Request the SAME protocols toward the host that the client requested, so the host echoes
            // the value the client expects in its MCS Connect-Response (avoids the negotiation-flags
            // mismatch / 0x609 abort).
            // The native MITM path is RDP-only forever (a native mstsc speaks RDP), so always the NLA
            // resolver over a direct TCP transport — same behaviour as before the resolver seam.
            host = await new NlaRdpResolver().ConnectAsync(
                new RdpResolveRequest(_host, _port, hostCreds, new DirectTcpTransport(_logger), _kerberos,
                    clientRequestedProtocols, _clientRoutingToken, _logger),
                ct);
            _logger.LogInformation("MITM: bridging client <-> {Host}:{Port} (sso={Sso}, routingToken={Tok}B)",
                _host, _port, _hostCreds != null, _clientRoutingToken?.Length ?? 0);

            // 5) Relay the decrypted RDP stream both ways, teeing into the recorder.
            await RelayDecryptedAsync(clientTls, host.Stream, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MITM: front-end ended with error");
        }
        finally
        {
            host?.Dispose();
            _fromFrontEnd.Writer.TryComplete();
            try { _onCompleted?.Invoke(); } catch (Exception ex) { _logger.LogWarning(ex, "MITM: onCompleted callback failed"); }
        }
    }

    /// <summary>
    /// Reads the client's X.224 Connection Request (TPKT + CR with an RDP negotiation request) and
    /// writes back a Connection Confirm selecting <c>PROTOCOL_HYBRID</c>, so the client proceeds to TLS+NLA.
    /// </summary>
    private async Task<(uint Requested, uint Selected)> NegotiateX224ServerAsync(Stream client, CancellationToken ct)
    {
        var tpkt = await RdpHostConnection.ReadExactAsync(client, 4, ct);
        if (tpkt[0] != 3) throw new IOException("MITM: bad TPKT version in connection request");
        int total = (tpkt[2] << 8) | tpkt[3];
        if (total < 4 || total > 4096) throw new IOException("MITM: implausible X.224 CR length");
        var crBody = await RdpHostConnection.ReadExactAsync(client, total - 4, ct);

        // The CR variable part (after the 7-byte fixed header LI/CR/dst/src/class) may contain, in order:
        // an optional routing token / Cookie ("Cookie: msts=...\r\n" or a "mstshash=" cookie) and then an
        // optional RDP_NEG_REQ: type(1) flags(1) length(2) requestedProtocols(4). On a Server Redirection
        // reconnect, mstsc puts the LoadBalanceInfo routing token here — we MUST forward it to the host so
        // the broker lands us on the redirected (handed-over) session instead of looping the redirect.
        uint requested = 0;
        _clientRoutingToken = ExtractRoutingToken(crBody);
        int negOff = 7 + (_clientRoutingToken?.Length ?? 0);
        if (crBody.Length >= negOff + 8 && crBody[negOff] == TYPE_RDP_NEG_REQ)
            requested = BitConverter.ToUInt32(crBody, negOff + 4);
        var crAscii = new char[crBody.Length];
        for (int j = 0; j < crBody.Length; j++) crAscii[j] = crBody[j] is >= (byte)0x20 and < (byte)0x7f ? (char)crBody[j] : '.';
        _logger.LogInformation("MITM: client X.224 CR ({Len}B), requestedProtocols=0x{Req:X} routingToken={Tok}B cr={Hex} ascii='{Ascii}'",
            total, requested, _clientRoutingToken?.Length ?? 0, Convert.ToHexString(crBody), new string(crAscii));

        // Select HYBRID_EX when the client offered it, otherwise plain HYBRID. Matching the client's
        // preference keeps both gateway legs symmetric: the host leg requests the same set and also lands
        // on HYBRID_EX, so the EUAR PDU and the client's GCC earlyCapabilityFlags line up on both sides.
        uint selectedProtocol = (requested & PROTOCOL_HYBRID_EX) != 0 ? PROTOCOL_HYBRID_EX : PROTOCOL_HYBRID;
        var neg = new byte[8];
        neg[0] = TYPE_RDP_NEG_RSP;
        neg[1] = 0;                       // flags
        neg[2] = 8; neg[3] = 0;           // length
        BitConverter.GetBytes(selectedProtocol).CopyTo(neg, 4);

        // X.224 CC TPDU: LI(1) CC-CDT(1)=0xD0 dst-ref(2) src-ref(2) class(1) + nego rsp.
        var x224 = new byte[7 + neg.Length];
        x224[0] = (byte)(x224.Length - 1);
        x224[1] = 0xD0;                   // CC
        neg.CopyTo(x224, 7);

        int pduLen = 4 + x224.Length;
        var pdu = new byte[pduLen];
        pdu[0] = 3; pdu[1] = 0;
        pdu[2] = (byte)(pduLen >> 8); pdu[3] = (byte)(pduLen & 0xff);
        x224.CopyTo(pdu, 4);

        await client.WriteAsync(pdu, ct);
        await client.FlushAsync(ct);
        return (requested, selectedProtocol);
    }

    /// <summary>
    /// Extract the X.224 routing token / cookie from a Connection Request body, if present. Per
    /// [MS-RDPBCGR] 2.2.1.1 the variable part after the 7-byte fixed CR header may begin with a routing
    /// token ("Cookie: msts=...\r\n") or a cookie ("Cookie: mstshash=...\r\n") — ASCII text terminated by
    /// CRLF — before the optional RDP_NEG_REQ. Returns the raw bytes INCLUDING the trailing CRLF (the exact
    /// form to re-send to the host), or null if there is no token. We only treat it as a routing token
    /// (worth forwarding) when it's the "msts=" load-balance form a redirect produces.
    /// </summary>
    private static byte[]? ExtractRoutingToken(byte[] crBody)
    {
        // Need at least the 7-byte fixed header + 1 byte to look at.
        if (crBody.Length <= 7) return null;
        // A token starts with 'C' ("Cookie:") or, for the bare routing-token form some clients send, the
        // token bytes directly. mstsc's redirect reconnect uses "Cookie: msts=<...>\r\n".
        if (crBody[7] != (byte)'C') return null; // not a Cookie/token (likely the RDP_NEG_REQ type byte)
        // Find the CRLF terminator within the variable part.
        for (int i = 7; i + 1 < crBody.Length; i++)
        {
            if (crBody[i] == 0x0D && crBody[i + 1] == 0x0A)
            {
                int len = (i + 2) - 7; // include CRLF
                var tok = new byte[len];
                Array.Copy(crBody, 7, tok, 0, len);
                // Only forward the load-balance routing token ("msts="); a plain "mstshash=" cookie is a
                // pre-auth hint the host doesn't need on our re-originated CR.
                var ascii = System.Text.Encoding.ASCII.GetString(tok);
                return ascii.Contains("msts=", StringComparison.Ordinal) ? tok : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Relays the decrypted RDP byte stream between the client-facing TLS and the host-facing TLS,
    /// teeing both directions into the optional recorder. Uses the same bounded-channel reader/writer
    /// split as <see cref="RdpRelaySession"/> on the host→client direction so a slow client never
    /// back-pressures (and stalls) the host mid-frame.
    /// </summary>
    private async Task RelayDecryptedAsync(SslStream client, Stream hostStream, CancellationToken ct)
    {
        using var recorder = RdpStreamRecorder.TryCreate(_logger, _mediaSink);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tok = linked.Token;

        // host -> client (recorded as ServerToClient)
        var toClient = PumpAsync(hostStream, client, RdpDir.ServerToClient, recorder, tok);
        // client -> host (recorded as ClientToServer)
        var toHost = PumpAsync(client, hostStream, RdpDir.ClientToServer, recorder, tok);

        await Task.WhenAny(toClient, toHost);
        linked.Cancel();
        try { await Task.WhenAll(toClient, toHost); } catch { /* shutdown races expected */ }
    }

    private async Task PumpAsync(Stream from, Stream to, RdpDir dir, RdpStreamRecorder? recorder, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await from.ReadAsync(buffer, ct);
                if (n == 0) break;
                recorder?.Feed(dir, buffer.AsSpan(0, n));
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                await to.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "MITM: {Dir} pump ended", dir); }
    }

    // ============================ Inner client-facing stream ============================

    /// <summary>
    /// A <see cref="Stream"/> view over the two client-facing channels, used by the X.224/TLS-server/
    /// CredSSP front-end. Reads pull client→gateway bytes; writes push gateway→client bytes.
    /// </summary>
    private sealed class ClientFacingStream : Stream
    {
        private readonly ChannelReader<byte[]> _in;
        private readonly ChannelWriter<byte[]> _out;
        private readonly CancellationToken _ct;
        private byte[] _residual = Array.Empty<byte>();
        private int _residualOffset;

        public ClientFacingStream(ChannelReader<byte[]> @in, ChannelWriter<byte[]> @out, CancellationToken ct)
        {
            _in = @in; _out = @out; _ct = ct;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_residualOffset >= _residual.Length)
            {
                try { _residual = await _in.ReadAsync(Link(ct)); _residualOffset = 0; }
                catch (ChannelClosedException) { return 0; }
                catch (OperationCanceledException) { return 0; }
            }
            int n = Math.Min(buffer.Length, _residual.Length - _residualOffset);
            _residual.AsSpan(_residualOffset, n).CopyTo(buffer.Span);
            _residualOffset += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => await _out.WriteAsync(buffer.ToArray(), Link(ct));

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        private CancellationToken Link(CancellationToken ct) =>
            ct.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(_ct, ct).Token : _ct;

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
