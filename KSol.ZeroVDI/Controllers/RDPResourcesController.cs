using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("admin/resources")]
    public class RDPResourcesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxClient _proxmox;
        private readonly ProxmoxBackendProvider _backends;
        private readonly CredentialProtector _credentials;
        private readonly ResourceShutdownService _shutdown;
        private readonly ResourceAccessService _access;
        private readonly IAuditLogger _audit;
        private readonly Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> _userManager;

        public RDPResourcesController(
            ApplicationDbContext context,
            ProxmoxClient proxmox,
            ProxmoxBackendProvider backends,
            CredentialProtector credentials,
            ResourceShutdownService shutdown,
            ResourceAccessService access,
            IAuditLogger audit,
            Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _proxmox = proxmox;
            _backends = backends;
            _credentials = credentials;
            _shutdown = shutdown;
            _access = access;
            _audit = audit;
            _userManager = userManager;
        }

        // GET: /admin/resources
        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            return View(await _context.RDPResources.ToListAsync());
        }

        // GET: /admin/resources/details/5
        // The standalone Details view was merged into the tabbed Edit page (its "Backend & VM" tab
        // shows everything Details did). Kept as a redirect so old links keep working.
        [HttpGet("details/{id}")]
        public IActionResult Details(string id) => RedirectToAction(nameof(Edit), new { id });

        // GET: /admin/resources/create
        [HttpGet("create")]
        public IActionResult Create()
        {
            return View(new RDPResource());
        }

        // POST: /admin/resources/create
        // Manual resources only: an admin supplies the address and options. Proxmox-backed resources
        // are created by the sync service, not here. Id is server-generated (GUID).
        [HttpPost("create")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Name,Description,IpAddress,Port,Protocol,KeyboardLayout,OsType,WakeMethod,WolMacAddress,IpmiHost,IpmiUser,ShutdownMethod,SshUser,ShutdownCommand,WindowsUser,DefaultConnectionDefaults")] RDPResource rDPResource, string? ipmiPassword, string? sshKey, string? windowsPassword)
        {
            if (ModelState.IsValid)
            {
                rDPResource.Source = ResourceSource.Manual;
                if (!string.IsNullOrEmpty(ipmiPassword))
                    rDPResource.ProtectedIpmiPassword = _credentials.Protect(ipmiPassword);
                if (!string.IsNullOrEmpty(sshKey))
                    rDPResource.ProtectedSshKey = _credentials.Protect(sshKey);
                if (!string.IsNullOrEmpty(windowsPassword))
                    rDPResource.ProtectedWindowsPassword = _credentials.Protect(windowsPassword);
                _context.Add(rDPResource);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(rDPResource);
        }

        // GET: /admin/resources/edit/5
        // The resource editor is the primary management surface: general settings, backend/VM info,
        // current state, assigned users + authorizations, connection defaults, stored credentials and
        // an audit/history tab — all in one tabbed page. Authorizations no longer have their own nav.
        [HttpGet("edit/{id}")]
        public async Task<IActionResult> Edit(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var resource = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (resource == null)
            {
                return NotFound();
            }

            return View(await BuildEditViewModelAsync(resource));
        }

        private async Task<ResourceEditViewModel> BuildEditViewModelAsync(RDPResource resource)
        {
            var authorizations = await _context.RDPResourceUserAuthorizations
                .Include(a => a.User)
                .Where(a => a.RDPResourceId == resource.Id)
                .ToListAsync();

            var assignedUserIds = authorizations.Select(a => a.UserId).ToHashSet();
            var availableUsers = await _userManager.Users
                .Where(u => !assignedUserIds.Contains(u.Id))
                .OrderBy(u => u.UserName)
                .ToListAsync();

            var recordings = await _context.Recordings
                .Where(r => r.RDPResourceId == resource.Id)
                .OrderByDescending(r => r.StartedUtc)
                .Take(20)
                .ToListAsync();

            var backend = resource.ProxmoxBackendId is { } bid
                ? await _backends.GetAsync(bid)
                : null;

            // Full provenance (direct ∪ group) for the access tab, plus the group-grant controls.
            var access = await _access.GetResourceAccessAsync(resource.Id);

            var grantedGroups = await _context.RDPResourceGroupAuthorizations
                .Include(g => g.Group)
                .Where(g => g.RDPResourceId == resource.Id)
                .Select(g => g.Group!)
                .OrderBy(g => g.Name)
                .ToListAsync();
            var grantedGroupIds = grantedGroups.Select(g => g.Id).ToHashSet();
            var availableGroups = await _context.UserGroups
                .Where(g => !grantedGroupIds.Contains(g.Id))
                .OrderBy(g => g.Name)
                .ToListAsync();

            return new ResourceEditViewModel
            {
                Resource = resource,
                Authorizations = authorizations,
                AvailableUsers = availableUsers,
                Recordings = recordings,
                Backend = backend,
                Access = access,
                GrantedGroups = grantedGroups,
                AvailableGroups = availableGroups,
                AvailableConnectors = await _context.Connectors.OrderBy(c => c.Name).ToListAsync(),
            };
        }

        // POST: RDPResources/Edit/5
        // Load the tracked entity and apply only the editable fields. For Proxmox resources the
        // address/options are owned by the sync (read-only here), so only name/description are
        // applied; for Manual resources the address, port and RDP options are editable too.
        [HttpPost("edit/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("Id,Name,Description,IpAddress,Port,Protocol,KeyboardLayout,OsType,WakeMethod,WolMacAddress,IpmiHost,IpmiUser,ShutdownMethod,SshUser,ShutdownCommand,WindowsUser,ForcedConnectorId")] RDPResource input, string? ipmiPassword, string? sshKey, string? windowsPassword)
        {
            if (id != input.Id)
            {
                return NotFound();
            }

            var existing = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (existing == null)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                existing.Name = input.Name;
                existing.Description = input.Description;
                if (existing.Source == ResourceSource.Manual)
                {
                    existing.IpAddress = input.IpAddress;
                    existing.Port = input.Port;
                    existing.Protocol = input.Protocol;
                    existing.KeyboardLayout = input.KeyboardLayout;
                    existing.OsType = input.OsType;
                    existing.WakeMethod = input.WakeMethod;
                    existing.WolMacAddress = input.WolMacAddress;
                    existing.IpmiHost = input.IpmiHost;
                    existing.IpmiUser = input.IpmiUser;
                    if (!string.IsNullOrEmpty(ipmiPassword))
                        existing.ProtectedIpmiPassword = _credentials.Protect(ipmiPassword);
                    existing.ShutdownMethod = input.ShutdownMethod;
                    existing.SshUser = input.SshUser;
                    if (!string.IsNullOrEmpty(sshKey))
                        existing.ProtectedSshKey = _credentials.Protect(sshKey);
                    existing.ShutdownCommand = input.ShutdownCommand;
                    existing.WindowsUser = input.WindowsUser;
                    if (!string.IsNullOrEmpty(windowsPassword))
                        existing.ProtectedWindowsPassword = _credentials.Protect(windowsPassword);
                }
                // Forced connector applies regardless of source (it pins the network path, not the machine).
                existing.ForcedConnectorId = string.IsNullOrEmpty(input.ForcedConnectorId) ? null : input.ForcedConnectorId;
                await _context.SaveChangesAsync();
                TempData["Status"] = "Resource saved.";
                return RedirectToAction(nameof(Edit), new { id });
            }
            return View(await BuildEditViewModelAsync(existing));
        }

        // POST: /admin/resources/{id}/stop — on-demand graceful shutdown of a running resource.
        // Manual resources use their configured ShutdownMethod (SSH/IPMI/Windows); Proxmox resources
        // are stopped through the backend API.
        [HttpPost("{id}/stop")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Stop(string id)
        {
            var res = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (res == null) return NotFound();

            bool ok;
            if (res.Source == ResourceSource.Proxmox && res.ProxmoxBackendId != null
                && res.ProxmoxNode != null && res.ProxmoxVmId != null)
            {
                var backend = await _backends.GetAsync(res.ProxmoxBackendId.Value);
                ok = backend != null && backend.IsConfigured
                    && await _proxmox.StopAsync(backend, res.ProxmoxNode, res.ProxmoxVmId.Value);
            }
            else
            {
                ok = await _shutdown.ShutDownAsync(res);
            }

            if (ok)
            {
                res.PowerState = ResourcePowerState.Stopped;
                await _context.SaveChangesAsync();
                TempData["Status"] = $"Shutdown requested for \"{res.Name}\".";
            }
            else
            {
                TempData["Error"] = $"Could not shut down \"{res.Name}\" — check its shutdown configuration.";
            }
            return RedirectToAction(nameof(Edit), new { id });
        }

        // --- Assigned users + authorizations (folded into the resource editor) ---

        // POST: /admin/resources/{id}/grant — authorize a user for this resource.
        [HttpPost("{id}/grant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Grant(string id, string userId)
        {
            var resource = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (resource == null) return NotFound();

            if (string.IsNullOrEmpty(userId))
            {
                TempData["Error"] = "Select a user to grant access.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            var exists = await _context.RDPResourceUserAuthorizations
                .AnyAsync(a => a.RDPResourceId == id && a.UserId == userId);
            if (!exists)
            {
                var auth = new RDPResourceUserAuthorization
                {
                    RDPResourceId = id,
                    UserId = userId,
                };
                if (resource.DefaultConnectionDefaults != null)
                {
                    auth.ConnectionDefaults = new ConnectionDefaults
                    {
                        Audio = resource.DefaultConnectionDefaults.Audio,
                        Clipboard = resource.DefaultConnectionDefaults.Clipboard,
                        Microphone = resource.DefaultConnectionDefaults.Microphone,
                        Camera = resource.DefaultConnectionDefaults.Camera,
                        GfxMode = resource.DefaultConnectionDefaults.GfxMode,
                        PerformanceFlags = resource.DefaultConnectionDefaults.PerformanceFlags,
                    };
                }
                _context.RDPResourceUserAuthorizations.Add(auth);
                await _context.SaveChangesAsync();
                TempData["Status"] = "Access granted.";
            }
            return RedirectToAction(nameof(Edit), new { id });
        }

        // POST: /admin/resources/{id}/revoke/{authId} — remove an authorization.
        [HttpPost("{id}/revoke/{authId}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Revoke(string id, string authId)
        {
            var auth = await _context.RDPResourceUserAuthorizations
                .FirstOrDefaultAsync(a => a.Id == authId && a.RDPResourceId == id);
            if (auth != null)
            {
                _context.RDPResourceUserAuthorizations.Remove(auth);
                await _context.SaveChangesAsync();
                TempData["Status"] = "Access revoked.";
            }
            return RedirectToAction(nameof(Edit), new { id });
        }

        // POST: /admin/resources/{id}/grant-group — grant a whole group access to this resource.
        [HttpPost("{id}/grant-group")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GrantGroup(string id, string groupId)
        {
            var resource = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (resource == null) return NotFound();

            var group = await _context.UserGroups.FindAsync(groupId);
            if (group == null)
            {
                TempData["Error"] = "Select a group to grant.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            var exists = await _context.RDPResourceGroupAuthorizations
                .AnyAsync(a => a.RDPResourceId == id && a.GroupId == groupId);
            if (!exists)
            {
                _context.RDPResourceGroupAuthorizations.Add(
                    new RDPResourceGroupAuthorization { RDPResourceId = id, GroupId = groupId });
                await _context.SaveChangesAsync();
                await _audit.LogAsync(AuditCategory.Authorization, "GroupAccessGranted",
                    targetType: nameof(RDPResource), targetId: id, targetName: resource.Name,
                    detail: new { groupId, groupName = group.Name });
                TempData["Status"] = $"Granted access to group “{group.Name}”.";
            }
            return RedirectToAction(nameof(Edit), new { id });
        }

        // POST: /admin/resources/{id}/revoke-group — revoke a group's access to this resource.
        [HttpPost("{id}/revoke-group")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokeGroup(string id, string groupId)
        {
            var resource = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (resource == null) return NotFound();

            var a = await _context.RDPResourceGroupAuthorizations
                .Include(x => x.Group)
                .FirstOrDefaultAsync(x => x.RDPResourceId == id && x.GroupId == groupId);
            if (a != null)
            {
                _context.RDPResourceGroupAuthorizations.Remove(a);
                await _context.SaveChangesAsync();
                await _audit.LogAsync(AuditCategory.Authorization, "GroupAccessRevoked",
                    targetType: nameof(RDPResource), targetId: id, targetName: resource.Name,
                    detail: new { groupId, groupName = a.Group?.Name });
                TempData["Status"] = "Group access revoked.";
            }
            return RedirectToAction(nameof(Edit), new { id });
        }

        // POST: /admin/resources/{id}/credentials/{authId} — store (encrypted) VM credentials for one
        // user's authorization on this resource (single sign-on for the in-browser console).
        [HttpPost("{id}/credentials/{authId}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetCredentials(string id, string authId, string username, string password, string? domain)
        {
            var auth = await _context.RDPResourceUserAuthorizations
                .FirstOrDefaultAsync(a => a.Id == authId && a.RDPResourceId == id);
            if (auth == null) return NotFound();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                TempData["Error"] = "Username and password are both required to store credentials.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            auth.ProtectedUsername = _credentials.Protect(username);
            auth.ProtectedPassword = _credentials.Protect(password);
            auth.ProtectedDomain = _credentials.Protect(domain);
            await _context.SaveChangesAsync();
            TempData["Status"] = "Stored credentials updated.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // POST: /admin/resources/{id}/clear-credentials/{authId}
        [HttpPost("{id}/clear-credentials/{authId}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearCredentials(string id, string authId)
        {
            var auth = await _context.RDPResourceUserAuthorizations
                .FirstOrDefaultAsync(a => a.Id == authId && a.RDPResourceId == id);
            if (auth == null) return NotFound();

            auth.ProtectedUsername = null;
            auth.ProtectedPassword = null;
            auth.ProtectedDomain = null;
            await _context.SaveChangesAsync();
            TempData["Status"] = "Stored credentials cleared.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // POST: /admin/resources/edit/{id}/defaults — set resource-wide connection defaults.
        [HttpPost("edit/{id}/defaults")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetResourceDefaults(string id, ConnectionDefaults connectionDefaults)
        {
            var resource = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (resource == null) return NotFound();

            resource.DefaultConnectionDefaults = connectionDefaults;
            await _context.SaveChangesAsync();
            TempData["Status"] = "Connection defaults saved.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // POST: /admin/resources/{id}/defaults/{authId} — set per-user connection defaults.
        [HttpPost("{id}/defaults/{authId}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetConnectionDefaults(string id, string authId, ConnectionDefaults connectionDefaults)
        {
            var auth = await _context.RDPResourceUserAuthorizations
                .FirstOrDefaultAsync(a => a.Id == authId && a.RDPResourceId == id);
            if (auth == null) return NotFound();

            auth.ConnectionDefaults = connectionDefaults;
            await _context.SaveChangesAsync();
            TempData["Status"] = "User connection defaults saved.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // GET: /admin/resources/{id}/rrddata — Proxmox RRD time-series data (JSON).
        [HttpGet("{id}/rrddata")]
        public async Task<IActionResult> RrdData(string id, string timeframe = "hour")
        {
            var res = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (res == null) return NotFound();
            if (res.Source != ResourceSource.Proxmox || res.ProxmoxBackendId == null
                || res.ProxmoxNode == null || res.ProxmoxVmId == null)
                return Json(Array.Empty<object>());

            var backend = await _backends.GetAsync(res.ProxmoxBackendId.Value);
            if (backend == null || !backend.IsConfigured) return Json(Array.Empty<object>());

            var data = await _proxmox.GetRrdDataAsync(backend, res.ProxmoxNode, res.ProxmoxVmId.Value, timeframe);
            return Json(data);
        }

        // GET: /admin/resources/delete/5
        [HttpGet("delete/{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var rDPResource = await _context.RDPResources
                .FirstOrDefaultAsync(m => m.Id == id);
            if (rDPResource == null)
            {
                return NotFound();
            }

            return View(rDPResource);
        }

        // POST: /admin/resources/delete/5
        [HttpPost("delete/{id}"), ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var rDPResource = await _context.RDPResources.FindAsync(id);
            if (rDPResource != null)
            {
                // Clean up dependents first, or SQLite's FK enforcement rejects the delete.
                // Authorizations are pure access grants with no value once the resource is gone.
                var auths = await _context.RDPResourceUserAuthorizations
                    .Where(a => a.RDPResourceId == id)
                    .ToListAsync();
                _context.RDPResourceUserAuthorizations.RemoveRange(auths);

                // Recordings are an audit artifact (with files on disk) — preserve them by detaching
                // from the resource rather than cascade-deleting.
                var recordings = await _context.Recordings
                    .Where(r => r.RDPResourceId == id)
                    .ToListAsync();
                foreach (var rec in recordings)
                    rec.RDPResourceId = null;

                _context.RDPResources.Remove(rDPResource);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Index));
        }

        // POST: /admin/resources/exclude/{id}
        // Excludes a Proxmox VM from indexing: writes the exclude marker into the VM notes (so future
        // discovers skip it) and deletes the resource row and its authorizations.
        [HttpPost("exclude/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Exclude(string id)
        {
            var res = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (res == null)
            {
                return NotFound();
            }

            if (res.Source != ResourceSource.Proxmox || res.ProxmoxBackendId == null
                || res.ProxmoxNode == null || res.ProxmoxVmId == null)
            {
                TempData["Status"] = "Only Proxmox resources can be excluded.";
                return RedirectToAction(nameof(Index));
            }

            var backend = await _backends.GetAsync(res.ProxmoxBackendId.Value);
            if (backend == null || !backend.IsConfigured)
            {
                TempData["Status"] = "The resource's Proxmox backend is unavailable; cannot set the exclude marker.";
                return RedirectToAction(nameof(Index));
            }

            // Stamp the exclude marker into the VM notes (preserving other content).
            var notes = await _proxmox.GetNotesAsync(backend, res.ProxmoxNode, res.ProxmoxVmId.Value);
            var stamped = ProxmoxNotes.WriteExcluded(notes);
            var ok = await _proxmox.SetNotesAsync(backend, res.ProxmoxNode, res.ProxmoxVmId.Value, stamped);
            if (!ok)
            {
                TempData["Status"] = $"Could not write the exclude marker to VM {res.ProxmoxVmId} (check token permissions). Resource not removed.";
                return RedirectToAction(nameof(Index));
            }

            // Remove the row and its authorizations.
            var auths = await _context.RDPResourceUserAuthorizations
                .Where(a => a.RDPResourceId == res.Id)
                .ToListAsync();
            _context.RDPResourceUserAuthorizations.RemoveRange(auths);
            _context.RDPResources.Remove(res);
            await _context.SaveChangesAsync();

            TempData["Status"] = $"Excluded \"{res.Name}\" from indexing.";
            return RedirectToAction(nameof(Index));
        }

        private bool RDPResourceExists(string id)
        {
            return _context.RDPResources.Any(e => e.Id == id);
        }
    }

    /// <summary>Backing model for the tabbed resource editor (general/backend/state/users/audit).</summary>
    public class ResourceEditViewModel
    {
        public RDPResource Resource { get; set; } = null!;
        public List<RDPResourceUserAuthorization> Authorizations { get; set; } = new();
        public List<ApplicationUser> AvailableUsers { get; set; } = new();
        public List<Recording> Recordings { get; set; } = new();
        public ProxmoxBackend? Backend { get; set; }

        /// <summary>Every user who can reach this resource, with Direct/via-Group provenance.</summary>
        public IReadOnlyList<UserAccessEntry> Access { get; set; } = new List<UserAccessEntry>();
        /// <summary>Groups currently granted this resource.</summary>
        public List<UserGroup> GrantedGroups { get; set; } = new();
        /// <summary>Groups that could be granted this resource.</summary>
        public List<UserGroup> AvailableGroups { get; set; } = new();
        /// <summary>Connectors that can be pinned as this resource's forced connection path.</summary>
        public List<Connector> AvailableConnectors { get; set; } = new();
    }
}
