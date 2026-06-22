using System.Security.Cryptography;
using System.Text;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Issues and validates the gateway's Pluggable Authentication (PAA) pre-auth tokens. A token is a
/// compact, HMAC-signed assertion of "user X is allowed until time T", used for the extended
/// HTTP_EXTENDED_AUTH_PAA cookie flow so a client that obtained a token from the web UI can open a
/// tunnel without re-entering credentials.
///
/// Format (URL-safe base64 of):  userId "|" expiryUnixSeconds "|" base64(HMACSHA256(payload))
/// </summary>
public class PaaTokenService
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

    public PaaTokenService(IConfiguration configuration)
    {
        // Use a configured signing key if present; otherwise derive a stable per-deployment key
        // from the data-protection-ish app secret so tokens survive within a deployment.
        var secret = configuration["Paa:SigningKey"]
            ?? configuration["ConnectionStrings:DefaultConnection"]
            ?? "ksol-rdpgw-default-paa-key";
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("paa:" + secret));
    }

    /// <summary>Issues a signed PAA token for the given user id.</summary>
    public string Issue(string userId)
    {
        var expiry = DateTimeOffset.UtcNow.Add(_lifetime).ToUnixTimeSeconds();
        var payload = $"{userId}|{expiry}";
        var sig = Convert.ToBase64String(Sign(payload));
        var token = $"{payload}|{sig}";
        return Base64Url(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>
    /// Validates a PAA token. Returns the user id on success, or null if invalid/expired/tampered.
    /// </summary>
    public string? Validate(string token)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(FromBase64Url(token));
            var parts = decoded.Split('|');
            if (parts.Length != 3)
                return null;

            var userId = parts[0];
            var expiry = long.Parse(parts[1]);
            var sig = Convert.FromBase64String(parts[2]);

            var expectedSig = Sign($"{userId}|{expiry}");
            if (!CryptographicOperations.FixedTimeEquals(sig, expectedSig))
                return null;

            if (DateTimeOffset.FromUnixTimeSeconds(expiry) < DateTimeOffset.UtcNow)
                return null;

            return userId;
        }
        catch
        {
            return null;
        }
    }

    private byte[] Sign(string payload)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
