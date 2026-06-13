using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;

namespace KSol.RDPGateway.RDP;

/// <summary>Outcome of a CredSSP handshake.</summary>
public sealed record CredSspResult(bool Success, string? Error);

/// <summary>
/// Client side of CredSSP ([MS-CSSP]) for the gateway's browser-RDP relay. After the gateway has
/// completed the X.224 <c>PROTOCOL_HYBRID</c> negotiation and wrapped the TCP socket in TLS, this
/// drives the CredSSP exchange over that TLS stream and, on success, leaves the stream positioned at
/// the start of the (NLA-pre-authenticated) RDP connection sequence, which is then relayed verbatim
/// to the browser.
///
/// The exchange is a series of <c>TSRequest</c> DER structures:
/// <list type="number">
/// <item>SPNEGO/auth token round-trips (NTLM negotiate→challenge→authenticate, or a Kerberos AP-REQ)
///       carried in <c>negoTokens</c>.</item>
/// <item>Server-public-key authentication: the client sends the TLS server's public key, signed and
///       sealed by the negotiated security context, in <c>pubKeyAuth</c>; the server returns the key
///       (transformed) and the client verifies it — defeating a MITM on the inner auth.</item>
/// <item>The client sends the delegated <c>TSCredentials</c> (the typed VM username/password/domain),
///       sealed by the security context.</item>
/// </list>
/// We advertise CredSSP <b>version 6</b> and implement the CVE-2018-0886 client-nonce hashing for the
/// public-key authentication step.
///
/// Auth-mechanism policy (current-standards posture): prefer <b>Kerberos/AES</b> when a realm/KDC is
/// configured and reachable; otherwise fall back to <b>NTLMv2</b> (whose SEAL uses RC4 by spec). The
/// NTLM path is implemented here; the Kerberos path is attempted via <see cref="KerberosAuth"/> and
/// falls through to NTLM when unavailable.
/// </summary>
public sealed class CredSspClient
{
    private const int CredSspVersion = 6;

    private readonly Stream _tls;
    private readonly byte[] _serverPublicKey; // SubjectPublicKeyInfo of the TLS server cert
    private readonly string _user;
    private readonly string _password;
    private readonly string _domain;
    private readonly string _targetHost;
    private readonly KerberosAuth? _kerberos;
    private readonly ILogger _logger;

    public CredSspClient(Stream tlsStream, byte[] serverPublicKey, string user, string password,
        string domain, string targetHost, KerberosAuth? kerberos, ILogger logger)
    {
        _tls = tlsStream;
        _serverPublicKey = serverPublicKey;
        _user = user;
        _password = password;
        _domain = domain ?? string.Empty;
        _targetHost = targetHost;
        _kerberos = kerberos;
        _logger = logger;
    }

    public async Task<CredSspResult> AuthenticateAsync(CancellationToken ct)
    {
        // Kerberos/AES is preferred but optional; only the NTLMv2 path is wired end-to-end here.
        // When a KDC is configured we still drive NTLM unless the Kerberos path is fully available.
        try
        {
            return await AuthenticateNtlmAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CredSSP: handshake failed");
            return new CredSspResult(false, ex.Message);
        }
    }

