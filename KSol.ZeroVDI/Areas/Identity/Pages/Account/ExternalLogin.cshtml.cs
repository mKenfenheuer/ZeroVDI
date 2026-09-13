using System.Security.Claims;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KSol.ZeroVDI.Areas.Identity.Pages.Account;

/// <summary>
/// The federated sign-in flow. This replaces the stock Identity UI page, which stops at a
/// "register an account" form: an enterprise SSO deployment expects the directory to be the source
/// of truth, so a first sign-in provisions the account (when configured to) and the visitor lands on
/// their desktop list rather than a second registration form.
///
/// The page only ever renders for a refusal — a successful sign-in redirects.
/// </summary>
[AllowAnonymous]
public class ExternalLoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ExternalIdentityService _external;
    private readonly ExternalIdentityOptions _options;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ExternalLoginModel> _logger;

    public ExternalLoginModel(SignInManager<ApplicationUser> signInManager,
        ExternalIdentityService external, ExternalIdentityOptions options,
        IAuditLogger audit, ILogger<ExternalLoginModel> logger)
    {
        _signInManager = signInManager;
        _external = external;
        _options = options;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Shown on the page; only set when the sign-in was refused.</summary>
    public string ErrorMessage { get; private set; } = "";

    public IActionResult OnGet() => RedirectToPage("./Login");

    /// <summary>Start the round-trip to the provider.</summary>
    public IActionResult OnPost(string? provider = null, string? returnUrl = null)
    {
        if (!_options.IsConfigured) return RedirectToPage("./Login");
        provider ??= ExternalIdentityOptions.Scheme;
        var redirect = Url.Page("./ExternalLogin", pageHandler: "Callback", values: new { returnUrl });
        var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirect);
        return new ChallengeResult(provider, properties);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl = null, string? remoteError = null)
    {
        returnUrl ??= Url.Content("~/");

        if (remoteError != null)
        {
            _logger.LogWarning("External provider returned an error: {Error}", remoteError);
            await _audit.LogAsync(Models.AuditCategory.Authentication, "ExternalLoginRejected",
                success: false, detail: new { remoteError });
            return Refuse("The identity provider reported an error: " + remoteError);
        }

        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (info == null)
        {
            // The external cookie expired or the round-trip was replayed; starting over is the fix.
            return Refuse("The sign-in did not complete in time. Please try again.");
        }

        var outcome = await _external.ResolveAsync(info);
        if (outcome.User == null) return Refuse(outcome.Reason ?? "Your sign-in was refused.");

        var user = outcome.User;
        if (await _signInManager.UserManager.IsLockedOutAsync(user))
        {
            await _audit.LogAsync(Models.AuditCategory.Authentication, "ExternalLoginRejected",
                success: false, actorUserId: user.Id, actorName: user.Email,
                detail: new { reason = "locked out" });
            return Refuse("This account is locked. Contact an administrator.");
        }

        // Sign in with an amr claim recording how the session was authenticated. The MFA gate reads it
        // (see MfaEnforcementMiddleware) and the audit trail shows federated sessions distinctly.
        var claims = new List<Claim> { new("amr", ExternalIdentityOptions.Scheme) };
        await _signInManager.SignInWithClaimsAsync(user, isPersistent: false, claims);

        await _audit.LogAsync(Models.AuditCategory.Authentication, "LoginSucceeded",
            actorUserId: user.Id, actorName: user.Email,
            detail: new { provider = info.LoginProvider, provisioned = outcome.Created });

        return LocalRedirect(returnUrl);
    }

    private IActionResult Refuse(string message)
    {
        ErrorMessage = message;
        return Page();
    }
}
