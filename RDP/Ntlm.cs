using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KSol.RDPGateway.RDP;

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

    /// <summary>
    /// Builds an NTLM Type 2 (Challenge) message carrying the given 8-byte server challenge.
    /// </summary>
    public static byte[] BuildChallenge(byte[] serverChallenge, string targetName = "KSOL")
    {
        var target = Encoding.Unicode.GetBytes(targetName);

        // Target info (AV pairs): NetBIOS domain name + EOL. Required for NTLMv2.
        using var avMem = new MemoryStream();
        WriteAvPair(avMem, 0x0002, target);           // MsvAvNbDomainName
        WriteAvPair(avMem, 0x0000, Array.Empty<byte>()); // MsvAvEOL
        var targetInfo = avMem.ToArray();

        // Negotiate flags: Unicode | NTLM | TargetInfo | Target type domain | Request target.
        const uint flags = 0x00000001 /*Unicode*/ | 0x00000200 /*NTLM*/ | 0x00800000 /*TargetInfo*/
            | 0x00010000 /*TargetTypeDomain*/ | 0x00000004 /*RequestTarget*/ | 0x80000000 /*Negotiate56*/;

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Signature);
        w.Write((uint)2); // Type 2

        int payloadOffset = 48; // fixed header size for this message layout
        // TargetName fields
        w.Write((ushort)target.Length);
        w.Write((ushort)target.Length);
        w.Write((uint)payloadOffset);
        // Flags
        w.Write(flags);
        // Server challenge (8 bytes)
        w.Write(serverChallenge);
        // Reserved (8 bytes)
        w.Write(0L);
        // TargetInfo fields
        w.Write((ushort)targetInfo.Length);
        w.Write((ushort)targetInfo.Length);
        w.Write((uint)(payloadOffset + target.Length));

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

            return new Type3
            {
                LmChallengeResponse = lm,
                NtChallengeResponse = nt,
                Domain = Encoding.Unicode.GetString(domain),
                User = Encoding.Unicode.GetString(user),
                Workstation = Encoding.Unicode.GetString(workstation),
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
