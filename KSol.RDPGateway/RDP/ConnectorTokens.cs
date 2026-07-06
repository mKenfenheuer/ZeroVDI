using System.Security.Cryptography;
using System.Text;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Minting and verification of connector secrets. Registration tokens and long-lived auth tokens are both
/// high-entropy URL-safe random strings; only the SHA-256 hash of the auth token is persisted, so a DB
/// leak never yields a usable bearer credential. Verification is constant-time.
/// </summary>
public static class ConnectorTokens
{
    /// <summary>Generates a 256-bit URL-safe random token (base64url, no padding).</summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>Lower-case hex SHA-256 of a token, as stored in <c>Connector.AuthTokenHash</c>.</summary>
    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Constant-time comparison of a candidate token against a stored hash.</summary>
    public static bool Verify(string token, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        var computed = Encoding.UTF8.GetBytes(Hash(token));
        var expected = Encoding.UTF8.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }
}
