namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Identity-federation settings, bound from the <c>Oidc</c> configuration section. When
/// <see cref="Enabled"/> is false (the default) nothing is registered and ZeroVDI behaves exactly as
/// it did before: local accounts only.
///
/// <code>
/// "Oidc": {
///   "Enabled": true,
///   "Authority": "https://login.example.com/realms/zerovdi",
///   "ClientId": "zerovdi",
///   "ClientSecret": "…",
///   "DisplayName": "Company SSO",
///   "Scopes": [ "openid", "profile", "email", "groups" ],
///   "GroupsClaim": "groups",
///   "AutoProvision": true,
///   "RequireMappedGroup": false,
///   "AdminGroups": [ "vdi-admins" ],
///   "AuditorGroups": [ "vdi-auditors" ],
///   "SatisfiesMfa": true
/// }
/// </code>
/// </summary>
public sealed class ExternalIdentityOptions
{
    public const string SectionName = "Oidc";

    /// <summary>The authentication scheme name used for the provider throughout Identity.</summary>
    public const string Scheme = "oidc";

    public bool Enabled { get; set; }

    /// <summary>OpenID Connect issuer; its discovery document must be reachable from the gateway.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Label on the sign-in button. Defaults to "Single sign-on".</summary>
    public string DisplayName { get; set; } = "Single sign-on";

    /// <summary>Scopes to request on top of <c>openid</c>. Defaults to profile + email.</summary>
    public string[] Scopes { get; set; } = { "profile", "email" };

    /// <summary>Redirect path registered at the provider. Must match the client configuration there.</summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>
    /// Claim carrying the user's group names or ids. Values are matched against ZeroVDI user-group
    /// names, case-insensitively; groups that do not exist in ZeroVDI are ignored.
    /// </summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>
    /// Create a ZeroVDI account the first time someone signs in through the provider. With this off,
    /// federation only signs in users an administrator has already created (matched by e-mail).
    /// </summary>
    public bool AutoProvision { get; set; } = true;

    /// <summary>
    /// Refuse the sign-in unless at least one of the user's provider groups matches a ZeroVDI group.
    /// Turns the group claim into an access gate rather than only an assignment mechanism.
    /// </summary>
    public bool RequireMappedGroup { get; set; }

    /// <summary>Provider groups whose members receive the Admin role (and lose it when removed).</summary>
    public string[] AdminGroups { get; set; } = Array.Empty<string>();

    /// <summary>Provider groups whose members receive the Auditor role.</summary>
    public string[] AuditorGroups { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Treat a federated sign-in as already multi-factor, so <see cref="MfaEnforcementMiddleware"/>
    /// does not additionally push the user through ZeroVDI's own enrollment. Leave this on when the
    /// provider enforces MFA itself (the usual reason to federate); turn it off to require a second
    /// factor here regardless.
    /// </summary>
    public bool SatisfiesMfa { get; set; } = true;

    /// <summary>True when the section is switched on AND carries the minimum usable configuration.</summary>
    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Why <see cref="IsConfigured"/> is false, for the startup warning and the ops page.</summary>
    public string? ConfigurationProblem
    {
        get
        {
            if (!Enabled) return null;
            if (string.IsNullOrWhiteSpace(Authority)) return "Oidc:Authority is not set.";
            if (string.IsNullOrWhiteSpace(ClientId)) return "Oidc:ClientId is not set.";
            return null;
        }
    }
}
