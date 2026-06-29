using Microsoft.AspNetCore.Identity;

namespace KSol.RDPGateway.Models;

/// <summary>
/// Application user that, in addition to the standard ASP.NET Identity password hash, stores the
/// NTLM NT hash derived from the plaintext password at set-time so the gateway's NLA/CredSSP
/// man-in-the-middle can validate NTLMv2 responses without prompting the user for anything extra.
///
/// Populated automatically by <see cref="RDP.DerivingPasswordHasher"/> whenever a password is
/// created, changed or reset.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// NTLM NT hash = MD4(UTF16LE(password)), hex-encoded. Used to validate NTLMv2 responses.
    /// Null until the user next sets a password.
    /// </summary>
    public string? NtHash { get; set; }

    /// <summary>
    /// Whether the user has enrolled email-based MFA (one-time codes sent to their account email).
    /// Identity's built-in <c>TwoFactorEnabled</c> is the master switch and the authenticator is keyed
    /// off the authenticator secret; email is a standalone alternative method, so it needs its own
    /// enrollment flag. A user may have the authenticator, email, or both — any enrolled method
    /// satisfies the MFA-required policy. When the last method is removed, <c>TwoFactorEnabled</c> is
    /// cleared too.
    /// </summary>
    public bool EmailTwoFactorEnabled { get; set; }
}