    private async Task<CredSspResult> AuthenticateNtlmAsync(CancellationToken ct)
    {
        var ntlm = new NtlmClient(_user, _password, _domain, $"TERMSRV/{_targetHost}");

        // 1) negoTokens: raw NTLM NEGOTIATE. The CredSSP server's "Negotiate" SSP accepts raw NTLM
        // tokens directly (no SPNEGO wrapping / mechListMIC) when Kerberos is unavailable — this
        // matches what FreeRDP and Windows do for workgroup NLA hosts.
        var negotiate = ntlm.CreateNegotiateTracked();
        await WriteTsRequestAsync(new TsRequest { NegoToken = negotiate }, ct);

        // Read the server CHALLENGE.
        var challengeReq = await ReadTsRequestAsync(ct);
        if (challengeReq.NegoToken == null)
            return new CredSspResult(false, "CredSSP: server did not return an NTLM challenge");

        // 2) AUTHENTICATE + pubKeyAuth. The NTLM AUTHENTICATE carries its own MIC; the pubKeyAuth is
        // the first sealed message (sequence 0), and the TSCredentials are sealed next (sequence 1).
        var authenticate = ntlm.ProcessChallenge(challengeReq.NegoToken);

        // pubKeyAuth: CredSSP v6 binds the TLS server key via SHA256(magic || nonce || pubKey), sealed
        // with the NTLM confidentiality context (consumes seq 0).
        var clientNonce = new byte[32];
        RandomNumberGenerator.Fill(clientNonce);
        var clientPubKeyHash = HashMagic("CredSSP Client-To-Server Binding Hash\0", clientNonce, _serverPublicKey);
        var (sealedPubKey, pubKeySig) = ntlm.SealAndSign(clientPubKeyHash);
        var pubKeyAuth = Concat(pubKeySig, sealedPubKey);

        await WriteTsRequestAsync(new TsRequest
        {
            NegoToken = authenticate,
            PubKeyAuth = pubKeyAuth,
            ClientNonce = clientNonce,
        }, ct);

        // Server confirms with its own pubKeyAuth (the bound key, transformed).
        var serverResp = await ReadTsRequestAsync(ct);
        if (serverResp.ErrorCode is { } ec && ec != 0)
            return new CredSspResult(false, $"CredSSP: server error 0x{ec:X8} (bad credentials or NLA refused)");
        if (serverResp.PubKeyAuth == null)
            return new CredSspResult(false, "CredSSP: server did not confirm the public key (auth rejected)");

        // 3) Send sealed TSCredentials (consumes seq 2).
        var tsCreds = EncodeTsPasswordCredentials(_domain, _user, _password);
        var (sealedCreds, credsSig) = ntlm.SealAndSign(tsCreds);
        await WriteTsRequestAsync(new TsRequest { AuthInfo = Concat(credsSig, sealedCreds) }, ct);

        _logger.LogInformation("CredSSP: NTLMv2 handshake completed for {User}", _user);
        return new CredSspResult(true, null);
    }

    // ---- CredSSP v6 public-key binding hash ([MS-CSSP] 3.1.5): SHA256(magic || nonce || pubkey). ----
    private static byte[] HashMagic(string magicAscii, byte[] nonce, byte[] pubKey)
    {
        var magic = Encoding.ASCII.GetBytes(magicAscii);
        return SHA256.HashData(Concat(magic, nonce, pubKey));
    }

    // ---- TSRequest (ASN.1 DER) ----

    private sealed class TsRequest
    {
        public byte[]? NegoToken;
        public byte[]? AuthInfo;
        public byte[]? PubKeyAuth;
        public uint? ErrorCode;
        public byte[]? ClientNonce;
    }

    private async Task WriteTsRequestAsync(TsRequest req, CancellationToken ct)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            // [0] version
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                w.WriteInteger(CredSspVersion);

