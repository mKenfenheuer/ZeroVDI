using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Initiator (client) side of NTLMv2 with NTLM2 extended session security, sufficient to drive the
/// NTLM fallback path of a CredSSP handshake against a Windows RDP host that requires NLA.
///
/// The repo already has the <i>acceptor</i> side in <see cref="Ntlm"/> (build challenge, verify
/// response — used by the RDWeb feed). This is the opposite direction: build the NEGOTIATE (Type 1)
/// and AUTHENTICATE (Type 3) messages, compute the NTLMv2 response and the exported session key, and
/// expose the signing/sealing keys + RC4 handles CredSSP needs to seal the public key and the
/// delegated credentials ([MS-NLMP], [MS-CSSP]).
///
/// Crypto primitives are reused from <see cref="AuthCrypto"/> (MD4 NT-hash, HMAC-MD5). RC4 is part
/// of the NTLM SEAL definition; it is not a chosen-down cipher and is only used on this fallback
/// path — the preferred path is Kerberos/AES (see <see cref="CredSspClient"/>).
/// </summary>
public sealed class NtlmClient
{
    // [MS-NLMP] 2.2.2.5 NEGOTIATE flags (subset we assert as a client).
    private const uint NEGOTIATE_UNICODE                 = 0x00000001;
    private const uint REQUEST_TARGET                    = 0x00000004;
    private const uint NEGOTIATE_SIGN                    = 0x00000010;
    private const uint NEGOTIATE_SEAL                    = 0x00000020;
    private const uint NEGOTIATE_NTLM                    = 0x00000200;
    private const uint NEGOTIATE_OEM_WORKSTATION_SUPPLIED = 0x00002000;
    private const uint NEGOTIATE_ALWAYS_SIGN             = 0x00008000;
    private const uint NEGOTIATE_EXTENDED_SESSIONSECURITY = 0x00080000;
    private const uint NEGOTIATE_TARGET_INFO             = 0x00800000;
    private const uint NEGOTIATE_VERSION                 = 0x02000000;
    private const uint NEGOTIATE_128                     = 0x20000000;
    private const uint NEGOTIATE_KEY_EXCH                = 0x40000000;
    private const uint NEGOTIATE_56                      = 0x80000000;

    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("NTLMSSP\0");

    private readonly string _domain;
    private readonly string _user;
    private readonly string _password;
    private readonly string? _servicePrincipalName;

    // Flags asserted in the AUTHENTICATE (Type 3). Includes WORKSTATION_SUPPLIED because we send a
    // workstation name; this matches what Windows/FreeRDP send and what NLA servers expect.
    private readonly uint _negotiateFlags =
        NEGOTIATE_UNICODE | REQUEST_TARGET | NEGOTIATE_SIGN | NEGOTIATE_SEAL | NEGOTIATE_NTLM |
        NEGOTIATE_OEM_WORKSTATION_SUPPLIED | NEGOTIATE_ALWAYS_SIGN | NEGOTIATE_EXTENDED_SESSIONSECURITY |
        NEGOTIATE_TARGET_INFO | NEGOTIATE_VERSION | NEGOTIATE_128 | NEGOTIATE_KEY_EXCH | NEGOTIATE_56;

    /// <summary>The 16-byte exported session key, available after <see cref="ProcessChallenge"/>.</summary>
    public byte[]? ExportedSessionKey { get; private set; }

    // Directional signing/sealing keys + RC4 states ([MS-NLMP] 3.4.5.x). "Client" = our outbound
    // ("ClientToServer"); "Server" = inbound ("ServerToClient").
    public byte[]? ClientSigningKey { get; private set; }
    public byte[]? ServerSigningKey { get; private set; }
    public Rc4? ClientSealing { get; private set; }
    public Rc4? ServerSealing { get; private set; }

    private uint _clientSeqNum;

    /// <param name="servicePrincipalName">
    /// The target SPN (e.g. <c>TERMSRV/host</c>) to advertise as the MsvAvTargetName AV pair. NLA
    /// servers validate this; omitting it causes the AUTHENTICATE to be rejected.
    /// </param>
    public NtlmClient(string user, string password, string domain, string? servicePrincipalName = null)
    {
        _user = user;
        _password = password;
        _domain = domain ?? string.Empty;
        _servicePrincipalName = servicePrincipalName;
    }

