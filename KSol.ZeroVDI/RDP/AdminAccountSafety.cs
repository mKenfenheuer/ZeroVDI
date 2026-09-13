using KSol.ZeroVDI.Models;
using Microsoft.AspNetCore.Identity;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Guards against the ways an administrator can lock every administrator out of ZeroVDI — the failure
/// mode that turns a configuration mistake into a restore-from-backup. Three moves are refused:
/// removing the last Admin role, deleting the last administrator, and disabling the last
/// administrator. Removing your own Admin role is refused outright, because doing it by accident
/// leaves you staring at a 403 on the page you were just using.
///
/// The checks are advisory in the sense that they return a message rather than throwing: callers
/// surface it in the admin UI and leave the state untouched.
/// </summary>
public sealed class AdminAccountSafety
{
    public const string AdminRole = "Admin";

    private readonly UserManager<ApplicationUser> _users;

    public AdminAccountSafety(UserManager<ApplicationUser> users) => _users = users;

    /// <summary>Number of enabled accounts holding the Admin role. Disabled accounts don't count —
    /// they cannot sign in, so they are no protection against a lockout.</summary>
    public async Task<int> CountUsableAdminsAsync()
    {
        var admins = await _users.GetUsersInRoleAsync(AdminRole);
        return admins.Count(a => !IsDisabled(a));
    }

    /// <summary>
    /// An account is "disabled" when its lockout is set to the far future — Identity has no separate
    /// enabled flag, and a lockout is exactly the semantics wanted (no sign-in, everything else intact).
    /// </summary>
    public static bool IsDisabled(ApplicationUser user) =>
        user.LockoutEnd is { } end && end > DateTimeOffset.UtcNow.AddYears(50);

    /// <summary>Temporarily locked out by failed sign-ins, as opposed to deliberately disabled.</summary>
    public static bool IsLockedOut(ApplicationUser user) =>
        user.LockoutEnd is { } end && end > DateTimeOffset.UtcNow && !IsDisabled(user);

    /// <summary>The far-future lockout used to mark an account disabled.</summary>
    public static readonly DateTimeOffset DisabledUntil = DateTimeOffset.MaxValue;

    /// <summary>
    /// Why the requested role set may not be applied, or null if it may. <paramref name="actorId"/> is
    /// the administrator performing the change.
    /// </summary>
    public async Task<string?> CheckRoleChangeAsync(ApplicationUser target, string? actorId,
        IEnumerable<string> currentRoles, IEnumerable<string> requestedRoles)
    {
        var had = currentRoles.Contains(AdminRole, StringComparer.OrdinalIgnoreCase);
        var keeps = requestedRoles.Contains(AdminRole, StringComparer.OrdinalIgnoreCase);
        if (!had || keeps) return null;

        if (target.Id == actorId)
            return "You cannot remove your own administrator role. Ask another administrator to do it.";

        if (await CountUsableAdminsAsync() <= 1)
            return "This is the only administrator account. Grant the role to someone else first.";

        return null;
    }

    /// <summary>Why the account may not be deleted, or null if it may.</summary>
    public async Task<string?> CheckDeleteAsync(ApplicationUser target, string? actorId)
    {
        if (target.Id == actorId)
            return "You cannot delete your own account.";
        return await CheckLastAdminAsync(target, "deleted");
    }

    /// <summary>Why the account may not be disabled, or null if it may.</summary>
    public async Task<string?> CheckDisableAsync(ApplicationUser target, string? actorId)
    {
        if (target.Id == actorId)
            return "You cannot disable your own account.";
        return await CheckLastAdminAsync(target, "disabled");
    }

    private async Task<string?> CheckLastAdminAsync(ApplicationUser target, string verb)
    {
        if (!await _users.IsInRoleAsync(target, AdminRole)) return null;
        if (await CountUsableAdminsAsync() > 1) return null;
        return $"This is the only administrator account and cannot be {verb}. "
             + "Grant the administrator role to someone else first.";
    }
}
