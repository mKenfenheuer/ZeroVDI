using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;

namespace KSol.RDPGateway.RDP;

/// <summary>Outcome of the server-side CredSSP handshake.</summary>
/// <param name="Success">Whether NLA completed and the client proved knowledge of the password.</param>
/// <param name="Error">A human-readable failure reason when <paramref name="Success"/> is false.</param>
/// <param name="DelegatedDomain">The domain from the TSCredentials the client delegated (if any).</param>
/// <param name="DelegatedUser">The username from the delegated TSCredentials (if any).</param>
/// <param name="DelegatedPassword">The password from the delegated TSCredentials (if any).</param>
public sealed record CredSspServerResult(
    bool Success, string? Error,
    string? DelegatedDomain = null, string? DelegatedUser = null, string? DelegatedPassword = null);

/// <summary>
/// Server side of CredSSP ([MS-CSSP]) for the gateway's RDP NLA man-in-the-middle. It is the mirror of
/// <see cref="CredSspClient"/>: after the gateway has accepted the client's X.224 <c>PROTOCOL_HYBRID</c>
/// request and completed a TLS <i>server</i> handshake (presenting the gateway certificate), this drives
/// the CredSSP exchange in the <b>acceptor</b> role over that TLS stream, validating the client's NTLMv2
/// response against a known NT hash (the gateway-login credentials the user already holds) and verifying
/// the public-key binding so the client is talking to us and not a spoof.
///
/// On success the client has authenticated and delegated its <c>TSCredentials</c> to us; the relay then
/// runs <see cref="CredSspClient"/> toward the real host with the <i>stored</i> host credentials. The
/// delegated client credentials are returned so the caller can decide whether to forward them or swap
/// them — for SSO they are discarded in favour of the stored host creds.
///
/// NTLMv2 only (the same fallback path <see cref="CredSspClient"/> implements); we advertise NTLM in the
/// challenge and never negotiate Kerberos as an acceptor here.
/// </summary>
public sealed class CredSspServer
{
    private const int CredSspVersion = 6;

    private readonly Stream _tls;
    private readonly byte[] _serverPublicKey;   // raw SubjectPublicKey of the gateway TLS cert
    private readonly byte[] _expectedNtHash;     // NT hash of the credentials the client must prove
    private readonly ILogger _logger;

    public CredSspServer(Stream tlsStream, byte[] serverPublicKey, byte[] expectedNtHash, ILogger logger)
    {
        _tls = tlsStream;
        _serverPublicKey = serverPublicKey;
        _expectedNtHash = expectedNtHash;
        _logger = logger;
    }

