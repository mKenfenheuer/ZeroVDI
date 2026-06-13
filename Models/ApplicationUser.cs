using Microsoft.AspNetCore.Identity;

namespace KSol.RDPGateway.Models;

/// <summary>
/// Application user that, in addition to the standard ASP.NET Identity password hash, stores
/// secrets derived from the plaintext password at set-time so the gateway can support HTTP Digest
/// and NTLM/Negotiate authentication without prompting the user for anything extra.
///
/// These are populated automatically by <see cref="RDP.DerivingPasswordHasher"/> whenever a
/// password is created, changed or reset.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// HTTP Digest HA1 = MD5("username:realm:password"), hex-encoded. Used by Digest auth.
    /// Null until the user next sets a password.
    /// </summary>
    public string? DigestHA1 { get; set; }

    /// <summary>
    /// The realm the <see cref="DigestHA1"/> was computed against. Digest auth must reuse this
    /// realm in its challenge for the stored HA1 to match.
    /// </summary>
    public string? DigestRealm { get; set; }

    /// <summary>
    /// NTLM NT hash = MD4(UTF16LE(password)), hex-encoded. Used to validate NTLMv2 responses.
    /// Null until the user next sets a password.
    /// </summary>
    public string? NtHash { get; set; }
}
