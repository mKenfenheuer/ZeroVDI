using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Server-side HTTP Digest (RFC 2617 / RFC 7616, MD5 + auth qop) support, validating against a
/// stored HA1 (see <see cref="ApplicationUser.DigestHA1"/>) so no plaintext password is needed.
///
/// The gateway issues a nonce in its challenge; the client returns a response digest that this
/// class recomputes from the stored HA1. Issued nonces are tracked briefly to bound replay.
/// </summary>
public static class DigestAuth
{
    // RDG clients may send the digest computed over any of these HTTP methods; the gateway does not
    // see the original method in the auth handler, so all plausible methods are tried.
    private static readonly string[] CandidateMethods = { "RDG_OUT_DATA", "RDG_IN_DATA", "GET", "POST" };

    private static readonly ConcurrentDictionary<string, DateTimeOffset> IssuedNonces = new();
    private static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Issues a fresh nonce and returns the WWW-Authenticate challenge parameters (after "Digest ").</summary>
    public static string BuildChallenge(string realm)
    {
        PruneNonces();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        IssuedNonces[nonce] = DateTimeOffset.UtcNow;
        var opaque = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        return $"realm=\"{realm}\", qop=\"auth\", algorithm=MD5, nonce=\"{nonce}\", opaque=\"{opaque}\"";
    }

    /// <summary>Parsed Digest authorization parameters.</summary>
    public class Params
    {
        public string Username = "";
        public string Realm = "";
        public string Nonce = "";
        public string Uri = "";
        public string Response = "";
        public string? Qop;
        public string? Nc;
        public string? Cnonce;
        public string? Algorithm;
    }

    /// <summary>Parses the credential portion of an "Authorization: Digest ..." header.</summary>
    public static Params Parse(string credentials)
    {
        var p = new Params();
        foreach (var (key, value) in Tokenize(credentials))
        {
            switch (key)
            {
                case "username": p.Username = value; break;
                case "realm": p.Realm = value; break;
                case "nonce": p.Nonce = value; break;
                case "uri": p.Uri = value; break;
                case "response": p.Response = value; break;
                case "qop": p.Qop = value; break;
                case "nc": p.Nc = value; break;
                case "cnonce": p.Cnonce = value; break;
                case "algorithm": p.Algorithm = value; break;
            }
        }
        return p;
    }

    /// <summary>
    /// Validates the Digest response against the stored HA1. Returns true on success.
    /// </summary>
    public static bool Validate(Params p, string storedHa1)
    {
        if (string.IsNullOrEmpty(p.Response) || string.IsNullOrEmpty(p.Nonce) || string.IsNullOrEmpty(storedHa1))
            return false;

        // Only accept nonces we issued and that are still fresh (bounds replay).
        if (!IssuedNonces.TryGetValue(p.Nonce, out var issued) || DateTimeOffset.UtcNow - issued > NonceLifetime)
            return false;

        foreach (var method in CandidateMethods)
        {
            var ha2 = Md5Hex($"{method}:{p.Uri}");

            string expected;
            if (string.Equals(p.Qop, "auth", StringComparison.OrdinalIgnoreCase))
            {
                expected = Md5Hex($"{storedHa1}:{p.Nonce}:{p.Nc}:{p.Cnonce}:{p.Qop}:{ha2}");
            }
            else
            {
                // Legacy (no qop) form.
                expected = Md5Hex($"{storedHa1}:{p.Nonce}:{ha2}");
            }

            if (FixedEquals(expected, p.Response))
                return true;
        }

        return false;
    }

    private static string Md5Hex(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(b.ToLowerInvariant()));

    private static void PruneNonces()
    {
        var cutoff = DateTimeOffset.UtcNow - NonceLifetime;
        foreach (var kv in IssuedNonces)
            if (kv.Value < cutoff)
                IssuedNonces.TryRemove(kv.Key, out _);
    }

    /// <summary>Tokenizes a comma-separated list of key=value (value may be quoted) pairs.</summary>
    private static IEnumerable<(string Key, string Value)> Tokenize(string input)
    {
        int i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && (input[i] == ',' || char.IsWhiteSpace(input[i]))) i++;
            int keyStart = i;
            while (i < input.Length && input[i] != '=') i++;
            if (i >= input.Length) yield break;
            var key = input.Substring(keyStart, i - keyStart).Trim().ToLowerInvariant();
            i++; // skip '='

            string value;
            if (i < input.Length && input[i] == '"')
            {
                i++;
                int vs = i;
                var sb = new StringBuilder();
                while (i < input.Length && input[i] != '"')
                {
                    if (input[i] == '\\' && i + 1 < input.Length) i++;
                    sb.Append(input[i]);
                    i++;
                }
                i++; // closing quote
                value = sb.ToString();
            }
            else
            {
                int vs = i;
                while (i < input.Length && input[i] != ',') i++;
                value = input.Substring(vs, i - vs).Trim();
            }
            yield return (key, value);
        }
    }
}
