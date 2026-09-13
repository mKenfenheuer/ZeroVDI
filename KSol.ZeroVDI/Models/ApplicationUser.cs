using Microsoft.AspNetCore.Identity;

namespace KSol.ZeroVDI.Models;

/// <summary>
/// Application user. Only ASP.NET Identity's own salted password hash is stored — the gateway never
/// needs a reversible or NTLM-usable form of the sign-in password, because it authenticates to RDP
/// hosts with the per-resource credentials, not with the portal password.
/// </summary>
public class ApplicationUser : IdentityUser
{
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
