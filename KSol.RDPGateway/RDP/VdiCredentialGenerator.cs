using System.Security.Cryptography;
using System.Text;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Derives a per-clone local account (username + password) for VDI pools that auto-generate credentials
/// (<see cref="Models.VdiPool.GenerateCredentials"/>). The username is built from the owner's account data
/// so it is recognisable and stable for that user, while the password is freshly random on every call.
/// Both are constrained to characters cloudbase-init (Windows) and cloud-init (Linux) accept for a local
/// user, and the password satisfies Windows local-account complexity (upper/lower/digit/symbol).
/// </summary>
public static class VdiCredentialGenerator
{
    // Windows local usernames may not contain " / \ [ ] : ; | = , + * ? < > @ and may not exceed 20 chars.
    private const int MaxUserLength = 20;
    private const int PasswordLength = 20;

    private const string Lower = "abcdefghijkmnpqrstuvwxyz";   // no l/o (look-alikes)
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // no I/O
    private const string Digits = "23456789";                  // no 0/1
    private const string Symbols = "!#%-_=+";                   // shell/cloud-init safe, no quoting needed

    /// <summary>The username + plaintext password to provision into a clone for a given owner.</summary>
    public readonly record struct GeneratedCredentials(string Username, string Password);

    /// <summary>
    /// Builds credentials for <paramref name="ownerName"/> (the owner's user name; <paramref name="ownerId"/>
    /// is mixed in to keep generated usernames distinct when two users share a display name).
    /// </summary>
    public static GeneratedCredentials Create(string ownerName, string ownerId)
        => new(BuildUsername(ownerName, ownerId), BuildPassword());

    private static string BuildUsername(string ownerName, string ownerId)
    {
        // Keep the recognisable letters/digits of the owner's name; fall back to "vdi".
        var basis = new string((ownerName ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(basis)) basis = "vdi";

        // A short stable suffix from the owner id disambiguates collisions deterministically.
        var suffix = ShortHash(ownerId, 4);
        var prefixLen = Math.Min(basis.Length, MaxUserLength - 1 - suffix.Length);
        return $"{basis[..prefixLen].ToLowerInvariant()}-{suffix}";
    }

    private static string BuildPassword()
    {
        // One guaranteed character from each class, then fill the rest, then shuffle — so complexity
        // rules always pass regardless of which random characters land in the fill.
        var chars = new List<char>
        {
            Pick(Lower), Pick(Upper), Pick(Digits), Pick(Symbols),
        };
        const string all = Lower + Upper + Digits + Symbols;
        while (chars.Count < PasswordLength) chars.Add(Pick(all));
        return Shuffle(chars);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

    private static string Shuffle(List<char> chars)
    {
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    private static string ShortHash(string input, int length)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input ?? ""));
        var sb = new StringBuilder();
        foreach (var b in hash)
        {
            sb.Append("abcdefghijkmnpqrstuvwxyz23456789"[b % 32]);
            if (sb.Length == length) break;
        }
        return sb.ToString();
    }
}