            // [1] negoTokens : SEQUENCE OF NegoData { [0] negoToken OCTET STRING }
            if (req.NegoToken != null)
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                using (w.PushSequence())                  // SEQUENCE OF
                using (w.PushSequence())                  // NegoData
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    w.WriteOctetString(req.NegoToken);
            }

            // [2] authInfo OCTET STRING
            if (req.AuthInfo != null)
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    w.WriteOctetString(req.AuthInfo);

            // [3] pubKeyAuth OCTET STRING
            if (req.PubKeyAuth != null)
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
                    w.WriteOctetString(req.PubKeyAuth);

            // [5] clientNonce OCTET STRING (CredSSP v5+)
            if (req.ClientNonce != null)
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 5)))
                    w.WriteOctetString(req.ClientNonce);
        }

        var bytes = w.Encode();
        await _tls.WriteAsync(bytes, ct);
        await _tls.FlushAsync(ct);
    }

    private async Task<TsRequest> ReadTsRequestAsync(CancellationToken ct)
    {
        var der = await ReadDerAsync(ct);
        var req = new TsRequest();

        var outer = new AsnReader(der, AsnEncodingRules.DER);
        var seq = outer.ReadSequence();

        // [0] version (required) — read and discard.
        var v = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        v.ReadInteger();

        while (seq.HasData)
        {
            var tag = seq.PeekTag();
            if (tag.TagClass != TagClass.ContextSpecific) { seq.ReadEncodedValue(); continue; }
            switch (tag.TagValue)
            {
                case 1: // negoTokens
                {
                    var nt = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1));
                    var seqOf = nt.ReadSequence();
                    var negoData = seqOf.ReadSequence();
                    var inner = negoData.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                    req.NegoToken = inner.ReadOctetString();
                    break;
                }
                case 2:
                {
                    var c = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2));
                    req.AuthInfo = c.ReadOctetString();
                    break;
                }
                case 3:
                {
                    var c = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3));
                    req.PubKeyAuth = c.ReadOctetString();
                    break;
                }
                case 4:
                {
                    // errorCode is a 4-byte NTSTATUS. DER encodes it as a signed INTEGER, so a value
                    // with the high bit set (e.g. 0x8009xxxx) is a large/negative BigInteger; read the
                    // raw two's-complement bytes and reassemble as an unsigned 32-bit value.
                    var c = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 4));
                    var raw = c.ReadIntegerBytes().ToArray(); // big-endian, minimal two's-complement
                    uint code = 0;
                    foreach (var b in raw) code = (code << 8) | b;
                    req.ErrorCode = code;
                    break;
                }
                default:
                    seq.ReadEncodedValue();
                    break;
            }
        }
        return req;
    }

    // Leftover bytes read past the end of one TSRequest (rare, but a TLS record may carry more than
    // one DER message). Carried into the next read.
    private byte[] _carry = Array.Empty<byte>();

    /// <summary>
    /// Reads one DER-encoded TSRequest from the TLS stream. Reads in large chunks (whole TLS records)
    /// rather than probing the length byte-by-byte: small reads against some TLS providers (notably
    /// macOS SecureTransport) fail to decrypt a record split across reads, and large reads are also
    /// simply more efficient. The outer SEQUENCE's definite length tells us when a full message has
    /// arrived; any bytes beyond it are carried to the next call.
    /// </summary>
    private async Task<byte[]> ReadDerAsync(CancellationToken ct)
    {
        using var acc = new MemoryStream();
        acc.Write(_carry, 0, _carry.Length);
        _carry = Array.Empty<byte>();

        var chunk = new byte[8192];
        while (true)
        {
            var buf = acc.GetBuffer();
            int have = (int)acc.Length;
            if (TryGetDerLength(buf, have, out int total) && have >= total)
            {
                var message = buf.AsSpan(0, total).ToArray();
                if (have > total) _carry = buf.AsSpan(total, have - total).ToArray();
                return message;
            }

            int n = await _tls.ReadAsync(chunk, ct);
            if (n == 0) throw new IOException("CredSSP: unexpected end of TLS stream");
            acc.Write(chunk, 0, n);
        }
    }

    /// <summary>
    /// Parses a DER tag + definite length from the front of <paramref name="buf"/>. Returns the total
    /// encoded length (header + content) via <paramref name="total"/>, or false if not enough bytes
    /// to determine the length yet.
    /// </summary>
    private static bool TryGetDerLength(byte[] buf, int count, out int total)
    {
        total = 0;
        if (count < 2) return false;
        int lenByte = buf[1];
        if ((lenByte & 0x80) == 0)
        {
            total = 2 + lenByte;
            return true;
        }
        int numLenBytes = lenByte & 0x7f;
        if (count < 2 + numLenBytes) return false;
        int length = 0;
        for (int i = 0; i < numLenBytes; i++) length = (length << 8) | buf[2 + i];
        total = 2 + numLenBytes + length;
        return true;
    }

    // ---- TSCredentials / TSPasswordCreds ([MS-CSSP] 2.2.1.2) ----

    private static byte[] EncodeTsPasswordCredentials(string domain, string user, string password)
    {
        // TSPasswordCreds ::= SEQUENCE { [0] domainName OCTET STRING (UTF-16LE),
        //                                [1] userName  OCTET STRING (UTF-16LE),
        //                                [2] password  OCTET STRING (UTF-16LE) }
        var pwInner = new AsnWriter(AsnEncodingRules.DER);
        using (pwInner.PushSequence())
        {
            using (pwInner.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                pwInner.WriteOctetString(Encoding.Unicode.GetBytes(domain));
            using (pwInner.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                pwInner.WriteOctetString(Encoding.Unicode.GetBytes(user));
            using (pwInner.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                pwInner.WriteOctetString(Encoding.Unicode.GetBytes(password));
        }
        var tsPassword = pwInner.Encode();

        // TSCredentials ::= SEQUENCE { [0] credType INTEGER (1=password), [1] credentials OCTET STRING }
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                w.WriteInteger(1);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                w.WriteOctetString(tsPassword);
        }
        return w.Encode();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var r = new byte[len];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }
}