    public async Task<CredSspServerResult> AuthenticateAsync(CancellationToken ct)
    {
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
            _logger.LogWarning(ex, "CredSSP server: handshake failed");
            return new CredSspServerResult(false, ex.Message);
        }
    }

    private async Task<CredSspServerResult> AuthenticateNtlmAsync(CancellationToken ct)
    {
        // 1) Read the client's NTLM NEGOTIATE (Type 1) carried in negoTokens.
        var negReq = await ReadTsRequestAsync(ct);
        if (negReq.NegoToken == null)
            return new CredSspServerResult(false, "CredSSP server: no NTLM negotiate token");
        var negotiate = Ntlm.TryUnwrapSpnego(negReq.NegoToken);
        if (Ntlm.GetMessageType(negotiate) != Ntlm.MessageType.Negotiate)
            return new CredSspServerResult(false, "CredSSP server: first token was not an NTLM negotiate");
        // Negotiate the CredSSP version: honour the client's, capped at what we implement (6). v5+ uses
        // the client-nonce public-key binding (CVE-2018-0886); v2–4 uses the legacy pubKey+1 scheme.
        int version = Math.Min(negReq.Version <= 0 ? CredSspVersion : negReq.Version, CredSspVersion);
        _logger.LogInformation("CredSSP server: client version={ClientVer}, using version={Ver}", negReq.Version, version);

        // 2) Issue an NTLM CHALLENGE (Type 2) with a fresh server challenge.
        var serverChallenge = Ntlm.NewServerChallenge();
        uint clientFlags = Ntlm.GetNegotiateFlags(negotiate);
        var challenge = Ntlm.BuildChallenge(serverChallenge, clientFlags);
        await WriteTsRequestAsync(version, new TsRequest { NegoToken = challenge }, ct);

        // 3) Read the client's AUTHENTICATE (Type 3) + pubKeyAuth (sealed pubkey, seq 0).
        var authReq = await ReadTsRequestAsync(ct);
        if (authReq.NegoToken == null)
            return new CredSspServerResult(false, "CredSSP server: no NTLM authenticate token");
        var authenticate = Ntlm.TryUnwrapSpnego(authReq.NegoToken);
        var t3 = Ntlm.ParseType3(authenticate);
        if (t3 == null)
            return new CredSspServerResult(false, "CredSSP server: malformed NTLM authenticate");

        // Verify the NTLMv2 response against the expected NT hash. This proves the client knows the
        // password without us ever transmitting it.
        if (!Ntlm.VerifyNtlmV2(_expectedNtHash, t3.User, t3.Domain, serverChallenge, t3.NtChallengeResponse))
        {
            _logger.LogWarning("CredSSP server: NTLMv2 verification failed for user='{User}' domain='{Domain}'", t3.User, t3.Domain);
            return new CredSspServerResult(false, "bad credentials");
        }
        _logger.LogInformation("CredSSP server: NTLMv2 verified for user='{User}' domain='{Domain}'", t3.User, t3.Domain);

        // Reconstruct the session security context the client derived, so we can verify the sealed
        // pubKeyAuth and seal our response + decrypt the delegated credentials.
        var sec = NtlmServerSecurity.Derive(_expectedNtHash, t3, serverChallenge);

        // 4) Verify the client's pubKeyAuth: signature(16) || sealed(boundKey, seq 0).
        if (authReq.PubKeyAuth == null || authReq.PubKeyAuth.Length < 16)
            return new CredSspServerResult(false, "CredSSP server: missing pubKeyAuth");
        var clientSig = authReq.PubKeyAuth.AsSpan(0, 16).ToArray();
        var clientSealed = authReq.PubKeyAuth.AsSpan(16).ToArray();
        var clientBoundKey = sec.UnsealAndVerifyFromClient(clientSealed, clientSig);
        if (clientBoundKey == null)
            return new CredSspServerResult(false, "CredSSP server: pubKeyAuth signature mismatch");

        byte[] serverPubKeyResponse;
        if (version >= 5)
        {
            // v5+: client bound SHA256(magic || nonce || pubKey); server responds with the
            // server-magic hash over the same nonce ([MS-CSSP] 3.1.5, CVE-2018-0886).
            if (authReq.ClientNonce == null || authReq.ClientNonce.Length != 32)
                return new CredSspServerResult(false, "CredSSP server: missing client nonce (v5+)");
            var expected = HashMagic("CredSSP Client-To-Server Binding Hash\0", authReq.ClientNonce, _serverPublicKey);
            if (!CryptographicOperations.FixedTimeEquals(clientBoundKey, expected))
                return new CredSspServerResult(false, "CredSSP server: public key binding mismatch (possible MITM)");
            serverPubKeyResponse = HashMagic("CredSSP Server-To-Client Binding Hash\0", authReq.ClientNonce, _serverPublicKey);
        }
        else
        {
            // Legacy (v2–4): client sent the raw public key; server returns it with the first byte
            // incremented, sealed in the server direction.
            if (!clientBoundKey.AsSpan().SequenceEqual(_serverPublicKey))
                return new CredSspServerResult(false, "CredSSP server: public key mismatch (possible MITM)");
            serverPubKeyResponse = (byte[])_serverPublicKey.Clone();
            serverPubKeyResponse[0]++;
        }

        // 5) Respond with our server-to-client pubKeyAuth, sealed with the server context (seq 0).
        var (serverSealed, serverSig) = sec.SealAndSignToClient(serverPubKeyResponse);
        await WriteTsRequestAsync(version, new TsRequest { PubKeyAuth = Concat(serverSig, serverSealed) }, ct);
        _logger.LogInformation("CredSSP server: sent server pubKeyAuth; awaiting delegated credentials");

        // 6) Read the client's sealed TSCredentials (seq 1, client direction).
        var credReq = await ReadTsRequestAsync(ct);
        if (credReq.AuthInfo == null || credReq.AuthInfo.Length < 16)
            return new CredSspServerResult(false, "CredSSP server: missing authInfo (credentials)");
        var credsSig = credReq.AuthInfo.AsSpan(0, 16).ToArray();
        var credsSealed = credReq.AuthInfo.AsSpan(16).ToArray();
        var tsCreds = sec.UnsealAndVerifyFromClient(credsSealed, credsSig);
        if (tsCreds == null)
            return new CredSspServerResult(false, "CredSSP server: credentials signature mismatch");

        var (domain, user, password) = DecodeTsPasswordCredentials(tsCreds);
        _logger.LogInformation("CredSSP server: NTLMv2 handshake completed for {User}", t3.User);
        return new CredSspServerResult(true, null, domain, user, password);
    }

    // ---- CredSSP v6 public-key binding hash ([MS-CSSP] 3.1.5): SHA256(magic || nonce || pubkey). ----
    private static byte[] HashMagic(string magicAscii, byte[] nonce, byte[] pubKey)
    {
        var magic = Encoding.ASCII.GetBytes(magicAscii);
        return SHA256.HashData(Concat(magic, nonce, pubKey));
    }

    // ============================ TSRequest (ASN.1 DER) ============================
    // Mirror of CredSspClient's codec; kept self-contained so the working client stays untouched.

    private sealed class TsRequest
    {
        public int Version;
        public byte[]? NegoToken;
        public byte[]? AuthInfo;
        public byte[]? PubKeyAuth;
        public uint? ErrorCode;
        public byte[]? ClientNonce;
    }

    private async Task WriteTsRequestAsync(int version, TsRequest req, CancellationToken ct)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                w.WriteInteger(version);

            if (req.NegoToken != null)
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                using (w.PushSequence())
                using (w.PushSequence())
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    w.WriteOctetString(req.NegoToken);
            }

            if (req.AuthInfo != null)
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    w.WriteOctetString(req.AuthInfo);

            if (req.PubKeyAuth != null)
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
                    w.WriteOctetString(req.PubKeyAuth);

            if (req.ErrorCode is { } code)
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4)))
                    w.WriteInteger(code);

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

        var v = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        req.Version = (int)v.ReadInteger();

        while (seq.HasData)
        {
            var tag = seq.PeekTag();
            if (tag.TagClass != TagClass.ContextSpecific) { seq.ReadEncodedValue(); continue; }
            switch (tag.TagValue)
            {
                case 1:
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
                    var c = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 4));
                    var raw = c.ReadIntegerBytes().ToArray();
                    uint code = 0;
                    foreach (var b in raw) code = (code << 8) | b;
                    req.ErrorCode = code;
                    break;
                }
                case 5:
                {
                    var c = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 5));
                    req.ClientNonce = c.ReadOctetString();
                    break;
                }
                default:
                    seq.ReadEncodedValue();
                    break;
            }
        }
        return req;
    }

    private byte[] _carry = Array.Empty<byte>();

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
            if (n == 0) throw new IOException("CredSSP server: unexpected end of TLS stream");
            acc.Write(chunk, 0, n);
        }
    }

    private static bool TryGetDerLength(byte[] buf, int count, out int total)
    {
        total = 0;
        if (count < 2) return false;
        int lenByte = buf[1];
        if ((lenByte & 0x80) == 0) { total = 2 + lenByte; return true; }
        int numLenBytes = lenByte & 0x7f;
        if (count < 2 + numLenBytes) return false;
        int length = 0;
        for (int i = 0; i < numLenBytes; i++) length = (length << 8) | buf[2 + i];
        total = 2 + numLenBytes + length;
        return true;
    }

    // ---- TSCredentials / TSPasswordCreds ([MS-CSSP] 2.2.1.2) ----

    private static (string Domain, string User, string Password) DecodeTsPasswordCredentials(byte[] tsCreds)
    {
        var outer = new AsnReader(tsCreds, AsnEncodingRules.DER);
        var seq = outer.ReadSequence();
        var credTypeSeq = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        credTypeSeq.ReadInteger(); // credType (1 = password); ignored
        var credsSeq = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1));
        var tsPassword = credsSeq.ReadOctetString();

        var pwOuter = new AsnReader(tsPassword, AsnEncodingRules.DER);
        var pwSeq = pwOuter.ReadSequence();
        string ReadUtf16(int ctx)
        {
            var c = pwSeq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, ctx));
            return Encoding.Unicode.GetString(c.ReadOctetString());
        }
        var domain = ReadUtf16(0);
        var user = ReadUtf16(1);
        var password = ReadUtf16(2);
        return (domain, user, password);
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

