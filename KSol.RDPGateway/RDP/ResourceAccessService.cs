using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Single source of truth for "which resources may this user reach". Effective access is the UNION of a
/// user's direct per-(user, resource) grants and the grants of every group they belong to. Every
/// authorization gate (dashboard listing, console page, readiness API, WS relay) resolves access through
/// this service so the rules can't drift apart.
///
/// Personal state (stored SSO credentials, per-user <see cref="ConnectionDefaults"/>) lives only on the
/// direct <see cref="RDPResourceUserAuthorization"/> row; group access grants the right to connect but
/// carries no credentials. Callers that need the per-user row (for SSO/defaults) still load it directly —
/// it may legitimately be null for a group-only grant, in which case the console shows the login overlay.
///
/// Scoped (uses the scoped <see cref="ApplicationDbContext"/>).
/// </summary>
public sealed class ResourceAccessService
{
    private readonly ApplicationDbContext _db;

    public ResourceAccessService(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>The group ids the user is a member of.</summary>
    private IQueryable<string> GroupIdsFor(string userId) =>
        _db.UserGroupMemberships.Where(m => m.UserId == userId).Select(m => m.GroupId);

    /// <summary>True if the user may connect to the resource (directly or via any group). </summary>
    public async Task<bool> CanAccessAsync(string userId, string resourceId, CancellationToken ct = default)
    {
        var direct = await _db.RDPResourceUserAuthorizations
            .AnyAsync(a => a.UserId == userId && a.RDPResourceId == resourceId, ct);
        if (direct) return true;

        var groupIds = GroupIdsFor(userId);
        return await _db.RDPResourceGroupAuthorizations
            .AnyAsync(g => g.RDPResourceId == resourceId && groupIds.Contains(g.GroupId), ct);
    }

    /// <summary>
    /// Load the resource for a user IF they are authorized for it, else null. Use at the connect/console
    /// gates where the resource entity is needed.
    /// </summary>
    public async Task<RDPResource?> GetAuthorizedResourceAsync(string userId, string resourceId, CancellationToken ct = default)
    {
        if (!await CanAccessAsync(userId, resourceId, ct)) return null;
        return await _db.RDPResources.FirstOrDefaultAsync(r => r.Id == resourceId, ct);
    }

    /// <summary>
    /// All resource ids the user may reach (direct ∪ group), de-duplicated. Used for the dashboard.
    /// </summary>
    public async Task<HashSet<string>> AccessibleResourceIdsAsync(string userId, CancellationToken ct = default)
    {
        var direct = await _db.RDPResourceUserAuthorizations
            .Where(a => a.UserId == userId && a.RDPResourceId != null)
            .Select(a => a.RDPResourceId!)
            .ToListAsync(ct);

        var groupIds = GroupIdsFor(userId);
        var viaGroups = await _db.RDPResourceGroupAuthorizations
            .Where(g => groupIds.Contains(g.GroupId))
            .Select(g => g.RDPResourceId)
            .ToListAsync(ct);

        var set = new HashSet<string>(direct);
        set.UnionWith(viaGroups);
        return set;
    }
}
