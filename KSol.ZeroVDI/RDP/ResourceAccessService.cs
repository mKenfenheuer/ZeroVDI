using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.RDP;

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

    /// <summary>
    /// True if the user may connect to the id, which may be a resource OR a VDI pool entry point.
    /// Resource access = direct ∪ group. Pool access = the user is assigned to the pool directly or via
    /// a group. A user also "accesses" the VDI clone resource the pool provisioned for them (the relay
    /// reconnects to the concrete clone id after provisioning).
    /// </summary>
    public async Task<bool> CanAccessAsync(string userId, string resourceId, CancellationToken ct = default)
    {
        var direct = await _db.RDPResourceUserAuthorizations
            .AnyAsync(a => a.UserId == userId && a.RDPResourceId == resourceId, ct);
        if (direct) return true;

        var groupIds = GroupIdsFor(userId);
        var viaGroup = await _db.RDPResourceGroupAuthorizations
            .AnyAsync(g => g.RDPResourceId == resourceId && groupIds.Contains(g.GroupId), ct);
        if (viaGroup) return true;

        // A VDI pool entry point the user is entitled to.
        if (await CanAccessPoolAsync(userId, resourceId, ct)) return true;

        // The user's own provisioned clone (a VdiClone resource owned by their instance).
        return await _db.VdiInstances
            .AnyAsync(i => i.OwnerUserId == userId && i.RDPResourceId == resourceId, ct);
    }

    /// <summary>True if the user is entitled to the VDI pool (direct user assignment or via a group).</summary>
    public async Task<bool> CanAccessPoolAsync(string userId, string poolId, CancellationToken ct = default)
    {
        var direct = await _db.VdiPoolAssignments
            .AnyAsync(a => a.PoolId == poolId && a.UserId == userId, ct);
        if (direct) return true;

        var groupIds = GroupIdsFor(userId);
        return await _db.VdiPoolAssignments
            .AnyAsync(a => a.PoolId == poolId && a.GroupId != null && groupIds.Contains(a.GroupId), ct);
    }

    /// <summary>The VDI pools the user is entitled to (direct ∪ group), de-duplicated.</summary>
    public async Task<IReadOnlyList<VdiPool>> AccessiblePoolsAsync(string userId, CancellationToken ct = default)
    {
        var groupIds = GroupIdsFor(userId);
        var poolIds = await _db.VdiPoolAssignments
            .Where(a => a.UserId == userId || (a.GroupId != null && groupIds.Contains(a.GroupId)))
            .Select(a => a.PoolId)
            .Distinct()
            .ToListAsync(ct);

        return await _db.VdiPools
            .Where(p => poolIds.Contains(p.Id))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Load the resource for a user IF they are authorized for it, else null. Use at the connect/console
    /// gates where the resource entity is needed.
    /// </summary>
    public async Task<RDPResource?> GetAuthorizedResourceAsync(string userId, string resourceId, CancellationToken ct = default)
    {
        if (!await CanAccessAsync(userId, resourceId, ct)) return null;

        var resource = await _db.RDPResources.FirstOrDefaultAsync(r => r.Id == resourceId, ct);
        if (resource != null) return resource;

        // No concrete resource: the id is a VDI pool entry point. Return a SYNTHETIC, non-tracked
        // RDPResource standing in for the pool so the console/connect gates render and the readiness
        // pre-step (VdiResourceResolver) provisions the real clone, keyed by this same pool id. It is
        // never persisted — Source=VdiClone keeps it on the Proxmox connect path.
        var pool = await _db.VdiPools.AsNoTracking().FirstOrDefaultAsync(p => p.Id == resourceId, ct);
        if (pool == null) return null;
        return new RDPResource
        {
            Id = pool.Id,
            Name = pool.Name,
            Description = pool.Description,
            Source = ResourceSource.VdiClone,
            Port = pool.Port,
            OsType = pool.OsType,
            DefaultConnectionDefaults = pool.ConnectionDefaults,
        };
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

        // The user's own provisioned VDI clones (a dedicated pool desktop, once it exists).
        var ownClones = await _db.VdiInstances
            .Where(i => i.OwnerUserId == userId && i.RDPResourceId != null
                && i.State != VdiInstanceState.Deprovisioning && i.State != VdiInstanceState.Failed)
            .Select(i => i.RDPResourceId!)
            .ToListAsync(ct);

        var set = new HashSet<string>(direct);
        set.UnionWith(viaGroups);
        set.UnionWith(ownClones);
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
    /// Every VDI pool a user is entitled to, each tagged Direct (a direct user assignment) and/or via
    /// which group(s). The single source for the user page's "Desktop pools" tab and the pool page's
    /// assignment view, so provenance can't drift — the pool analogue of <see cref="GetUserAccessAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<PoolAccessEntry>> GetUserPoolAccessAsync(string userId, CancellationToken ct = default)
    {
        var directPoolIds = await _db.VdiPoolAssignments
            .Where(a => a.UserId == userId)
            .Select(a => a.PoolId)
            .ToListAsync(ct);

        var viaGroups = await _db.UserGroupMemberships
            .Where(m => m.UserId == userId)
            .Join(_db.VdiPoolAssignments.Where(a => a.GroupId != null), m => m.GroupId, a => a.GroupId,
                (m, a) => new { a.PoolId, a.Group!.Id, a.Group.Name })
            .ToListAsync(ct);

        var poolIds = directPoolIds.Concat(viaGroups.Select(g => g.PoolId)).ToHashSet();
        var poolsById = await _db.VdiPools
            .Where(p => poolIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var groupsByPool = viaGroups
            .GroupBy(g => g.PoolId)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.Id, x.Name)).Distinct().ToList());
        var directSet = directPoolIds.ToHashSet();

        var entries = new List<PoolAccessEntry>();
        foreach (var pid in poolIds)
        {
            if (!poolsById.TryGetValue(pid, out var pool)) continue;
            entries.Add(new PoolAccessEntry(
                pool,
                Direct: directSet.Contains(pid),
                ViaGroups: groupsByPool.TryGetValue(pid, out var gs) ? gs : new()));
        }
        return entries.OrderBy(e => e.Pool.Name).ToList();
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

/// <summary>One VDI pool a user is entitled to, with the provenance of that entitlement.</summary>
public sealed record PoolAccessEntry(
    VdiPool Pool,
    bool Direct,
    List<(string Id, string Name)> ViaGroups);
