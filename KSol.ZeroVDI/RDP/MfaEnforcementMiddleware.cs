using Microsoft.AspNetCore.Identity;
using KSol.ZeroVDI.Models;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Forces authenticated users who are <em>required</em> to use MFA (see <see cref="MfaPolicy"/>) but have
/// not yet enrolled an authenticator to complete enrollment before they can use the rest of the app. Such
/// a user is redirected to the EnableAuthenticator page on every navigation until 2FA is enabled.
///
/// An allow-list of paths is exempted so the user can actually finish setup (the 2FA management pages,
/// recovery-code pages), sign out, load static assets, and so on. Non-interactive endpoints (the RDP
/// WebSocket relay and the RDWeb feed) are also exempt: they have their own auth and a redirect would only
/// corrupt the protocol — the gate that matters there is the login itself, which already runs the 2FA
/// challenge for enrolled users.
/// </summary>
public sealed class MfaEnforcementMiddleware
{
    private const string SetupPath = "/Identity/Account/Manage/EnableAuthenticator";

    private readonly RequestDelegate _next;
    private readonly MfaPolicy _policy;
    private readonly ExternalIdentityOptions _external;

    public MfaEnforcementMiddleware(RequestDelegate next, MfaPolicy policy,
        ExternalIdentityOptions external)
    {
        _next = next;
        _policy = policy;
        _external = external;
    }

    public async Task InvokeAsync(HttpContext context, UserManager<ApplicationUser> userManager,
        IAuditLogger audit)
    {
        if (!_policy.Enabled
            || context.User?.Identity?.IsAuthenticated != true
            || IsExempt(context.Request.Path)
            || !_policy.IsRequiredFor(context.User)
            || IsFederatedAndAccepted(context.User))
        {
            await _next(context);
            return;
        }

        var user = await userManager.GetUserAsync(context.User);
        // If the principal can't be loaded, let the request proceed; downstream auth will handle it.
        if (user == null || await userManager.GetTwoFactorEnabledAsync(user))
        {
            await _next(context);
            return;
        }

        // Required but not enrolled → push to setup. Record it once per redirect so admins can see who is
        // being gated (and detect users stuck unable to enroll).
        await audit.LogAsync(AuditCategory.Authentication, "MfaEnrollmentRequired",
            actorUserId: user.Id, actorName: user.UserName);

        var returnUrl = context.Request.Path + context.Request.QueryString;
        context.Response.Redirect($"{SetupPath}?returnUrl={Uri.EscapeDataString(returnUrl)}&mfaRequired=1");
    }

    /// <summary>
    /// A session established through the identity provider, where the provider is trusted to have done
    /// the second factor (<c>Oidc:SatisfiesMfa</c>, on by default — enforcing MFA at the provider is
    /// the usual reason to federate). Turning it off demands a ZeroVDI second factor on top.
    /// </summary>
    private bool IsFederatedAndAccepted(System.Security.Claims.ClaimsPrincipal user)
        => _external.SatisfiesMfa && user.HasClaim("amr", ExternalIdentityOptions.Scheme);

    /// <summary>
    /// Paths the enforcement gate must never block: the 2FA setup/management flow itself, sign-out,
    /// static assets, the error page, and the non-interactive gateway endpoints.
    /// </summary>
    private static bool IsExempt(PathString path)
    {
        // The whole account-management 2FA flow + logout + the login/challenge pages.
        if (path.StartsWithSegments("/Identity/Account/Manage", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.StartsWithSegments("/Identity/Account/Logout", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.StartsWithSegments("/Identity/Account/Login", StringComparison.OrdinalIgnoreCase))
            return true;

        // Static assets & framework infra.
        if (path.StartsWithSegments("/css", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/js", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/lib", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/images", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/favicon", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/Home/Error", StringComparison.OrdinalIgnoreCase))
            return true;

        // Non-interactive gateway endpoints: redirect would break the protocol. These carry their own
        // auth and the login 2FA challenge already covers enrolled users.
        if (path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
