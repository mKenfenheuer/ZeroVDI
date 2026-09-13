using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;

namespace KSol.ZeroVDI.RDP;

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

        // The protocol version in effect is the lower of ours and the server's ([MS-CSSP] 3.1.5.1): it
        // decides the shape of the public-key binding below (v5+ nonce hash vs the v2-4 raw key).
        int negotiated = Math.Min(CredSspVersion, challengeReq.Version > 0 ? challengeReq.Version : 2);

        // 2) AUTHENTICATE + pubKeyAuth. The NTLM AUTHENTICATE carries its own MIC; the pubKeyAuth is
        // the first sealed message (sequence 0), and the TSCredentials are sealed next (sequence 1).
        var authenticate = ntlm.ProcessChallenge(challengeReq.NegoToken);

        // pubKeyAuth: CredSSP v5+ binds the TLS server key via SHA256(magic || nonce || pubKey)
        // (CVE-2018-0886); v2-4 sends the raw public key. Sealed with the NTLM context (consumes seq 0).
        byte[]? clientNonce = null;
        byte[] clientPubKeyPlain;
        if (negotiated >= 5)
        {
            clientNonce = new byte[32];
            RandomNumberGenerator.Fill(clientNonce);
            clientPubKeyPlain = HashMagic("CredSSP Client-To-Server Binding Hash\0", clientNonce, _serverPublicKey);
        }
        else
        {
            clientPubKeyPlain = _serverPublicKey;
        }
        var (sealedPubKey, pubKeySig) = ntlm.SealAndSign(clientPubKeyPlain);
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
            return new CredSspResult(false, DescribeServerError(ec));
        if (serverResp.PubKeyAuth == null)
            return new CredSspResult(false, "CredSSP: server did not confirm the public key (auth rejected)");

        // VERIFY the server's confirmation ([MS-CSSP] 3.1.5 step 5). This is the check that makes it safe
        // to accept the host's (self-signed) TLS certificate: an active man-in-the-middle can forward the
        // NTLM exchange, but it cannot seal the Server-To-Client binding of the key WE saw without the
        // session key, and the session key requires the password. Until 0.6.34 only the presence of the
        // field was checked, so that protection was not actually in place.
        var serverPlain = ntlm.UnsealAndVerify(serverResp.PubKeyAuth);
        if (serverPlain == null)
            return new CredSspResult(false, "CredSSP: the server's public-key confirmation failed verification (possible man-in-the-middle)");
        var expected = negotiated >= 5
            ? HashMagic("CredSSP Server-To-Client Binding Hash\0", clientNonce!, _serverPublicKey)
            : PublicKeyPlusOne(_serverPublicKey);
        if (!CryptographicOperations.FixedTimeEquals(serverPlain, expected))
            return new CredSspResult(false, "CredSSP: the server's public-key confirmation does not match its TLS certificate (possible man-in-the-middle)");

        // 3) Send sealed TSCredentials (consumes seq 1).
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

    // CredSSP v2-4 server confirmation: the public key with its first byte incremented ([MS-CSSP] 3.1.5).
    private static byte[] PublicKeyPlusOne(byte[] pubKey)
    {
        var r = (byte[])pubKey.Clone();
        if (r.Length > 0) r[0]++;
        return r;
    }

    /// <summary>
    /// Turn the TSRequest <c>errorCode</c> (an NTSTATUS / SEC_E_* code, [MS-CSSP] 2.2.1) into something a
    /// user can act on. Every failure used to read "bad credentials or NLA refused", so a locked-out,
    /// expired or disabled account was indistinguishable from a typo.
    /// </summary>
    private static string DescribeServerError(uint code) => code switch
    {
        0xC000006D => "Sign-in failed: wrong username or password.",
        0xC000006A => "Sign-in failed: wrong password.",
        0xC0000064 => "Sign-in failed: the account does not exist on this desktop.",
        0xC0000234 => "Sign-in failed: the account is locked out. Wait or ask an administrator to unlock it.",
        0xC0000071 => "Sign-in failed: the password has expired and must be changed.",
        0xC0000224 => "Sign-in failed: the password must be changed before the first sign-in.",
        0xC0000072 => "Sign-in failed: the account is disabled.",
        0xC0000193 => "Sign-in failed: the account has expired.",
        0xC000006E => "Sign-in failed: an account restriction (e.g. blank password not allowed) prevents this sign-in.",
        0xC000006F => "Sign-in failed: sign-in is not permitted at this time of day.",
        0xC0000070 => "Sign-in failed: this account may not sign in from this computer.",
        0xC000015B => "Sign-in failed: the account is not allowed to sign in remotely (Remote Desktop Users membership or the 'Allow log on through Remote Desktop Services' right is missing).",
        0xC0000133 => "Sign-in failed: the clock difference between the gateway and the domain is too large.",
        0xC000018B or 0xC000018C or 0xC000018D => "Sign-in failed: the desktop could not reach its domain controller to validate the account.",
        0x80090308 => "NLA rejected the authentication token (SEC_E_INVALID_TOKEN).",
        0x8009030E => "NLA rejected the credentials (SEC_E_NO_CREDENTIALS).",
        0x80090302 => "The desktop does not support the authentication method the gateway offered (NTLM may be disabled — Kerberos is not yet supported).",
        _ => $"NLA authentication failed (server error 0x{code:X8}).",
    };

    // ---- TSRequest (ASN.1 DER) ----

    private sealed class TsRequest
    {
        public int Version;
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

        // [0] version (required). The server's version decides the public-key binding form.
        var v = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        if (v.TryReadInt32(out int version)) req.Version = version; else v.ReadInteger();

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
