using System.Security.Claims;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.RDP;

/// <summary>The outcome of resolving a federated sign-in to a ZeroVDI account.</summary>
/// <param name="User">The account to sign in, or null when the sign-in was refused.</param>
/// <param name="Reason">User-facing explanation when <paramref name="User"/> is null.</param>
/// <param name="Created">Whether the account was provisioned by this sign-in.</param>
public sealed record ExternalSignInOutcome(ApplicationUser? User, string? Reason, bool Created);

/// <summary>
/// Turns an OpenID Connect sign-in into a ZeroVDI account: matching or provisioning the user, then
/// projecting the provider's group claim onto ZeroVDI user groups and roles.
///
/// Group mapping is deliberately one-directional and conservative. Provider groups are matched to
/// <em>existing</em> ZeroVDI groups by name — the provider never creates groups here, so an
/// administrator decides which directory groups mean anything by creating a group with that name and
/// granting it resources. Memberships this service creates are flagged
/// (<see cref="UserGroupMembership.IsExternal"/>) so a later sign-in can withdraw a membership the
/// directory has revoked without ever touching one an administrator added by hand.
/// </summary>
public sealed class ExternalIdentityService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<IdentityRole> _roles;
    private readonly ExternalIdentityOptions _options;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ExternalIdentityService> _logger;

    public ExternalIdentityService(ApplicationDbContext db, UserManager<ApplicationUser> users,
        RoleManager<IdentityRole> roles, ExternalIdentityOptions options, IAuditLogger audit,
        ILogger<ExternalIdentityService> logger)
    {
        _db = db;
        _users = users;
        _roles = roles;
        _options = options;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Resolve (and if necessary create) the local account behind an external login, then bring its
    /// groups and roles in line with the provider's claims. Never throws for a rejected sign-in — a
    /// refusal comes back as a null user plus a reason to show the visitor.
    /// </summary>
    public async Task<ExternalSignInOutcome> ResolveAsync(ExternalLoginInfo info)
    {
        var email = FindClaim(info.Principal, ClaimTypes.Email, "email", "preferred_username", "upn");
        var providerKey = info.ProviderKey;

        // Existing link first — the provider's subject is the stable identifier; an e-mail can change.
        var user = await _users.FindByLoginAsync(info.LoginProvider, providerKey);

        if (user == null && !string.IsNullOrWhiteSpace(email))
        {
            // No link yet: adopt an account with the same e-mail, so an administrator can pre-create
            // accounts (or an existing local user can move to SSO) without losing their grants.
            user = await _users.FindByEmailAsync(email);
        }

        var claimedGroups = ReadGroupClaims(info.Principal);
        var matched = await MatchGroupsAsync(claimedGroups);

        if (_options.RequireMappedGroup && matched.Count == 0)
        {
            await _audit.LogAsync(AuditCategory.Authentication, "ExternalLoginRejected", success: false,
                actorUserId: user?.Id, actorName: email ?? providerKey,
                detail: new { reason = "no mapped group", groups = claimedGroups });
            return new ExternalSignInOutcome(null, "Your account is not a member of any group that "
                + "grants access to this portal. Ask an administrator to assign one.", false);
        }

        var created = false;
        if (user == null)
        {
            if (!_options.AutoProvision)
            {
                await _audit.LogAsync(AuditCategory.Authentication, "ExternalLoginRejected", success: false,
                    actorName: email ?? providerKey, detail: new { reason = "no local account" });
                return new ExternalSignInOutcome(null, "There is no ZeroVDI account for this identity. "
                    + "Ask an administrator to create one.", false);
            }
            if (string.IsNullOrWhiteSpace(email))
            {
                await _audit.LogAsync(AuditCategory.Authentication, "ExternalLoginRejected", success: false,
                    actorName: providerKey, detail: new { reason = "no email claim" });
                return new ExternalSignInOutcome(null, "The identity provider did not supply an e-mail "
                    + "address, which ZeroVDI needs to create an account.", false);
            }

            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                // The provider vouched for the address; there is no local password to confirm against
                // and RequireConfirmedAccount would otherwise lock the user out of their own sign-in.
                EmailConfirmed = true,
            };
            var create = await _users.CreateAsync(user);
            if (!create.Succeeded)
            {
                var errors = string.Join("; ", create.Errors.Select(e => e.Description));
                _logger.LogWarning("Federated provisioning failed for {Email}: {Errors}", email, errors);
                await _audit.LogAsync(AuditCategory.User, "ExternalUserProvisioned", success: false,
                    actorName: email, detail: new { errors });
                return new ExternalSignInOutcome(null, "Your account could not be created automatically. "
                    + "Ask an administrator to check the ZeroVDI log.", false);
            }
            created = true;
            await _audit.LogAsync(AuditCategory.User, "ExternalUserProvisioned",
                actorUserId: user.Id, actorName: user.Email,
                targetType: "User", targetId: user.Id, targetName: user.Email,
                detail: new { provider = info.LoginProvider });
        }

        // Link the provider identity if this account reached us by e-mail match or is brand new.
        var logins = await _users.GetLoginsAsync(user);
        if (!logins.Any(l => l.LoginProvider == info.LoginProvider && l.ProviderKey == providerKey))
        {
            var link = await _users.AddLoginAsync(user, new UserLoginInfo(
                info.LoginProvider, providerKey, info.ProviderDisplayName));
            if (!link.Succeeded)
            {
                var errors = string.Join("; ", link.Errors.Select(e => e.Description));
                _logger.LogWarning("Linking external login for {Email} failed: {Errors}", user.Email, errors);
                return new ExternalSignInOutcome(null, "Your identity could not be linked to the "
                    + "existing ZeroVDI account. Ask an administrator to check the ZeroVDI log.", false);
            }
        }

        await SyncGroupsAsync(user, matched, claimedGroups);
        await SyncRolesAsync(user, claimedGroups);

        return new ExternalSignInOutcome(user, null, created);
    }

    /// <summary>Whether federated sign-ins should count as multi-factor for the enforcement gate.</summary>
    public bool SatisfiesMfa => _options.SatisfiesMfa;

    private static string? FindClaim(ClaimsPrincipal principal, params string[] types)
    {
        foreach (var type in types)
        {
            var value = principal.FindFirstValue(type);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    /// <summary>
    /// All values of the configured group claim. Providers differ: Keycloak sends full paths
    /// (<c>/eng/vdi-users</c>), Entra ID sends object ids or names, others send a space-separated
    /// list. Both the raw value and its last path segment are offered for matching.
    /// </summary>
    private List<string> ReadGroupClaims(ClaimsPrincipal principal)
    {
        var values = new List<string>();
        foreach (var claim in principal.FindAll(_options.GroupsClaim))
        {
            var raw = claim.Value?.Trim();
            if (string.IsNullOrEmpty(raw)) continue;
            values.Add(raw);
            var leaf = raw.TrimEnd('/').Split('/').Last();
            if (!string.IsNullOrEmpty(leaf) && !values.Contains(leaf, StringComparer.OrdinalIgnoreCase))
                values.Add(leaf);
        }
        return values;
    }

    /// <summary>ZeroVDI groups whose name matches one of the provider's group values.</summary>
    private async Task<List<UserGroup>> MatchGroupsAsync(List<string> claimed)
    {
        if (claimed.Count == 0) return new List<UserGroup>();
        var all = await _db.UserGroups.ToListAsync();
        return all.Where(g => claimed.Any(c => string.Equals(c, g.Name, StringComparison.OrdinalIgnoreCase)))
                  .ToList();
    }

    /// <summary>
    /// Add the memberships the directory implies and withdraw the ones it no longer does — but only
    /// among memberships this service created, so hand-made assignments survive untouched.
    /// </summary>
    private async Task SyncGroupsAsync(ApplicationUser user, List<UserGroup> matched, List<string> claimed)
    {
        var current = await _db.UserGroupMemberships
            .Where(m => m.UserId == user.Id).ToListAsync();

        var added = new List<string>();
        foreach (var group in matched)
        {
            var existing = current.FirstOrDefault(m => m.GroupId == group.Id);
            if (existing == null)
            {
                _db.UserGroupMemberships.Add(new UserGroupMembership
                {
                    GroupId = group.Id,
                    UserId = user.Id,
                    IsExternal = true,
                });
                added.Add(group.Name);
            }
        }

        var removed = new List<string>();
        foreach (var membership in current.Where(m => m.IsExternal))
        {
            if (matched.Any(g => g.Id == membership.GroupId)) continue;
            _db.UserGroupMemberships.Remove(membership);
            removed.Add(membership.GroupId);
        }

        if (added.Count == 0 && removed.Count == 0) return;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(AuditCategory.Authorization, "ExternalGroupsSynced",
            actorUserId: user.Id, actorName: user.Email,
            targetType: "User", targetId: user.Id, targetName: user.Email,
            detail: new { added, removedGroupIds = removed, claimed });
    }

    /// <summary>
    /// Grant or withdraw Admin/Auditor from the configured provider groups. Only these two roles are
    /// managed; a role list that is empty in configuration is left entirely alone, so federation can
    /// never silently strip an administrator who was promoted locally.
    /// </summary>
    private async Task SyncRolesAsync(ApplicationUser user, List<string> claimed)
    {
        await SyncRoleAsync(user, "Admin", _options.AdminGroups, claimed);
        await SyncRoleAsync(user, "Auditor", _options.AuditorGroups, claimed);
    }

    private async Task SyncRoleAsync(ApplicationUser user, string role, string[] mappedGroups,
        List<string> claimed)
    {
        if (mappedGroups.Length == 0) return;
        if (!await _roles.RoleExistsAsync(role)) return;

        var shouldHave = mappedGroups.Any(g =>
            claimed.Any(c => string.Equals(c, g, StringComparison.OrdinalIgnoreCase)));
        var hasRole = await _users.IsInRoleAsync(user, role);
        if (shouldHave == hasRole) return;

        if (shouldHave) await _users.AddToRoleAsync(user, role);
        else await _users.RemoveFromRoleAsync(user, role);

        // A role change invalidates the cached principal; force the next request to re-issue it.
        await _users.UpdateSecurityStampAsync(user);
        await _audit.LogAsync(AuditCategory.Authorization, "ExternalRoleSynced",
            actorUserId: user.Id, actorName: user.Email,
            targetType: "User", targetId: user.Id, targetName: user.Email,
            detail: new { role, granted = shouldHave });
    }
}
