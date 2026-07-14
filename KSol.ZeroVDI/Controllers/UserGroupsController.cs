using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// Admin CRUD for user groups, their memberships, and the resources each group grants. A group is an
/// authorization grouping: granting a resource to a group gives every member the right to connect (the
/// access gates resolve direct ∪ group via <see cref="ResourceAccessService"/>). Personal SSO credentials
/// and per-user console defaults are never group state. Admin-only; all mutations audited.
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin/groups")]
public class UserGroupsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAuditLogger _audit;

    public UserGroupsController(ApplicationDbContext context, IAuditLogger audit)
    {
        _context = context;
        _audit = audit;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? q)
    {
        var query = _context.UserGroups
            .Include(g => g.Memberships)
            .Include(g => g.ResourceAuthorizations)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(g =>
                EF.Functions.Like(g.Name, $"%{term}%")
                || (g.Description != null && EF.Functions.Like(g.Description, $"%{term}%")));
        }
        ViewData["Query"] = q;
        var groups = await query.OrderBy(g => g.Name).ToListAsync();
        return View(groups);
    }

    [HttpGet("create")]
    public IActionResult Create() => View(new UserGroup());

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Name,Description")] UserGroup group)
    {
        if (await _context.UserGroups.AnyAsync(g => g.Name == group.Name))
            ModelState.AddModelError(nameof(group.Name), "A group with that name already exists.");
        if (!ModelState.IsValid) return View(group);

        _context.UserGroups.Add(group);
        await _context.SaveChangesAsync();
        await _audit.LogAsync(AuditCategory.Authorization, "GroupCreated",
            targetType: nameof(UserGroup), targetId: group.Id, targetName: group.Name);
        return RedirectToAction(nameof(Manage), new { id = group.Id });
    }

    // The combined manage page: rename, members, and resource grants.
    [HttpGet("manage/{id}")]
    public async Task<IActionResult> Manage(string id)
    {
        var group = await _context.UserGroups
            .Include(g => g.Memberships).ThenInclude(m => m.User)
            .Include(g => g.ResourceAuthorizations).ThenInclude(a => a.RDPResource)
            .FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return NotFound();

        var memberIds = group.Memberships.Select(m => m.UserId).ToHashSet();
        var grantedResourceIds = group.ResourceAuthorizations.Select(a => a.RDPResourceId).ToHashSet();

        ViewBag.NonMembers = await _context.Users
            .Where(u => !memberIds.Contains(u.Id))
            .OrderBy(u => u.UserName).ToListAsync();
        // VDI clones are not independently grantable (access flows through pool assignment), so keep
        // them out of the resource-grant picker.
        ViewBag.UngrantedResources = await _context.RDPResources
            .Where(r => r.Source != ResourceSource.VdiClone && !grantedResourceIds.Contains(r.Id))
            .OrderBy(r => r.Name).ToListAsync();

        // Group → VDI pool assignments (every member is entitled to the pool).
        ViewBag.AssignedPools = await _context.VdiPoolAssignments
            .Where(a => a.GroupId == id)
            .Include(a => a.Pool)
            .Select(a => a.Pool!)
            .OrderBy(p => p.Name).ToListAsync();
        var assignedPoolIds = await _context.VdiPoolAssignments
            .Where(a => a.GroupId == id).Select(a => a.PoolId).ToListAsync();
        ViewBag.UnassignedPools = await _context.VdiPools
            .Where(p => !assignedPoolIds.Contains(p.Id))
            .OrderBy(p => p.Name).ToListAsync();
        return View(group);
    }

    [HttpPost("rename/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(string id, string name, string? description)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null) return NotFound();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Group name is required.";
            return RedirectToAction(nameof(Manage), new { id });
        }
        if (await _context.UserGroups.AnyAsync(g => g.Name == name && g.Id != id))
        {
            TempData["Error"] = "Another group already has that name.";
            return RedirectToAction(nameof(Manage), new { id });
        }
        group.Name = name;
        group.Description = description;
        await _context.SaveChangesAsync();
        await _audit.LogAsync(AuditCategory.Authorization, "GroupUpdated",
            targetType: nameof(UserGroup), targetId: group.Id, targetName: group.Name);
        TempData["Status"] = "Group updated.";
        return RedirectToAction(nameof(Manage), new { id });
    }

    [HttpPost("delete/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null) return NotFound();
        _context.UserGroups.Remove(group); // memberships + grants cascade
        await _context.SaveChangesAsync();
        await _audit.LogAsync(AuditCategory.Authorization, "GroupDeleted",
            targetType: nameof(UserGroup), targetId: id, targetName: group.Name);
        TempData["Status"] = $"Group “{group.Name}” deleted.";
        return RedirectToAction(nameof(Index));
    }

    // --- Membership ---

    [HttpPost("{id}/members/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(string id, string userId)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null) return NotFound();
        var exists = await _context.UserGroupMemberships.AnyAsync(m => m.GroupId == id && m.UserId == userId);
        if (!exists && await _context.Users.AnyAsync(u => u.Id == userId))
        {
            _context.UserGroupMemberships.Add(new UserGroupMembership { GroupId = id, UserId = userId });
            await _context.SaveChangesAsync();
            await _audit.LogAsync(AuditCategory.Authorization, "GroupMemberAdded",
                targetType: nameof(UserGroup), targetId: id, targetName: group.Name,
                detail: new { userId });
        }
        return RedirectToAction(nameof(Manage), new { id });
    }

    [HttpPost("{id}/members/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(string id, string userId)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null) return NotFound();
        var m = await _context.UserGroupMemberships.FirstOrDefaultAsync(x => x.GroupId == id && x.UserId == userId);
        if (m != null)
        {
            _context.UserGroupMemberships.Remove(m);
            await _context.SaveChangesAsync();
            await _audit.LogAsync(AuditCategory.Authorization, "GroupMemberRemoved",
                targetType: nameof(UserGroup), targetId: id, targetName: group.Name,
                detail: new { userId });
        }
        return RedirectToAction(nameof(Manage), new { id });
    }

    // --- Resource grants ---

    [HttpPost("{id}/resources/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddResource(string id, string resourceId)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null) return NotFound();
        var exists = await _context.RDPResourceGroupAuthorizations
            .AnyAsync(a => a.GroupId == id && a.RDPResourceId == resourceId);
        if (!exists && await _context.RDPResources.AnyAsync(r => r.Id == resourceId))
        {
            _context.RDPResourceGroupAuthorizations.Add(
                new RDPResourceGroupAuthorization { GroupId = id, RDPResourceId = resourceId });
            await _context.SaveChangesAsync();
            await _audit.LogAsync(AuditCategory.Authorization, "GroupAccessGranted",
                targetType: nameof(RDPResource), targetId: resourceId,
                detail: new { groupId = id, groupName = group.Name });
        }
        return RedirectToAction(nameof(Manage), new { id });
    }

    [HttpPost("{id}/resources/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveResource(string id, string resourceId)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null) return NotFound();
        var a = await _context.RDPResourceGroupAuthorizations
            .FirstOrDefaultAsync(x => x.GroupId == id && x.RDPResourceId == resourceId);
        if (a != null)
        {
            _context.RDPResourceGroupAuthorizations.Remove(a);
            await _context.SaveChangesAsync();
            await _audit.LogAsync(AuditCategory.Authorization, "GroupAccessRevoked",
                targetType: nameof(RDPResource), targetId: resourceId,
                detail: new { groupId = id, groupName = group.Name });
        }
        return RedirectToAction(nameof(Manage), new { id });
    }

    // --- VDI pool assignments (every member is entitled to the pool) ---

    [HttpPost("{id}/pools/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPool(string id, string poolId)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null) return NotFound();
        var exists = await _context.VdiPoolAssignments.AnyAsync(a => a.PoolId == poolId && a.GroupId == id);
        if (!exists && await _context.VdiPools.AnyAsync(p => p.Id == poolId))
        {
            _context.VdiPoolAssignments.Add(new VdiPoolAssignment { PoolId = poolId, GroupId = id });
            await _context.SaveChangesAsync();
            await _audit.LogAsync(AuditCategory.Authorization, "VdiPoolAssignmentGranted",
                targetType: nameof(VdiPool), targetId: poolId,
                detail: new { groupId = id, groupName = group.Name });
        }
        return RedirectToAction(nameof(Manage), new { id });
    }

    [HttpPost("{id}/pools/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemovePool(string id, string poolId)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null) return NotFound();
        var a = await _context.VdiPoolAssignments.FirstOrDefaultAsync(x => x.PoolId == poolId && x.GroupId == id);
        if (a != null)
        {
            _context.VdiPoolAssignments.Remove(a);
            await _context.SaveChangesAsync();
            await _audit.LogAsync(AuditCategory.Authorization, "VdiPoolAssignmentRevoked",
                targetType: nameof(VdiPool), targetId: poolId,
                detail: new { groupId = id, groupName = group.Name });
        }
        return RedirectToAction(nameof(Manage), new { id });
    }
}
