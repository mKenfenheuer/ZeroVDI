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
}
