using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Config-driven multi-factor enforcement policy. Identity already <em>honors</em> 2FA at login (a user
/// with an authenticator is sent through <c>LoginWith2fa</c>); this policy adds the missing piece —
/// deciding which users are <em>required</em> to have MFA enrolled, so that <see cref="MfaEnforcementMiddleware"/>
/// can force un-enrolled-but-required users to set it up before using the app.
///
/// Configuration (Mfa section):
///   - <c>RequireForAll</c> (bool): every authenticated user must enroll.
///   - <c>RequiredRoles</c> (string[]): users in any of these roles must enroll. Defaults to ["Admin"]
///     when neither this nor RequireForAll is set, so privileged accounts are protected out of the box.
/// </summary>
public sealed class MfaPolicy
{
    private readonly bool _requireForAll;
    private readonly HashSet<string> _requiredRoles;

    public MfaPolicy(IConfiguration config)
    {
        var section = config.GetSection("Mfa");
        _requireForAll = section.GetValue("RequireForAll", false);
        var roles = section.GetSection("RequiredRoles").Get<string[]>();
        // Secure-by-default: if nothing is configured, require MFA for Admins.
        _requiredRoles = new HashSet<string>(
            (roles is { Length: > 0 } ? roles : new[] { "Admin" }),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True if enforcement is active for at least one user class (used to short-circuit the middleware).</summary>
    public bool Enabled => _requireForAll || _requiredRoles.Count > 0;

    /// <summary>
    /// Whether the given principal is required to have MFA enrolled. Evaluated off the auth-cookie claims
    /// (no DB hit) so it is cheap to call per request.
    /// </summary>
    public bool IsRequiredFor(ClaimsPrincipal user)
    {
        if (_requireForAll) return true;
        foreach (var role in _requiredRoles)
            if (user.IsInRole(role)) return true;
        return false;
    }
}