/// <summary>
/// The NTLMv2 session-security context reconstructed by the CredSSP <i>acceptor</i> from a verified
/// AUTHENTICATE message. Derives the same exported session key the client derived (by decrypting the
/// client's EncryptedRandomSessionKey with the NTLMv2 key-exchange key), then the directional
/// sign/seal keys ([MS-NLMP] 3.4.5.2). Naming follows the client's perspective: "client" keys protect
/// client→server messages, "server" keys protect server→client. As the acceptor we <b>seal with the
/// server keys</b> and <b>verify/unseal with the client keys</b> — the inverse of <see cref="NtlmClient"/>.
/// </summary>
internal sealed class NtlmServerSecurity
{
    private readonly byte[] _clientSigningKey;
    private readonly byte[] _serverSigningKey;
    private readonly Rc4 _clientSealing;   // decrypts client→server
    private readonly Rc4 _serverSealing;   // encrypts server→client
    private uint _serverSeqNum;            // our outbound sequence
    private uint _clientSeqNum;            // expected inbound sequence

    private NtlmServerSecurity(byte[] exportedSessionKey)
    {
        _clientSigningKey = Md5Concat(exportedSessionKey,
            "session key to client-to-server signing key magic constant\0");
        _serverSigningKey = Md5Concat(exportedSessionKey,
            "session key to server-to-client signing key magic constant\0");
        var clientSealKey = Md5Concat(exportedSessionKey,
            "session key to client-to-server sealing key magic constant\0");
        var serverSealKey = Md5Concat(exportedSessionKey,
            "session key to server-to-client sealing key magic constant\0");
        _clientSealing = new Rc4(clientSealKey);
        _serverSealing = new Rc4(serverSealKey);
    }

