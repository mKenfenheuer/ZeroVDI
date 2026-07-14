using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// Admin CRUD for VDI pools: clone-from-template provisioning policies plus the users/groups entitled
/// to them. A pool clones a desktop from a Proxmox template on first connect (see
/// <see cref="VdiProvisioningService"/>); entitlement is direct ∪ group, resolved through
/// <see cref="ResourceAccessService"/> like resource access. Admin-only; all mutations audited.
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin/vdi-pools")]
public class VdiPoolsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAuditLogger _audit;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;
    private readonly CredentialProtector _credentials;
    private readonly VdiProvisioningService _provisioning;

    public VdiPoolsController(ApplicationDbContext context, IAuditLogger audit,
        ProxmoxClient proxmox, ProxmoxBackendProvider backends, CredentialProtector credentials,
        VdiProvisioningService provisioning)
    {
        _context = context;
        _audit = audit;
        _proxmox = proxmox;
        _backends = backends;
        _credentials = credentials;
        _provisioning = provisioning;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? q)
    {
        var query = _context.VdiPools
            .Include(p => p.Assignments)
            .Include(p => p.Instances)
            .Include(p => p.ProxmoxBackend)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(p =>
                EF.Functions.Like(p.Name, $"%{term}%")
                || (p.ProxmoxBackend != null && EF.Functions.Like(p.ProxmoxBackend.Name, $"%{term}%")));
        }
        ViewData["Query"] = q;
        var pools = await query.OrderBy(p => p.Name).ToListAsync();
        return View(pools);
    }

    [HttpGet("create")]
    public async Task<IActionResult> Create()
    {
        ViewBag.Backends = await _context.ProxmoxBackends.OrderBy(b => b.Name).ToListAsync();
        return View(new VdiPool());
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        [Bind("Name,Description,ProxmoxBackendId,TemplateVmId,Kind,CloneMode,OsType,Port")] VdiPool pool)
    {
        if (await _context.VdiPools.AnyAsync(p => p.Name == pool.Name))
            ModelState.AddModelError(nameof(pool.Name), "A pool with that name already exists.");
        if (!await _context.ProxmoxBackends.AnyAsync(b => b.Id == pool.ProxmoxBackendId))
            ModelState.AddModelError(nameof(pool.ProxmoxBackendId), "Choose a backend.");
        if (pool.TemplateVmId <= 0)
            ModelState.AddModelError(nameof(pool.TemplateVmId), "Choose a template.");

        if (!ModelState.IsValid)
        {
            ViewBag.Backends = await _context.ProxmoxBackends.OrderBy(b => b.Name).ToListAsync();
            return View(pool);
        }

        _context.VdiPools.Add(pool);
        await _context.SaveChangesAsync();
        await _audit.LogAsync(AuditCategory.Authorization, "VdiPoolCreated",
            targetType: nameof(VdiPool), targetId: pool.Id, targetName: pool.Name);
        return RedirectToAction(nameof(Manage), new { id = pool.Id });
    }

    /// <summary>Lists the templates available on a backend (for the create/manage template picker).</summary>
    [HttpGet("templates/{backendId:int}")]
    public async Task<IActionResult> Templates(int backendId)
    {
        var backend = await _backends.GetAsync(backendId);
        if (backend == null) return NotFound();
        var templates = await _proxmox.ListTemplatesAsync(backend);
        return Json(templates.Select(t => new { t.VmId, t.Node, t.Name }).OrderBy(t => t.Name));
    }

    [HttpGet("manage/{id}")]
    public async Task<IActionResult> Manage(string id)
    {
        var pool = await _context.VdiPools
            .Include(p => p.ProxmoxBackend)
            .Include(p => p.Assignments).ThenInclude(a => a.User)
            .Include(p => p.Assignments).ThenInclude(a => a.Group)
            .Include(p => p.Instances).ThenInclude(i => i.OwnerUser)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (pool == null) return NotFound();

        var assignedUserIds = pool.Assignments.Where(a => a.UserId != null).Select(a => a.UserId!).ToHashSet();
        var assignedGroupIds = pool.Assignments.Where(a => a.GroupId != null).Select(a => a.GroupId!).ToHashSet();

        ViewBag.UnassignedUsers = await _context.Users
            .Where(u => !assignedUserIds.Contains(u.Id)).OrderBy(u => u.UserName).ToListAsync();
        ViewBag.UnassignedGroups = await _context.UserGroups
            .Where(g => !assignedGroupIds.Contains(g.Id)).OrderBy(g => g.Name).ToListAsync();
        ViewBag.Templates = await _proxmox.ListTemplatesAsync(pool.ProxmoxBackend!);
        return View(pool);
    }

    [HttpPost("update/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string id, VdiPool form)
    {
        var pool = await _context.VdiPools.FirstOrDefaultAsync(p => p.Id == id);
        if (pool == null) return NotFound();

        if (string.IsNullOrWhiteSpace(form.Name))
        {
            TempData["Error"] = "Pool name is required.";
            return RedirectToAction(nameof(Manage), new { id });
        }
        if (await _context.VdiPools.AnyAsync(p => p.Name == form.Name && p.Id != id))
        {
            TempData["Error"] = "Another pool already has that name.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        pool.Name = form.Name;
        pool.Description = form.Description;
        pool.TemplateVmId = form.TemplateVmId;
        pool.Kind = form.Kind;
        pool.CloneMode = form.CloneMode;
        pool.OsType = form.OsType;
        pool.Port = form.Port;
        pool.TargetNode = string.IsNullOrWhiteSpace(form.TargetNode) ? null : form.TargetNode.Trim();
        pool.TargetStorage = string.IsNullOrWhiteSpace(form.TargetStorage) ? null : form.TargetStorage.Trim();
        pool.VmidRangeStart = form.VmidRangeStart;
        pool.VmidRangeEnd = form.VmidRangeEnd;
        pool.NamePattern = string.IsNullOrWhiteSpace(form.NamePattern) ? "{pool}-{user}" : form.NamePattern.Trim();
        pool.MaxSize = form.MaxSize;

        // Identity / customization.
        pool.IdentityMode = form.IdentityMode;
        pool.HostnamePattern = string.IsNullOrWhiteSpace(form.HostnamePattern) ? null : form.HostnamePattern.Trim();
        pool.GenerateCredentials = form.GenerateCredentials;
        pool.CiUser = string.IsNullOrWhiteSpace(form.CiUser) ? null : form.CiUser.Trim();
        pool.CiSshKeys = string.IsNullOrWhiteSpace(form.CiSshKeys) ? null : form.CiSshKeys;
        pool.DomainName = string.IsNullOrWhiteSpace(form.DomainName) ? null : form.DomainName.Trim();
        pool.DomainOu = string.IsNullOrWhiteSpace(form.DomainOu) ? null : form.DomainOu.Trim();
        pool.DomainJoinUser = string.IsNullOrWhiteSpace(form.DomainJoinUser) ? null : form.DomainJoinUser.Trim();

        // Secrets: only overwrite when a new value is supplied (the form posts blank to keep the
        // existing one). Stored encrypted via CredentialProtector (never rendered back to the browser).
        if (!string.IsNullOrEmpty(form.ProtectedCiPassword))
            pool.ProtectedCiPassword = _credentials.Protect(form.ProtectedCiPassword);
        if (!string.IsNullOrEmpty(form.ProtectedDomainJoinPassword))
            pool.ProtectedDomainJoinPassword = _credentials.Protect(form.ProtectedDomainJoinPassword);

        await _context.SaveChangesAsync();
        await _audit.LogAsync(AuditCategory.Authorization, "VdiPoolUpdated",
            targetType: nameof(VdiPool), targetId: pool.Id, targetName: pool.Name);
        TempData["Status"] = "Pool updated.";
        return RedirectToAction(nameof(Manage), new { id });
    }

    [HttpPost("delete/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id)
    {
        var pool = await _context.VdiPools.Include(p => p.Instances).FirstOrDefaultAsync(p => p.Id == id);
        if (pool == null) return NotFound();

        // Deleting a pool cascades to its provisioned desktops: each instance's Proxmox VM is destroyed
        // ONLY after verifying it is really that clone (VMIDs get recycled / nodes drift), and its clone
        // RDPResource + authorizations are removed so no orphaned VdiClone row is left behind (which
        // previously caused an FK error when the pool row went away).
        var backend = await _backends.GetAsync(pool.ProxmoxBackendId);
        var desktopCount = pool.Instances.Count;
        var resourceIds = pool.Instances.Where(i => i.RDPResourceId != null).Select(i => i.RDPResourceId!).ToList();

        int mismatched = 0;
        if (backend != null)
        {
            foreach (var instance in pool.Instances)
            {
                var outcome = await _provisioning.SafeDestroyInstanceVmAsync(backend, instance);
                if (outcome == VdiProvisioningService.DestroyOutcome.Error)
                {
                    // Backend unreachable mid-teardown — abort so we never delete the pool while its VMs
                    // may still exist. Nothing has been removed from the DB at this point.
                    TempData["Error"] = "Could not reach the backend to verify the pool's VMs. Nothing was changed — try again.";
                    return RedirectToAction(nameof(Manage), new { id });
                }
                if (outcome == VdiProvisioningService.DestroyOutcome.IdentityMismatch) mismatched++;
            }
        }

        if (resourceIds.Count > 0)
        {
            var resources = await _context.RDPResources.Where(r => resourceIds.Contains(r.Id)).ToListAsync();
            _context.RDPResources.RemoveRange(resources);
            var auths = await _context.RDPResourceUserAuthorizations
                .Where(a => a.RDPResourceId != null && resourceIds.Contains(a.RDPResourceId)).ToListAsync();
            _context.RDPResourceUserAuthorizations.RemoveRange(auths);
        }

        _context.VdiInstances.RemoveRange(pool.Instances);
        _context.VdiPools.Remove(pool); // assignments cascade
        await _context.SaveChangesAsync();
        await _audit.LogAsync(AuditCategory.Authorization, "VdiPoolDeleted",
            targetType: nameof(VdiPool), targetId: id, targetName: pool.Name,
            detail: new { DesktopsRemoved = desktopCount, VmsSkippedIdentityMismatch = mismatched });
        if (mismatched > 0)
            TempData["Error"] = $"Pool “{pool.Name}” deleted, but {mismatched} VM(s) did not match their desktop identity and were left untouched — verify no orphaned VMs remain.";
        else
            TempData["Status"] = $"Pool “{pool.Name}” and its desktops deleted.";
        return RedirectToAction(nameof(Index));
    }

    // --- Assignments (direct user OR group) ---

    [HttpPost("{id}/assignments/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAssignment(string id, string? userId, string? groupId)
    {
        var pool = await _context.VdiPools.FindAsync(id);
        if (pool == null) return NotFound();
        if (string.IsNullOrEmpty(userId) == string.IsNullOrEmpty(groupId))
        {
            TempData["Error"] = "Pick exactly one user or group to assign.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        var exists = await _context.VdiPoolAssignments
            .AnyAsync(a => a.PoolId == id && a.UserId == userId && a.GroupId == groupId);
        var principalOk = userId != null
            ? await _context.Users.AnyAsync(u => u.Id == userId)
            : await _context.UserGroups.AnyAsync(g => g.Id == groupId);
        if (!exists && principalOk)
        {
            _context.VdiPoolAssignments.Add(new VdiPoolAssignment { PoolId = id, UserId = userId, GroupId = groupId });
            await _context.SaveChangesAsync();
            await _audit.LogAsync(AuditCategory.Authorization, "VdiPoolAssignmentGranted",
                targetType: nameof(VdiPool), targetId: id, targetName: pool.Name,
                detail: new { userId, groupId });
        }
        return RedirectToAction(nameof(Manage), new { id });
    }

    [HttpPost("{id}/assignments/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAssignment(string id, string assignmentId)
    {
        var pool = await _context.VdiPools.FindAsync(id);
        if (pool == null) return NotFound();
        var a = await _context.VdiPoolAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.PoolId == id);
        if (a != null)
        {
            _context.VdiPoolAssignments.Remove(a);
            await _context.SaveChangesAsync();
            await _audit.LogAsync(AuditCategory.Authorization, "VdiPoolAssignmentRevoked",
                targetType: nameof(VdiPool), targetId: id, targetName: pool.Name,
                detail: new { a.UserId, a.GroupId });
        }
        return RedirectToAction(nameof(Manage), new { id });
    }

    // --- Instance lifecycle ---

    /// <summary>
    /// Tears down a provisioned desktop: destroys the Proxmox VM and removes the instance + its
    /// resource row. (The reconcile loop in a later slice automates this for lost assignments; this is
    /// the manual admin action.)
    /// </summary>
    [HttpPost("{id}/instances/deprovision")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deprovision(string id, string instanceId)
    {
        var pool = await _context.VdiPools.FindAsync(id);
        if (pool == null) return NotFound();
        var instance = await _context.VdiInstances.FirstOrDefaultAsync(i => i.Id == instanceId && i.PoolId == id);
        if (instance == null) return RedirectToAction(nameof(Manage), new { id });

        var priorState = instance.State;
        instance.State = VdiInstanceState.Deprovisioning;
        await _context.SaveChangesAsync();

        var backend = await _backends.GetAsync(pool.ProxmoxBackendId);
        if (backend != null)
        {
            // Verify the VM is really this clone before destroying anything — VMIDs get recycled and the
            // recorded node can be stale after migration, so a blind destroy-by-VMID can wipe an unrelated VM.
            var outcome = await _provisioning.SafeDestroyInstanceVmAsync(backend, instance);
            if (outcome == VdiProvisioningService.DestroyOutcome.Error)
            {
                // Backend unreachable / probe failed — we don't know the VM's fate. Abort and keep all rows.
                instance.State = priorState;
                await _context.SaveChangesAsync();
                TempData["Error"] = "Could not reach the backend to verify the VM. Nothing was changed — try again.";
                return RedirectToAction(nameof(Manage), new { id });
            }
            if (outcome == VdiProvisioningService.DestroyOutcome.IdentityMismatch)
            {
                // A different VM now sits at this VMID (or the notes binding is gone). We refuse to destroy
                // it. The DB rows are still removed below so the stale instance is cleared, but we warn the
                // admin to check for an orphaned VM rather than assume it was deleted.
                TempData["Error"] = $"VMID {instance.ProxmoxVmId} did not match this desktop's identity, so no VM " +
                    "was destroyed (it may have been deleted or its ID reused). The record was removed — verify no orphaned VM remains.";
            }
        }

        if (instance.RDPResourceId != null)
        {
            var res = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == instance.RDPResourceId);
            if (res != null) _context.RDPResources.Remove(res);
            var auths = await _context.RDPResourceUserAuthorizations
                .Where(au => au.RDPResourceId == instance.RDPResourceId).ToListAsync();
            _context.RDPResourceUserAuthorizations.RemoveRange(auths);
        }
        _context.VdiInstances.Remove(instance);
        await _context.SaveChangesAsync();

        await _audit.LogAsync(AuditCategory.Authorization, "VdiInstanceDeprovisioned",
            targetType: nameof(VdiPool), targetId: id, targetName: pool.Name,
            detail: new { instance.ProxmoxVmId, instance.OwnerUserId });
        if (TempData["Error"] == null) TempData["Status"] = "Desktop deprovisioned.";
        return RedirectToAction(nameof(Manage), new { id });
    }
}
