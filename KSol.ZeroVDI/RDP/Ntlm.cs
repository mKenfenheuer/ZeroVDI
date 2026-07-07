using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Minimal, self-contained NTLM (NTLMv2) server-side message handling, sufficient to validate a
/// client's NTLMv2 response against a stored NT hash without a Windows domain. Implements parsing
/// of the Type 1 (Negotiate) and Type 3 (Authenticate) messages and generation of the Type 2
/// (Challenge) message, plus NTLMv2 response verification per [MS-NLMP].
///
/// SPNEGO/Negotiate wrappers are unwrapped to their inner NTLM token by <see cref="TryUnwrapSpnego"/>.
/// </summary>
public static class Ntlm
{
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("NTLMSSP\0");

    public enum MessageType { Unknown = 0, Negotiate = 1, Challenge = 2, Authenticate = 3 }

    public static MessageType GetMessageType(byte[] msg)
    {
        if (msg.Length < 12 || !msg.AsSpan(0, 8).SequenceEqual(Signature))
            return MessageType.Unknown;
        return (MessageType)BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(8, 4));
    }

    // NTLM NEGOTIATE flag bits ([MS-NLMP] 2.2.2.5).
    private const uint NEGOTIATE_UNICODE                = 0x00000001;
    private const uint REQUEST_TARGET                   = 0x00000004;
    private const uint NEGOTIATE_SIGN                   = 0x00000010;
    private const uint NEGOTIATE_SEAL                   = 0x00000020;
    private const uint NEGOTIATE_NTLM                   = 0x00000200;
    private const uint NEGOTIATE_ALWAYS_SIGN            = 0x00008000;
    private const uint NEGOTIATE_EXTENDED_SESSIONSECURITY= 0x00080000;
    private const uint TARGET_TYPE_DOMAIN               = 0x00010000;
    private const uint NEGOTIATE_TARGET_INFO            = 0x00800000;
    private const uint NEGOTIATE_VERSION                = 0x02000000;
    private const uint NEGOTIATE_128                    = 0x20000000;
    private const uint NEGOTIATE_KEY_EXCH               = 0x40000000;
    private const uint NEGOTIATE_56                     = 0x80000000;

    /// <summary>
    /// Extracts the NegotiateFlags from a Type 1 (Negotiate) message, or 0 if it can't be read.
    /// </summary>
    public static uint GetNegotiateFlags(byte[] type1)
    {
        if (GetMessageType(type1) != MessageType.Negotiate || type1.Length < 16)
            return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(type1.AsSpan(12, 4));
    }

    /// <summary>
    /// Builds an NTLM Type 2 (Challenge) message carrying the given 8-byte server challenge.
    /// <paramref name="clientFlags"/> are the flags from the client's Type 1, used to mirror the
    /// session-security and key options the client requested (Heimdal/macOS GSS-NTLM aborts silently
    /// if the challenge omits NTLM2 extended session security, so we always assert it).
    /// </summary>
    public static byte[] BuildChallenge(byte[] serverChallenge, uint clientFlags = 0, string targetName = "KSOL")
    {
        var target = Encoding.Unicode.GetBytes(targetName);

        // Target info (AV pairs). NTLMv2 requires at least NbDomain + NbComputer; macOS GSS-NTLM
        // also wants a timestamp. Terminate with EOL.
        var timestamp = (DateTime.UtcNow.ToFileTimeUtc());
        var tsBytes = BitConverter.GetBytes(timestamp); // little-endian FILETIME
        using var avMem = new MemoryStream();
        WriteAvPair(avMem, 0x0002, target);              // MsvAvNbDomainName
        WriteAvPair(avMem, 0x0001, target);              // MsvAvNbComputerName
        WriteAvPair(avMem, 0x0007, tsBytes);             // MsvAvTimestamp
        WriteAvPair(avMem, 0x0000, Array.Empty<byte>()); // MsvAvEOL
        var targetInfo = avMem.ToArray();

        // Base flags we always set, OR'd with the session-security/size flags the client asked for.
        uint flags = NEGOTIATE_UNICODE | REQUEST_TARGET | NEGOTIATE_NTLM | TARGET_TYPE_DOMAIN
            | NEGOTIATE_TARGET_INFO | NEGOTIATE_VERSION | NEGOTIATE_EXTENDED_SESSIONSECURITY;
        // Mirror the session-security/signing/size flags the client requested. macOS GSS-NTLM sends
        // a Type 1 with SIGN/SEAL/ALWAYS_SIGN and refuses to emit a Type 3 if the challenge doesn't
        // confirm them, so we echo them back rather than silently dropping them.
        flags |= clientFlags & (NEGOTIATE_SIGN | NEGOTIATE_SEAL | NEGOTIATE_ALWAYS_SIGN
            | NEGOTIATE_56 | NEGOTIATE_128 | NEGOTIATE_KEY_EXCH);
        // Ensure 56/128 are advertised even if the (sometimes minimal) Type 1 omitted them.
        flags |= NEGOTIATE_56 | NEGOTIATE_128;

        // Header is 56 bytes when the 8-byte Version field is present (NEGOTIATE_VERSION).
        const int headerSize = 56;
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Signature);
        w.Write((uint)2); // Type 2

        // TargetName fields (len, maxlen, offset)
        w.Write((ushort)target.Length);
        w.Write((ushort)target.Length);
        w.Write((uint)headerSize);
        // Flags
        w.Write(flags);
        // Server challenge (8 bytes)
        w.Write(serverChallenge);
        // Reserved (8 bytes)
        w.Write(0L);
        // TargetInfo fields (len, maxlen, offset)
        w.Write((ushort)targetInfo.Length);
        w.Write((ushort)targetInfo.Length);
        w.Write((uint)(headerSize + target.Length));
        // Version (8 bytes): 6.1.7601, NTLM revision 15 — a plausible Windows version.
        w.Write((byte)6); w.Write((byte)1);          // major.minor
        w.Write((ushort)7601);                        // build
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); // reserved
        w.Write((byte)15);                            // NTLM revision current

        // Payload
        w.Write(target);
        w.Write(targetInfo);
        return ms.ToArray();
    }

    private static void WriteAvPair(Stream s, ushort id, byte[] value)
    {
        Span<byte> hdr = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(hdr, id);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(2), (ushort)value.Length);
        s.Write(hdr);
        s.Write(value);
    }

    /// <summary>Parsed fields from a Type 3 (Authenticate) message needed to verify the response.</summary>
    public class Type3
    {
        public string Domain = "";
        public string User = "";
        public string Workstation = "";
        public byte[] NtChallengeResponse = Array.Empty<byte>();
        public byte[] LmChallengeResponse = Array.Empty<byte>();
        public byte[] EncryptedRandomSessionKey = Array.Empty<byte>();
    }

    public static Type3? ParseType3(byte[] msg)
    {
        if (GetMessageType(msg) != MessageType.Authenticate)
            return null;

        try
        {
            byte[] ReadField(int fieldOffset)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(fieldOffset, 2));
                int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(fieldOffset + 4, 4));
                if (len == 0 || off + len > msg.Length) return Array.Empty<byte>();
                return msg.AsSpan(off, len).ToArray();
            }

            var lm = ReadField(12);
            var nt = ReadField(20);
            var domain = ReadField(28);
            var user = ReadField(36);
            var workstation = ReadField(44);
            // EncryptedRandomSessionKeyFields at offset 52 (present when NEGOTIATE_KEY_EXCH was set).
            var sessionKey = msg.Length >= 60 ? ReadField(52) : Array.Empty<byte>();

            return new Type3
            {
                LmChallengeResponse = lm,
                NtChallengeResponse = nt,
                Domain = Encoding.Unicode.GetString(domain),
                User = Encoding.Unicode.GetString(user),
                Workstation = Encoding.Unicode.GetString(workstation),
                EncryptedRandomSessionKey = sessionKey,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Verifies an NTLMv2 challenge response against the user's NT hash and the server challenge.
    /// </summary>
    /// <param name="ntHash">The 16-byte NT hash (MD4 of UTF-16LE password).</param>
    /// <param name="user">The username the client authenticated as (Type3.User).</param>
    /// <param name="domain">The domain the client supplied (Type3.Domain).</param>
    /// <param name="serverChallenge">The 8-byte challenge sent in the Type 2 message.</param>
    /// <param name="ntResponse">The NtChallengeResponse from the Type 3 message.</param>
    public static bool VerifyNtlmV2(byte[] ntHash, string user, string domain, byte[] serverChallenge, byte[] ntResponse)
    {
        if (ntResponse.Length < 16)
            return false;

        // NTLMv2: NTProofStr = HMAC-MD5(ntlmv2Hash, serverChallenge || temp)
        // where temp is the remainder of the response after the 16-byte proof, and
        // ntlmv2Hash = HMAC-MD5(ntHash, UPPER(user) + domain) in UTF-16LE.
        var ntlmV2Hash = AuthCrypto.HmacMd5(
            ntHash,
            Encoding.Unicode.GetBytes(user.ToUpperInvariant() + domain));

        var proof = ntResponse.AsSpan(0, 16).ToArray();
        var temp = ntResponse.AsSpan(16).ToArray();

        var data = new byte[serverChallenge.Length + temp.Length];
        serverChallenge.CopyTo(data, 0);
        temp.CopyTo(data, serverChallenge.Length);

        var expected = AuthCrypto.HmacMd5(ntlmV2Hash, data);
        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }

    /// <summary>Generates a fresh 8-byte server challenge.</summary>
    public static byte[] NewServerChallenge()
    {
        var b = new byte[8];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>
    /// If the token is a SPNEGO (GSS-API) wrapper, extracts the inner NTLMSSP token; otherwise
    /// returns the token unchanged. Handles the common initiator token shapes RDP clients send.
    /// </summary>
    public static byte[] TryUnwrapSpnego(byte[] token)
    {
        // Raw NTLM already.
        if (token.Length >= 8 && token.AsSpan(0, 8).SequenceEqual(Signature))
            return token;

        // Search for the "NTLMSSP\0" signature within the SPNEGO/GSS DER blob and return from there.
        for (int i = 0; i + 8 <= token.Length; i++)
        {
            if (token.AsSpan(i, 8).SequenceEqual(Signature))
                return token.AsSpan(i).ToArray();
        }
        return token;
    }
}
