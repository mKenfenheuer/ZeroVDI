using System.Security.Cryptography;
using System.Text;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Cryptographic helpers for the gateway's NTLM/CredSSP authentication: the NTLM primitives
/// (MD4 NT-hash, HMAC-MD5 based NTLMv2). MD4 is not provided by the .NET BCL, so a compact
/// implementation is included here.
/// </summary>
public static class AuthCrypto
{
    /// <summary>The NTLM realm advertised by this gateway.</summary>
    public const string Realm = "KSol.IT ZeroVDI";

    /// <summary>Computes the NTLM NT hash = MD4(UTF-16LE(password)), hex (lowercase).</summary>
    public static string NtHash(string password)
    {
        var bytes = MD4(Encoding.Unicode.GetBytes(password));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Computes the raw NTLM NT hash bytes = MD4(UTF-16LE(password)).</summary>
    public static byte[] NtHashBytes(string password) => MD4(Encoding.Unicode.GetBytes(password));

    /// <summary>HMAC-MD5 helper used throughout the NTLMv2 computation.</summary>
    public static byte[] HmacMd5(byte[] key, byte[] data)
    {
        using var hmac = new HMACMD5(key);
        return hmac.ComputeHash(data);
    }

    /// <summary>
    /// RFC 1320 MD4. NTLM's NT hash is defined as MD4 over the UTF-16LE password, and .NET has no
    /// built-in MD4, so we implement it here. This is only used for NTLM hashing, not for any
    /// security-sensitive primitive on its own.
    /// </summary>
    public static byte[] MD4(byte[] input)
    {
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;

        // Pad: append 0x80, then zeros, then 64-bit bit-length, to a multiple of 64 bytes.
        long bitLen = (long)input.Length * 8;
        int padLen = (56 - (input.Length + 1) % 64 + 64) % 64;
        var msg = new byte[input.Length + 1 + padLen + 8];
        Array.Copy(input, msg, input.Length);
        msg[input.Length] = 0x80;
        BitConverter.GetBytes(bitLen).CopyTo(msg, msg.Length - 8);

        Func<uint, int, uint> rol = (x, n) => (x << n) | (x >> (32 - n));

        for (int off = 0; off < msg.Length; off += 64)
        {
            var x = new uint[16];
            for (int i = 0; i < 16; i++)
                x[i] = BitConverter.ToUInt32(msg, off + i * 4);

            uint aa = a, bb = b, cc = c, dd = d;

            // Round 1: F(x,y,z) = (x & y) | (~x & z)
            int[] r1 = { 3, 7, 11, 19 };
            for (int i = 0; i < 16; i++)
            {
                uint f = (b & c) | (~b & d);
                uint tmp = a + f + x[i];
                a = d; d = c; c = b;
                b = rol(tmp, r1[i % 4]);
            }

            // Round 2: G(x,y,z) = (x & y) | (x & z) | (y & z), constant 0x5a827999
            int[] r2 = { 3, 5, 9, 13 };
            int[] o2 = { 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15 };
            for (int i = 0; i < 16; i++)
            {
                uint g = (b & c) | (b & d) | (c & d);
                uint tmp = a + g + x[o2[i]] + 0x5a827999;
                a = d; d = c; c = b;
                b = rol(tmp, r2[i % 4]);
            }

            // Round 3: H(x,y,z) = x ^ y ^ z, constant 0x6ed9eba1
            int[] r3 = { 3, 9, 11, 15 };
            int[] o3 = { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 };
            for (int i = 0; i < 16; i++)
            {
                uint h = b ^ c ^ d;
                uint tmp = a + h + x[o3[i]] + 0x6ed9eba1;
                a = d; d = c; c = b;
                b = rol(tmp, r3[i % 4]);
            }

            a += aa; b += bb; c += cc; d += dd;
        }

        var result = new byte[16];
        BitConverter.GetBytes(a).CopyTo(result, 0);
        BitConverter.GetBytes(b).CopyTo(result, 4);
        BitConverter.GetBytes(c).CopyTo(result, 8);
        BitConverter.GetBytes(d).CopyTo(result, 12);
        return result;
    }
}