    /// <summary>
    /// Reconstructs the context from the verified Type 3 message. Recomputes NTOWFv2 from the NT hash
    /// and the username/domain the client claimed, derives the SessionBaseKey from the NTProofStr in
    /// the response, and decrypts the EncryptedRandomSessionKey to recover the exported session key.
    /// </summary>
    public static NtlmServerSecurity Derive(byte[] ntHash, Ntlm.Type3 t3, byte[] serverChallenge)
    {
        // NTOWFv2 = HMAC-MD5(NTHash, UPPER(user) || domain) in UTF-16LE.
        var responseKey = AuthCrypto.HmacMd5(ntHash,
            Encoding.Unicode.GetBytes(t3.User.ToUpperInvariant() + t3.Domain));
        // NTProofStr is the first 16 bytes of the NtChallengeResponse.
        var ntProof = t3.NtChallengeResponse.AsSpan(0, 16).ToArray();
        // SessionBaseKey = HMAC-MD5(responseKey, NTProofStr); KeyExchangeKey == SessionBaseKey for NTLMv2.
        var keyExchangeKey = AuthCrypto.HmacMd5(responseKey, ntProof);

        // With KEY_EXCH the client sent EncryptedRandomSessionKey = RC4(KeyExchangeKey, ExportedSessionKey).
        // Decrypt it (RC4 is symmetric) to recover the exported session key. If the client did not use
        // key exchange, the exported key equals the key-exchange key.
        byte[] exportedSessionKey;
        if (t3.EncryptedRandomSessionKey.Length == 16)
        {
            using var rc4 = new Rc4(keyExchangeKey);
            exportedSessionKey = rc4.Transform(t3.EncryptedRandomSessionKey);
        }
        else
        {
            exportedSessionKey = keyExchangeKey;
        }
        return new NtlmServerSecurity(exportedSessionKey);
    }

    /// <summary>Seals + signs a server→client message ([MS-NLMP] 3.4.4.1, extended session security).</summary>
    public (byte[] Sealed, byte[] Signature) SealAndSignToClient(byte[] plaintext)
    {
        uint seq = _serverSeqNum++;
        var seqBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seqBytes, seq);
        var hmac = AuthCrypto.HmacMd5(_serverSigningKey, Concat(seqBytes, plaintext));
        var sealedData = _serverSealing.Transform(plaintext);
        var checksum = _serverSealing.Transform(hmac.AsSpan(0, 8).ToArray());
        var sig = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(0, 4), 1);
        checksum.CopyTo(sig.AsSpan(4, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(12, 4), seq);
        return (sealedData, sig);
    }

    /// <summary>
    /// Unseals a client→server message and verifies its signature, returning the plaintext or null if
    /// the signature does not match. Advances the inbound keystream identically to the client's outbound.
    /// </summary>
    public byte[]? UnsealAndVerifyFromClient(byte[] sealedData, byte[] signature)
    {
        uint seq = _clientSeqNum++;
        var plaintext = _clientSealing.Transform(sealedData);
        var seqBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seqBytes, seq);
        var hmac = AuthCrypto.HmacMd5(_clientSigningKey, Concat(seqBytes, plaintext));
        var checksum = _clientSealing.Transform(hmac.AsSpan(0, 8).ToArray());

        var expectedSig = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(expectedSig.AsSpan(0, 4), 1);
        checksum.CopyTo(expectedSig.AsSpan(4, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(expectedSig.AsSpan(12, 4), seq);

        return CryptographicOperations.FixedTimeEquals(expectedSig, signature) ? plaintext : null;
    }

    private static byte[] Md5Concat(byte[] key, string magicAscii)
        => MD5.HashData(Concat(key, Encoding.ASCII.GetBytes(magicAscii)));

    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var r = new byte[len];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }
}
