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

    // --- Provenance-aware admin lookups -------------------------------------------------------
    // The admin pages need to show not just *whether* access exists but *why*: a direct grant, a
    // grant inherited from one or more groups, or both. These two methods are the single source
    // for the user page's "Resource access" tab and the resource page's "Users & access" tab, so
    // the Direct/via-Group provenance can't drift between the two views.

    /// <summary>
    /// Every resource the user can reach, each tagged Direct and/or via which group(s). The direct
    /// authorization row (carrying SSO creds + per-user ConnectionDefaults) is attached when present.
    /// </summary>
    public async Task<IReadOnlyList<ResourceAccessEntry>> GetUserAccessAsync(string userId, CancellationToken ct = default)
    {
        var directAuths = await _db.RDPResourceUserAuthorizations
            .Include(a => a.RDPResource)
            .Where(a => a.UserId == userId && a.RDPResourceId != null)
            .ToListAsync(ct);

        var groups = await _db.UserGroupMemberships
            .Where(m => m.UserId == userId)
            .Join(_db.RDPResourceGroupAuthorizations, m => m.GroupId, g => g.GroupId,
                (m, g) => new { g.RDPResourceId, g.Group!.Id, g.Group.Name })
            .ToListAsync(ct);

        var resourceIds = directAuths.Select(a => a.RDPResourceId!)
            .Concat(groups.Select(g => g.RDPResourceId))
            .ToHashSet();

        // Resources referenced only via a group aren't on the direct auth rows; load them by id.
        var resourcesById = await _db.RDPResources
            .Where(r => resourceIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);

        var groupsByResource = groups
            .GroupBy(g => g.RDPResourceId)
            .ToDictionary(g => g.Key, g => g
                .Select(x => (x.Id, x.Name))
                .Distinct()
                .ToList());

        var directByResource = directAuths.ToDictionary(a => a.RDPResourceId!, a => a);

        var entries = new List<ResourceAccessEntry>();
        foreach (var rid in resourceIds)
        {
            if (!resourcesById.TryGetValue(rid, out var resource)) continue;
            directByResource.TryGetValue(rid, out var directAuth);
            entries.Add(new ResourceAccessEntry(
                resource,
                Direct: directAuth != null,
                ViaGroups: groupsByResource.TryGetValue(rid, out var gs) ? gs : new(),
                DirectAuth: directAuth));
        }
        return entries.OrderBy(e => e.Resource.Name).ToList();
    }

    /// <summary>
    /// Every user who can reach a resource, each tagged Direct and/or via which group(s). The direct
    /// authorization row is attached when present (for the resource page's per-user creds/defaults UI).
    /// </summary>
    public async Task<IReadOnlyList<UserAccessEntry>> GetResourceAccessAsync(string resourceId, CancellationToken ct = default)
    {
        var directAuths = await _db.RDPResourceUserAuthorizations
            .Include(a => a.User)
            .Where(a => a.RDPResourceId == resourceId && a.UserId != null)
            .ToListAsync(ct);

        var viaGroups = await _db.RDPResourceGroupAuthorizations
            .Where(g => g.RDPResourceId == resourceId)
            .Join(_db.UserGroupMemberships, g => g.GroupId, m => m.GroupId,
                (g, m) => new { m.UserId, g.Group!.Id, g.Group.Name })
            .ToListAsync(ct);

        var userIds = directAuths.Select(a => a.UserId!)
            .Concat(viaGroups.Select(g => g.UserId))
            .ToHashSet();

        var usersById = await _db.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var groupsByUser = viaGroups
            .GroupBy(g => g.UserId)
            .ToDictionary(g => g.Key, g => g
                .Select(x => (x.Id, x.Name))
                .Distinct()
                .ToList());

        var directByUser = directAuths.ToDictionary(a => a.UserId!, a => a);

        var entries = new List<UserAccessEntry>();
        foreach (var uid in userIds)
        {
            if (!usersById.TryGetValue(uid, out var user)) continue;
            directByUser.TryGetValue(uid, out var directAuth);
            entries.Add(new UserAccessEntry(
                user,
                Direct: directAuth != null,
                ViaGroups: groupsByUser.TryGetValue(uid, out var gs) ? gs : new(),
                DirectAuth: directAuth));
        }
        return entries.OrderBy(e => e.User.UserName).ToList();
    }
}

/// <summary>One resource a user can reach, with the provenance of that access.</summary>
public sealed record ResourceAccessEntry(
    RDPResource Resource,
    bool Direct,
    List<(string Id, string Name)> ViaGroups,
    RDPResourceUserAuthorization? DirectAuth);

/// <summary>One user who can reach a resource, with the provenance of that access.</summary>
public sealed record UserAccessEntry(
    ApplicationUser User,
    bool Direct,
    List<(string Id, string Name)> ViaGroups,
    RDPResourceUserAuthorization? DirectAuth);