    /// <summary>Builds the NTLM NEGOTIATE (Type 1) message.</summary>
    public byte[] CreateNegotiate()
    {
        // No domain/workstation payload; offsets point just past the fixed header.
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Signature);
        w.Write((uint)1); // Type 1
        w.Write(_negotiateFlags);
        // DomainNameFields (len, maxlen, offset) — empty.
        w.Write((ushort)0); w.Write((ushort)0); w.Write((uint)40);
        // WorkstationFields (len, maxlen, offset) — empty.
        w.Write((ushort)0); w.Write((ushort)0); w.Write((uint)40);
        // Version (8 bytes): 6.1.7601 / NTLM revision 15.
        w.Write((byte)6); w.Write((byte)1); w.Write((ushort)7601);
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)15);
        return ms.ToArray();
    }

    /// <summary>
    /// Processes the server CHALLENGE (Type 2) and produces the AUTHENTICATE (Type 3) message. Also
    /// derives <see cref="ExportedSessionKey"/> and the directional signing/sealing material.
    /// </summary>
    /// <param name="challengeMessage">The full Type 2 message bytes.</param>
    public byte[] ProcessChallenge(byte[] challengeMessage)
    {
        if (Ntlm.GetMessageType(challengeMessage) != Ntlm.MessageType.Challenge)
            throw new InvalidOperationException("expected NTLM CHALLENGE (Type 2)");

        var serverChallenge = challengeMessage.AsSpan(24, 8).ToArray();
        uint serverFlags = BinaryPrimitives.ReadUInt32LittleEndian(challengeMessage.AsSpan(20, 4));

        // TargetInfo (AV pairs) from the Type 2. We must echo it back in the NTLMv2 temp blob, but
        // with the MsvAvFlags pair carrying 0x02 (MIC present) so the server expects and validates the
        // MIC we include in the AUTHENTICATE ([MS-NLMP] 3.1.5.1.2 / 2.2.2.1).
        int tiLen = BinaryPrimitives.ReadUInt16LittleEndian(challengeMessage.AsSpan(40, 2));
        int tiOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(challengeMessage.AsSpan(44, 4));
        var serverTargetInfo = tiLen > 0 && tiOff + tiLen <= challengeMessage.Length
            ? challengeMessage.AsSpan(tiOff, tiLen).ToArray()
            : Array.Empty<byte>();
        var targetInfo = BuildResponseTargetInfo(serverTargetInfo);

        // NTOWFv2 = HMAC-MD5(NTHash, UPPER(user) || domain) in UTF-16LE.
        var ntHash = AuthCrypto.NtHashBytes(_password);
        var responseKey = AuthCrypto.HmacMd5(ntHash,
            Encoding.Unicode.GetBytes(_user.ToUpperInvariant() + _domain));

        var clientChallenge = new byte[8];
        RandomNumberGenerator.Fill(clientChallenge);

        // temp = Responserversion(1) HiResponserversion(1) Z(6) Timestamp(8) ClientChallenge(8) Z(4)
        //        TargetInfo Z(4)
        long timestamp = DateTime.UtcNow.ToFileTimeUtc();
        using var tempMs = new MemoryStream();
        var tw = new BinaryWriter(tempMs);
        tw.Write((byte)1); tw.Write((byte)1);
        tw.Write((ushort)0); tw.Write((uint)0);           // reserved (6 bytes total)
        tw.Write(timestamp);                               // 8-byte FILETIME
        tw.Write(clientChallenge);                         // 8 bytes
        tw.Write((uint)0);                                 // reserved
        tw.Write(targetInfo);
        tw.Write((uint)0);                                 // reserved
        var temp = tempMs.ToArray();

        // NTProofStr = HMAC-MD5(responseKey, serverChallenge || temp)
        var proofInput = new byte[serverChallenge.Length + temp.Length];
        serverChallenge.CopyTo(proofInput, 0);
        temp.CopyTo(proofInput, serverChallenge.Length);
        var ntProof = AuthCrypto.HmacMd5(responseKey, proofInput);

        var ntChallengeResponse = new byte[ntProof.Length + temp.Length];
        ntProof.CopyTo(ntChallengeResponse, 0);
        temp.CopyTo(ntChallengeResponse, ntProof.Length);

        // SessionBaseKey = HMAC-MD5(responseKey, NTProofStr).
        var sessionBaseKey = AuthCrypto.HmacMd5(responseKey, ntProof);

        // With NTLM2 session security + KEY_EXCH the client generates an exported session key and
        // sends it sealed with RC4(KeyExchangeKey). KeyExchangeKey == SessionBaseKey for NTLMv2.
        var keyExchangeKey = sessionBaseKey;
        var exportedSessionKey = new byte[16];
        RandomNumberGenerator.Fill(exportedSessionKey);
        byte[] encryptedRandomSessionKey;
        using (var rc4 = new Rc4(keyExchangeKey))
            encryptedRandomSessionKey = rc4.Transform(exportedSessionKey);

        ExportedSessionKey = exportedSessionKey;
        DeriveSignSealKeys(exportedSessionKey);

        // LMv2 response: for NTLMv2 we send an all-zero (24-byte) LM response with the client
        // challenge appended is acceptable; many servers accept Z(24). Use HMAC-based LMv2 for
        // correctness.
        var lmProof = AuthCrypto.HmacMd5(responseKey, Concat(serverChallenge, clientChallenge));
        var lmChallengeResponse = new byte[24];
        lmProof.CopyTo(lmChallengeResponse, 0);
        clientChallenge.CopyTo(lmChallengeResponse, 16);

        var type3 = BuildAuthenticate(lmChallengeResponse, ntChallengeResponse,
            encryptedRandomSessionKey, serverFlags, out int micOffset);

        // MIC = HMAC-MD5(ExportedSessionKey, Negotiate || Challenge || Authenticate[with MIC=0]).
        var mic = AuthCrypto.HmacMd5(exportedSessionKey,
            Concat(_lastNegotiate ?? Array.Empty<byte>(), challengeMessage, type3));
        Array.Copy(mic, 0, type3, micOffset, 16);
        return type3;
    }

    // AV_PAIR ids ([MS-NLMP] 2.2.2.1).
    private const ushort MsvAvEOL = 0x0000;
    private const ushort MsvAvFlags = 0x0006;
    private const ushort MsvAvChannelBindings = 0x000A;
    private const ushort MsvAvTargetName = 0x0009;

    /// <summary>
    /// Returns the server target-info AV_PAIR list augmented with the pairs an NLA/CredSSP server
    /// requires in the NTLMv2 response ([MS-NLMP] 3.1.5.1.2, [MS-CSSP]):
    /// <list type="bullet">
    /// <item>MsvAvFlags |= 0x02 (a MIC is present).</item>
    /// <item>MsvAvChannelBindings — 16 bytes, all zero when no TLS channel-binding token is used.</item>
    /// <item>MsvAvTargetName — the target SPN (UTF-16LE), e.g. <c>TERMSRV/host</c>.</item>
    /// </list>
    /// Omitting these makes the server reject AUTHENTICATE. Pairs are inserted just before the
    /// trailing MsvAvEOL; an existing MsvAvFlags is updated in place.
    /// </summary>
    private byte[] BuildResponseTargetInfo(byte[] serverTargetInfo)
    {
        using var outMs = new MemoryStream();
        bool wroteFlags = false;
        int i = 0;
        while (i + 4 <= serverTargetInfo.Length)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(serverTargetInfo.AsSpan(i, 2));
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(serverTargetInfo.AsSpan(i + 2, 2));
            if (i + 4 + len > serverTargetInfo.Length) break;
            var value = serverTargetInfo.AsSpan(i + 4, len).ToArray();
            i += 4 + len;

            if (id == MsvAvEOL)
            {
                if (!wroteFlags)
                {
                    WriteAv(outMs, MsvAvFlags, BitConverter.GetBytes((uint)0x00000002));
                    wroteFlags = true;
                }
                // Channel bindings: 16 zero bytes (no TLS channel-binding token bound).
                WriteAv(outMs, MsvAvChannelBindings, new byte[16]);
                // Target name: the SPN we are authenticating to.
                if (!string.IsNullOrEmpty(_servicePrincipalName))
                    WriteAv(outMs, MsvAvTargetName, Encoding.Unicode.GetBytes(_servicePrincipalName));
                WriteAv(outMs, MsvAvEOL, Array.Empty<byte>());
                break;
            }

            if (id == MsvAvFlags)
            {
                uint flags = len >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(value) : 0;
                flags |= 0x00000002;
                WriteAv(outMs, MsvAvFlags, BitConverter.GetBytes(flags));
                wroteFlags = true;
            }
            else
            {
                WriteAv(outMs, id, value);
            }
        }
        return outMs.ToArray();
    }

    private static void WriteAv(Stream s, ushort id, byte[] value)
    {
        Span<byte> hdr = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(hdr, id);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(2), (ushort)value.Length);
        s.Write(hdr);
        s.Write(value);
    }

    private byte[]? _lastNegotiate;

    /// <summary>
    /// Convenience that records the Negotiate message (needed for the MIC) and returns it.
    /// </summary>
    public byte[] CreateNegotiateTracked()
    {
        _lastNegotiate = CreateNegotiate();
        return _lastNegotiate;
    }

    private byte[] BuildAuthenticate(byte[] lm, byte[] nt, byte[] encryptedSessionKey,
        uint serverFlags, out int micOffset)
    {
        var domain = Encoding.Unicode.GetBytes(_domain);
        var user = Encoding.Unicode.GetBytes(_user);
        var workstation = Encoding.Unicode.GetBytes(Environment.MachineName);

        // Fixed header is 88 bytes when Version (8) + MIC (16) are present:
        //   8 sig + 4 type + 6*8 fields + 4 flags + 8 version + 16 MIC = 88.
        const int headerSize = 88;
        int off = headerSize;

        int lmOff = off; off += lm.Length;
        int ntOff = off; off += nt.Length;
        int domOff = off; off += domain.Length;
        int userOff = off; off += user.Length;
        int wsOff = off; off += workstation.Length;
        int keyOff = off; off += encryptedSessionKey.Length;

        var buf = new byte[off];
        using var ms = new MemoryStream(buf);
        var w = new BinaryWriter(ms);
        w.Write(Signature);
        w.Write((uint)3); // Type 3
        WriteField(w, lm.Length, lmOff);
        WriteField(w, nt.Length, ntOff);
        WriteField(w, domain.Length, domOff);
        WriteField(w, user.Length, userOff);
        WriteField(w, workstation.Length, wsOff);
        WriteField(w, encryptedSessionKey.Length, keyOff);
        // NegotiateFlags echoed (must keep UNICODE/NTLM/EXT_SESSION_SECURITY/KEY_EXCH/128/56).
        w.Write(_negotiateFlags);
        // Version (8 bytes).
        w.Write((byte)6); w.Write((byte)1); w.Write((ushort)7601);
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)15);
        micOffset = (int)ms.Position;
        w.Write(new byte[16]); // MIC placeholder (filled by caller)

        ms.Position = lmOff;  w.Write(lm);
        ms.Position = ntOff;  w.Write(nt);
        ms.Position = domOff; w.Write(domain);
        ms.Position = userOff; w.Write(user);
        ms.Position = wsOff;  w.Write(workstation);
        ms.Position = keyOff; w.Write(encryptedSessionKey);
        return buf;
    }

    private static void WriteField(BinaryWriter w, int len, int offset)
    {
        w.Write((ushort)len);
        w.Write((ushort)len);
        w.Write((uint)offset);
    }

    // [MS-NLMP] 3.4.5.2: derive directional sign/seal keys from the exported session key.
    private void DeriveSignSealKeys(byte[] exportedSessionKey)
    {
        ClientSigningKey = Md5Concat(exportedSessionKey,
            "session key to client-to-server signing key magic constant\0");
        ServerSigningKey = Md5Concat(exportedSessionKey,
            "session key to server-to-client signing key magic constant\0");
        var clientSealKey = Md5Concat(exportedSessionKey,
            "session key to client-to-server sealing key magic constant\0");
        var serverSealKey = Md5Concat(exportedSessionKey,
            "session key to server-to-client sealing key magic constant\0");
        ClientSealing = new Rc4(clientSealKey);
        ServerSealing = new Rc4(serverSealKey);
    }

    private static byte[] Md5Concat(byte[] key, string magicAscii)
        => MD5.HashData(Concat(key, Encoding.ASCII.GetBytes(magicAscii)));

    /// <summary>
    /// Produces the NTLM message-signature (16 bytes) for an outbound message and seals it ([MS-NLMP]
    /// 3.4.4.1 with extended session security). Used by CredSSP to sign+seal the pubKeyAuth and the
    /// TSCredentials it sends to the server.
    /// </summary>
    public (byte[] Sealed, byte[] Signature) SealAndSign(byte[] plaintext)
        => SignInternal(plaintext, seal: true);

    /// <summary>
    /// Produces the NTLM message-signature for an outbound message <b>without</b> encrypting it
    /// (GSS_GetMIC / MakeSignature). Used for the SPNEGO mechListMIC, which signs the mechTypes list
    /// but does not seal it. Like <see cref="SealAndSign"/> it consumes one sequence number and
    /// advances the sealing keystream by the checksum only (8 bytes), not by the message length.
    /// </summary>
    public byte[] Sign(byte[] plaintext) => SignInternal(plaintext, seal: false).Signature;

    private (byte[] Sealed, byte[] Signature) SignInternal(byte[] plaintext, bool seal)
    {
        if (ClientSealing == null || ClientSigningKey == null)
            throw new InvalidOperationException("sign/seal keys not derived");

        uint seq = _clientSeqNum++;
        // Checksum HMAC is computed over the PLAINTEXT in both the sign and seal cases.
        var seqBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seqBytes, seq);
        var hmac = AuthCrypto.HmacMd5(ClientSigningKey, Concat(seqBytes, plaintext));

        // With extended session security, Seal encrypts the message then the checksum (one keystream),
        // while Sign (GetMIC) encrypts only the checksum. Order matters because both sides advance the
        // RC4 keystream identically.
        var sealedData = seal ? ClientSealing.Transform(plaintext) : plaintext;
        var checksum = ClientSealing.Transform(hmac.AsSpan(0, 8).ToArray());

        // Signature: version(4)=1 || checksum(8) || seqnum(4).
        var sig = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(0, 4), 1);
        checksum.CopyTo(sig.AsSpan(4, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(12, 4), seq);
        return (sealedData, sig);
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
/// Minimal RC4 stream cipher. RC4 is required by the NTLM SEAL definition ([MS-NLMP]); it is used
/// only on the NTLM fallback path of CredSSP, never for the transport (which is TLS/AES) and never
/// when Kerberos/AES is available.
/// </summary>
public sealed class Rc4 : IDisposable
{
    private readonly byte[] _s = new byte[256];
    private int _i;
    private int _j;

    public Rc4(byte[] key)
    {
        for (int k = 0; k < 256; k++) _s[k] = (byte)k;
        int j = 0;
        for (int k = 0; k < 256; k++)
        {
            j = (j + _s[k] + key[k % key.Length]) & 0xff;
            (_s[k], _s[j]) = (_s[j], _s[k]);
        }
    }

    /// <summary>Transforms (en/decrypts — RC4 is symmetric) the buffer, advancing the keystream.</summary>
    public byte[] Transform(byte[] data)
    {
        var outp = new byte[data.Length];
        for (int n = 0; n < data.Length; n++)
        {
            _i = (_i + 1) & 0xff;
            _j = (_j + _s[_i]) & 0xff;
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
            var k = _s[(_s[_i] + _s[_j]) & 0xff];
            outp[n] = (byte)(data[n] ^ k);
        }
        return outp;
    }

    public void Dispose() => Array.Clear(_s);
}
