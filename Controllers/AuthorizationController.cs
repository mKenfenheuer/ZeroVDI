using System.Security.Claims;
using KSol.RDPGateway.Models;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// OAuth 2.0 / OpenID Connect endpoints (authorize, token, userinfo) for the self-hosted OpenIddict
/// server. Authentication is delegated to the existing ASP.NET Identity login (cookie); once the
/// user is signed in, this controller issues the tokens.
/// </summary>
public class AuthorizationController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public AuthorizationController(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Authorization endpoint. If the user isn't signed in, challenge the Identity cookie scheme
    /// (which redirects to the login page); once signed in, issue an authorization code.
    /// </summary>
    [HttpGet("connect/authorize")]
    [HttpPost("connect/authorize")]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Require an interactive Identity session; otherwise send the user to the login page and
        // come back to this same authorize URL afterwards.
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded)
        {
            return Challenge(
                authenticationSchemes: IdentityConstants.ApplicationScheme,
                properties: new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path + QueryString.Create(Request.HasFormContentType ? Request.Form : Request.Query)
                });
        }

        var user = await _userManager.GetUserAsync(result.Principal)
            ?? throw new InvalidOperationException("The user details cannot be retrieved.");

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters_DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, await _userManager.GetUserIdAsync(user))
                .SetClaim(Claims.Name, await _userManager.GetUserNameAsync(user))
                .SetClaim(Claims.Email, await _userManager.GetEmailAsync(user));

        identity.SetScopes(request.GetScopes());
        identity.SetDestinations(GetDestinations);

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>Token endpoint: exchanges an authorization code or refresh token for tokens.</summary>
    [HttpPost("connect/token")]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // Recover the principal stored in the authorization code / refresh token. The user is
            // identified by the OIDC subject ("sub") claim, not ASP.NET's NameIdentifier, so resolve
            // by subject explicitly.
            var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var subject = result.Principal?.GetClaim(Claims.Subject);
            var user = subject != null ? await _userManager.FindByIdAsync(subject) : null;
            if (user == null)
            {
                return Forbid(
                    authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    properties: new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token is no longer valid."
                    }));
            }

            var identity = new ClaimsIdentity(result.Principal!.Claims,
                authenticationType: TokenValidationParameters_DefaultAuthenticationType,
                nameType: Claims.Name,
                roleType: Claims.Role);

            identity.SetClaim(Claims.Subject, await _userManager.GetUserIdAsync(user))
                    .SetClaim(Claims.Name, await _userManager.GetUserNameAsync(user))
                    .SetClaim(Claims.Email, await _userManager.GetEmailAsync(user));

            identity.SetDestinations(GetDestinations);

            return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new InvalidOperationException("The specified grant type is not supported.");
    }

    /// <summary>UserInfo endpoint.</summary>
    [HttpGet("connect/userinfo")]
    [HttpPost("connect/userinfo")]
    public async Task<IActionResult> UserInfo()
    {
        var subject = User.GetClaim(Claims.Subject);
        var user = subject != null ? await _userManager.FindByIdAsync(subject) : null;
        if (user == null)
            return Challenge(authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = await _userManager.GetUserIdAsync(user),
            [Claims.Name] = await _userManager.GetUserNameAsync(user) ?? string.Empty,
            [Claims.Email] = await _userManager.GetEmailAsync(user) ?? string.Empty,
        };
        return Ok(claims);
    }

    private const string TokenValidationParameters_DefaultAuthenticationType = "AuthenticationTypes.Federation";

    /// <summary>Decides which claims go into the access token vs. the identity token.</summary>
    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Subject:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            case Claims.Name:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Profile))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Email))
                    yield return Destinations.IdentityToken;
                yield break;

            case "AspNet.Identity.SecurityStamp":
                yield break; // never expose the security stamp

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
