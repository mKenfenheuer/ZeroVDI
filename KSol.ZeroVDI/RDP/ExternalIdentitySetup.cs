using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Wires up optional OpenID Connect federation. Everything is behind <c>Oidc:Enabled</c>: with the
/// section absent or off, no handler is registered, the sign-in page shows no provider button, and
/// ZeroVDI is a local-accounts-only deployment exactly as before.
/// </summary>
public static class ExternalIdentitySetup
{
    public static ExternalIdentityOptions AddExternalIdentity(this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(ExternalIdentityOptions.SectionName)
            .Get<ExternalIdentityOptions>() ?? new ExternalIdentityOptions();

        // Registered unconditionally so the sign-in page and the operations view can ask whether
        // federation is on without every call site having to handle a missing service.
        services.AddSingleton(options);
        services.AddScoped<ExternalIdentityService>();

        if (!options.IsConfigured) return options;

        services.AddAuthentication()
            .AddOpenIdConnect(ExternalIdentityOptions.Scheme, options.DisplayName, oidc =>
            {
                oidc.Authority = options.Authority;
                oidc.ClientId = options.ClientId;
                oidc.ClientSecret = options.ClientSecret;
                oidc.CallbackPath = options.CallbackPath;

                // Authorization code + PKCE: the only flow appropriate for a confidential server-side
                // client, and the only one that keeps tokens out of the browser entirely.
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                // ZeroVDI never calls the provider's APIs on the user's behalf, so nothing is gained by
                // keeping the tokens in the auth cookie — and a cookie without them is much smaller.
                oidc.SaveTokens = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;

                // The external handler deposits the principal in Identity's external cookie; our
                // ExternalLogin page picks it up from there and resolves it to a ZeroVDI account.
                oidc.SignInScheme = IdentityConstants.ExternalScheme;

                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                foreach (var scope in options.Scopes)
                    if (!string.IsNullOrWhiteSpace(scope) && !oidc.Scope.Contains(scope))
                        oidc.Scope.Add(scope);

                // Group claims are usually an array, which the stock claim actions flatten to a single
                // JSON string; map them to one claim per group instead.
                oidc.ClaimActions.Add(new ArrayClaimAction(options.GroupsClaim));

                oidc.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        // Behind a reverse proxy the redirect_uri must be the address the BROWSER uses,
                        // not whatever host header reached Kestrel — a mismatch is rejected by the
                        // provider as an unregistered redirect URI. App:PublicBaseUrl is authoritative
                        // when configured; otherwise the forwarded-header-corrected request stands.
                        var publicUrl = context.HttpContext.RequestServices.GetService<PublicUrl>();
                        if (publicUrl?.BaseUrl is { } baseUrl)
                        {
                            context.ProtocolMessage.RedirectUri =
                                baseUrl.ToString().TrimEnd('/') + options.CallbackPath;
                        }
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = context =>
                    {
                        // Never surface the provider's raw failure page; send the user back to a page
                        // that explains it in ZeroVDI's own terms.
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>().CreateLogger("Oidc");
                        logger.LogWarning(context.Failure, "OpenID Connect sign-in failed.");
                        context.Response.Redirect("/Identity/Account/ExternalLogin?handler=Callback&remoteError="
                            + Uri.EscapeDataString(context.Failure?.Message ?? "sign-in failed"));
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            });

        return options;
    }

    /// <summary>
    /// Maps a userinfo property that may be a JSON array into one claim per element (the built-in
    /// <c>MapJsonKey</c> only handles scalars, and a group list arriving as <c>["a","b"]</c> would
    /// otherwise become a single unusable claim value).
    /// </summary>
    private sealed class ArrayClaimAction : ClaimAction
    {
        public ArrayClaimAction(string claimType) : base(claimType, ClaimValueTypes.String) { }

        public override void Run(JsonElement userData, ClaimsIdentity identity, string issuer)
        {
            if (userData.ValueKind != JsonValueKind.Object) return;
            if (!userData.TryGetProperty(ClaimType, out var value)) return;

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) Add(item, identity, issuer);
            }
            else Add(value, identity, issuer);
        }

        private void Add(JsonElement item, ClaimsIdentity identity, string issuer)
        {
            if (item.ValueKind != JsonValueKind.String) return;
            var text = item.GetString();
            if (string.IsNullOrEmpty(text)) return;
            // The ID token may already have carried the same group; don't duplicate it.
            if (identity.HasClaim(c => c.Type == ClaimType && c.Value == text)) return;
            identity.AddClaim(new Claim(ClaimType, text, ValueType, issuer));
        }
    }
}
