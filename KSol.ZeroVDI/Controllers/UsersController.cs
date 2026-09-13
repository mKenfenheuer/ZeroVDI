using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("admin/users")]
    public class UsersController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly ResourceAccessService _access;
        private readonly KSol.ZeroVDI.RDP.IAuditLogger _audit;
        private readonly AdminAccountSafety _safety;
        private readonly SessionTracker _sessions;
        private readonly Microsoft.AspNetCore.Identity.UI.Services.IEmailSender _email;
        private readonly PublicUrl _publicUrl;

        public UsersController(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager,
            ApplicationDbContext context, ResourceAccessService access, KSol.ZeroVDI.RDP.IAuditLogger audit,
            AdminAccountSafety safety, SessionTracker sessions,
            Microsoft.AspNetCore.Identity.UI.Services.IEmailSender email, PublicUrl publicUrl)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _access = access;
            _audit = audit;
            _safety = safety;
            _sessions = sessions;
            _email = email;
            _publicUrl = publicUrl;
        }

        /// <summary>The signed-in administrator's user id, used by the self-protection guards.</summary>
        private string? ActorId => _userManager.GetUserId(User);

        // GET: /admin/users
        [HttpGet("")]
        public async Task<IActionResult> Index(string? q)
        {
            var query = _userManager.Users.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(u =>
                    (u.UserName != null && EF.Functions.Like(u.UserName, $"%{term}%"))
                    || (u.Email != null && EF.Functions.Like(u.Email, $"%{term}%")));
            }

            var users = await query.OrderBy(u => u.UserName).ToListAsync();
            return View(new UsersIndexViewModel { Users = users, Query = q });
        }

        // GET: /admin/users/details/5
        // The standalone Details/AssignRoles views were merged into the tabbed Manage page.
        // Kept as a redirect so old links keep working.
        [HttpGet("details/{id}")]
        public IActionResult Details(string id) => RedirectToAction(nameof(Manage), new { id });

        // GET: /admin/users/manage/5
        // The user editor is the single management surface for a person: account, roles, group
        // memberships and effective resource access (direct ∪ group, with provenance) — all in one
        // tabbed page, mirroring the resource editor. Granting/revoking access lives here and on the
        // resource page; group-derived access is read-only here (revoke it on the group page).
        [HttpGet("manage/{id}")]
        public async Task<IActionResult> Manage(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            return View(await BuildManageViewModelAsync(user));
        }

        private async Task<UserManageViewModel> BuildManageViewModelAsync(ApplicationUser user)
        {
            var allRoles = await _roleManager.Roles.OrderBy(r => r.Name).ToListAsync();
            var userRoles = await _userManager.GetRolesAsync(user);

            var memberships = await _context.UserGroupMemberships
                .Include(m => m.Group)
                .Where(m => m.UserId == user.Id)
                .ToListAsync();
            var memberGroupIds = memberships.Select(m => m.GroupId).ToHashSet();
            var availableGroups = await _context.UserGroups
                .Where(g => !memberGroupIds.Contains(g.Id))
                .OrderBy(g => g.Name)
                .ToListAsync();

            var access = await _access.GetUserAccessAsync(user.Id);
            var accessibleIds = access.Select(a => a.Resource.Id).ToHashSet();
            // VDI clones are provisioned per-pool, not independently grantable — keep them out of the
            // direct-grant picker (access flows through pool assignment + ownership).
            var grantableResources = await _context.RDPResources
                .Where(r => r.Source != ResourceSource.VdiClone && !accessibleIds.Contains(r.Id))
                .OrderBy(r => r.Name)
                .ToListAsync();

            var pools = await _access.GetUserPoolAccessAsync(user.Id);
            var assignedPoolIds = pools.Select(p => p.Pool.Id).ToHashSet();
            var assignablePools = await _context.VdiPools
                .Where(p => !assignedPoolIds.Contains(p.Id))
                .OrderBy(p => p.Name)
                .ToListAsync();

            return new UserManageViewModel
            {
                User = user,
                IsDisabled = AdminAccountSafety.IsDisabled(user),
                IsLockedOut = AdminAccountSafety.IsLockedOut(user),
                IsSelf = user.Id == ActorId,
                HasExternalLogins = (await _userManager.GetLoginsAsync(user)).Count > 0,
                AllRoles = allRoles,
                UserRoles = userRoles.ToList(),
                Memberships = memberships,
                AvailableGroups = availableGroups,
                Access = access,
                GrantableResources = grantableResources,
                Pools = pools,
                AssignablePools = assignablePools,
            };
        }

        // GET: /admin/users/create
        [HttpGet("create")]
        public IActionResult Create()
        {
            return View();
        }

        // POST: /admin/users/create
        [HttpPost("create")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("UserName,Email")] ApplicationUser user, string password)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    user.EmailConfirmed = true;
                    var result = await _userManager.CreateAsync(user, password);
                    if (result.Succeeded)
                    {
                        // Assign User role by default
                        await _userManager.AddToRoleAsync(user, "User");
                        await _audit.LogAsync(KSol.ZeroVDI.Models.AuditCategory.User, "UserCreated",
                            targetType: "User", targetId: user.Id, targetName: user.UserName);
                        return RedirectToAction(nameof(Index));
                    }
                    foreach (var error in result.Errors)
                    {
                        ModelState.AddModelError("", error.Description);
                    }
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("", $"Error creating user: {ex.Message}");
                }
            }
            return View(user);
        }

        // GET: /admin/users/edit/5
        // The account fields are edited inline on the Manage page's Account tab; this GET just lands there.
        [HttpGet("edit/{id}")]
        public IActionResult Edit(string id) => RedirectToAction(nameof(Manage), new { id });

        // POST: /admin/users/edit/5
        [HttpPost("edit/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("Id,UserName,Email")] ApplicationUser user)
        {
            if (id != user.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existingUser = await _userManager.FindByIdAsync(id);
                    if (existingUser == null)
                    {
                        return NotFound();
                    }

                    existingUser.UserName = user.UserName;
                    existingUser.Email = user.Email;
                    existingUser.NormalizedUserName = user.UserName?.ToUpper();
                    existingUser.NormalizedEmail = user.Email?.ToUpper();

                    var result = await _userManager.UpdateAsync(existingUser);
                    if (result.Succeeded)
                    {
                        TempData["Status"] = "Account saved.";
                        return RedirectToAction(nameof(Manage), new { id });
                    }
                    foreach (var error in result.Errors)
                    {
                        ModelState.AddModelError("", error.Description);
                    }
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("", $"Error updating user: {ex.Message}");
                }
            }
            var reload = await _userManager.FindByIdAsync(id) ?? user;
            return View(nameof(Manage), await BuildManageViewModelAsync(reload));
        }

        // GET: /admin/users/delete/5
        [HttpGet("delete/{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            var roles = await _userManager.GetRolesAsync(user);
            ViewBag.UserRoles = roles;
            return View(user);
        }

        // POST: /admin/users/delete/5
        [HttpPost("delete/{id}"), ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user != null)
            {
                if (await _safety.CheckDeleteAsync(user, ActorId) is { } refusal)
                {
                    TempData["Error"] = refusal;
                    return RedirectToAction(nameof(Manage), new { id });
                }

                // Their desktops are open right now; deleting the account must not leave those tunnels
                // running under an identity that no longer exists.
                DisconnectSessionsFor(user, "user deleted");

                var result = await _userManager.DeleteAsync(user);
                if (!result.Succeeded)
                {
                    foreach (var error in result.Errors)
                    {
                        ModelState.AddModelError("", error.Description);
                    }
                    return View(user);
                }
                await _audit.LogAsync(KSol.ZeroVDI.Models.AuditCategory.User, "UserDeleted",
                    targetType: "User", targetId: user.Id, targetName: user.UserName);
            }
            return RedirectToAction(nameof(Index));
        }

        // GET: /admin/users/assignroles/5 — roles are edited on the Manage page's Roles tab.
        [HttpGet("assignroles/{id}")]
        public IActionResult AssignRoles(string id) => RedirectToAction(nameof(Manage), new { id });

        // POST: /admin/users/assignroles/5
        [HttpPost("assignroles/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignRoles(string id, [FromForm] string[] selectedRoles)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            var userRoles = await _userManager.GetRolesAsync(user);
            var requested = selectedRoles ?? Array.Empty<string>();

            // Never let an administrator strip the last Admin role (or their own).
            if (await _safety.CheckRoleChangeAsync(user, ActorId, userRoles, requested) is { } refusal)
            {
                TempData["Error"] = refusal;
                return RedirectToAction(nameof(Manage), new { id = user.Id });
            }

            var rolesToRemove = userRoles.Except(requested).ToList();
            var rolesToAdd = requested.Except(userRoles).ToList();

            try
            {
                if (rolesToRemove.Any())
                {
                    await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
                }

                if (rolesToAdd.Any())
                {
                    await _userManager.AddToRolesAsync(user, rolesToAdd);
                }

                if (rolesToAdd.Any() || rolesToRemove.Any())
                {
                    // Roles live in the auth cookie. Without rotating the stamp, a revoked administrator
                    // keeps their admin rights for the whole validation interval — and a promotion
                    // wouldn't take effect until then either.
                    await _userManager.UpdateSecurityStampAsync(user);
                    await _audit.LogAsync(KSol.ZeroVDI.Models.AuditCategory.Authorization, "RolesChanged",
                        targetType: "User", targetId: user.Id, targetName: user.UserName,
                        detail: new { added = rolesToAdd, removed = rolesToRemove });
                }

                TempData["Status"] = "Roles updated.";
                return RedirectToAction(nameof(Manage), new { id = user.Id });
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Error assigning roles: {ex.Message}");
                return View(nameof(Manage), await BuildManageViewModelAsync(user));
            }
        }

        // --- Account security: the operations an administrator needs when a person calls the help
        // desk. Everything here is guarded by AdminAccountSafety so none of it can lock the last
        // administrator out, and everything is audited. ---

        /// <summary>Aborts every live console session belonging to a user.</summary>
        private int DisconnectSessionsFor(ApplicationUser user, string reason)
        {
            var count = 0;
            foreach (var session in _sessions.All().Where(s => s.UserId == user.Id).ToList())
            {
                if (_sessions.ForceDisconnect(session.SessionId) != null) count++;
            }
            if (count > 0)
            {
                _audit.LogAsync(AuditCategory.Session, "SessionForceDisconnected",
                    targetType: "User", targetId: user.Id, targetName: user.UserName,
                    detail: new { sessions = count, reason }).GetAwaiter().GetResult();
            }
            return count;
        }

        // POST: /admin/users/{id}/password — set a new password directly (help-desk reset).
        [HttpPost("{id}/password")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPassword(string id, string newPassword, bool signOutEverywhere = true)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                TempData["Error"] = "Enter a new password.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            // Reset via a token rather than by writing a hash: this is the only path that runs the
            // configured password validators and clears any stale lockout counter.
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
            if (!result.Succeeded)
            {
                TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
                return RedirectToAction(nameof(Manage), new { id });
            }

            if (signOutEverywhere)
            {
                await _userManager.UpdateSecurityStampAsync(user);
                DisconnectSessionsFor(user, "password reset by administrator");
            }

            await _audit.LogAsync(AuditCategory.User, "PasswordResetByAdmin",
                targetType: "User", targetId: user.Id, targetName: user.UserName,
                detail: new { signOutEverywhere });
            TempData["Status"] = "Password set." + (signOutEverywhere ? " Existing sessions were signed out." : "");
            return RedirectToAction(nameof(Manage), new { id });
        }

        // POST: /admin/users/{id}/password/email — e-mail the user a reset link instead.
        [HttpPost("{id}/password/email")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendPasswordReset(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                TempData["Error"] = "This account has no e-mail address.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            var code = await _userManager.GeneratePasswordResetTokenAsync(user);
            code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
                System.Text.Encoding.UTF8.GetBytes(code));
            // Built from App:PublicBaseUrl, never the request host — see PublicUrl.
            var callbackUrl = _publicUrl.Absolute(
                Url.Page("/Account/ResetPassword", pageHandler: null, values: new { area = "Identity", code }),
                Request);

            try
            {
                await _email.SendEmailAsync(user.Email, "Reset your ZeroVDI password",
                    $"An administrator started a password reset for your account. " +
                    $"<a href='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(callbackUrl)}'>Choose a new password</a>.");
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Could not send the e-mail: " + ex.Message;
                return RedirectToAction(nameof(Manage), new { id });
            }

            await _audit.LogAsync(AuditCategory.User, "PasswordResetLinkSent",
                targetType: "User", targetId: user.Id, targetName: user.UserName);
            TempData["Status"] = $"Reset link sent to {user.Email}.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        // POST: /admin/users/{id}/unlock — clear a brute-force lockout.
        [HttpPost("{id}/unlock")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unlock(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            await _userManager.SetLockoutEndDateAsync(user, null);
            await _userManager.ResetAccessFailedCountAsync(user);
            await _audit.LogAsync(AuditCategory.User, "UserUnlocked",
                targetType: "User", targetId: user.Id, targetName: user.UserName);
            TempData["Status"] = "Account unlocked.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        // POST: /admin/users/{id}/enabled — disable or re-enable sign-in for the account.
        [HttpPost("{id}/enabled")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetEnabled(string id, bool enabled)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (!enabled && await _safety.CheckDisableAsync(user, ActorId) is { } refusal)
            {
                TempData["Error"] = refusal;
                return RedirectToAction(nameof(Manage), new { id });
            }

            // Identity has no "enabled" flag; a far-future lockout is the standard way to express it and
            // is honoured by every sign-in path, including the external/federated one.
            await _userManager.SetLockoutEnabledAsync(user, true);
            await _userManager.SetLockoutEndDateAsync(user, enabled ? null : AdminAccountSafety.DisabledUntil);

            if (!enabled)
            {
                // A cookie already issued outlives the lockout unless the stamp changes.
                await _userManager.UpdateSecurityStampAsync(user);
                DisconnectSessionsFor(user, "account disabled");
            }

            await _audit.LogAsync(AuditCategory.User, enabled ? "UserEnabled" : "UserDisabled",
                targetType: "User", targetId: user.Id, targetName: user.UserName);
            TempData["Status"] = enabled ? "Account enabled." : "Account disabled and signed out.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        // POST: /admin/users/{id}/signout — invalidate every issued cookie and drop live sessions.
        [HttpPost("{id}/signout")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SignOutEverywhere(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            await _userManager.UpdateSecurityStampAsync(user);
            var dropped = DisconnectSessionsFor(user, "signed out by administrator");
            await _audit.LogAsync(AuditCategory.User, "UserSignedOutEverywhere",
                targetType: "User", targetId: user.Id, targetName: user.UserName,
                detail: new { sessions = dropped });
            TempData["Status"] = dropped > 0
                ? $"Signed out everywhere; {dropped} console session(s) disconnected."
                : "Signed out everywhere.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        // --- Group membership (mirrors UserGroupsController; here keyed by the user) ---

        // POST: /admin/users/{id}/groups/add
        [HttpPost("{id}/groups/add")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddToGroup(string id, string groupId)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            var group = await _context.UserGroups.FindAsync(groupId);
            if (group == null)
            {
                TempData["Error"] = "Select a group.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            var exists = await _context.UserGroupMemberships.AnyAsync(m => m.GroupId == groupId && m.UserId == id);
            if (!exists)
            {
                _context.UserGroupMemberships.Add(new UserGroupMembership { GroupId = groupId, UserId = id });
                await _context.SaveChangesAsync();
                await _audit.LogAsync(AuditCategory.Authorization, "GroupMemberAdded",
                    targetType: nameof(UserGroup), targetId: groupId, targetName: group.Name,
                    detail: new { userId = id });
                TempData["Status"] = $"Added to “{group.Name}”.";
            }
            return RedirectToAction(nameof(Manage), new { id });
        }

        // POST: /admin/users/{id}/groups/remove
        [HttpPost("{id}/groups/remove")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveFromGroup(string id, string groupId)
        {
            var m = await _context.UserGroupMemberships
                .Include(x => x.Group)
                .FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == id);
            if (m != null)
            {
                _context.UserGroupMemberships.Remove(m);
                await _context.SaveChangesAsync();
                await _audit.LogAsync(AuditCategory.Authorization, "GroupMemberRemoved",
                    targetType: nameof(UserGroup), targetId: groupId, targetName: m.Group?.Name,
                    detail: new { userId = id });
                TempData["Status"] = $"Removed from “{m.Group?.Name}”.";
            }
            return RedirectToAction(nameof(Manage), new { id });
        }

        // --- Direct resource access (the per-(user, resource) grant). Group-derived access is NOT
        // mutable here — it is owned by the group page. ---

        // POST: /admin/users/{id}/access/grant — grant this user direct access to a resource.
        [HttpPost("{id}/access/grant")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GrantResource(string id, string resourceId)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            var resource = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == resourceId);
            if (resource == null)
            {
                TempData["Error"] = "Select a resource to grant.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            var exists = await _context.RDPResourceUserAuthorizations
                .AnyAsync(a => a.RDPResourceId == resourceId && a.UserId == id);
            if (!exists)
            {
                var auth = new RDPResourceUserAuthorization { RDPResourceId = resourceId, UserId = id };
                // Seed per-user console defaults from the resource-wide defaults, matching the
                // resource page's Grant behaviour so both entry points behave identically.
                if (resource.DefaultConnectionDefaults is { } d)
                {
                    auth.ConnectionDefaults = new ConnectionDefaults
                    {
                        Audio = d.Audio, Clipboard = d.Clipboard, Microphone = d.Microphone,
                        Camera = d.Camera, GfxMode = d.GfxMode, PerformanceFlags = d.PerformanceFlags,
                    };
                }
                _context.RDPResourceUserAuthorizations.Add(auth);
                await _context.SaveChangesAsync();
                await _audit.LogAsync(AuditCategory.Authorization, "AccessGranted",
                    targetType: nameof(RDPResource), targetId: resourceId, targetName: resource.Name,
                    detail: new { userId = id });
                TempData["Status"] = $"Granted access to “{resource.Name}”.";
            }
            return RedirectToAction(nameof(Manage), new { id });
        }

        // POST: /admin/users/{id}/access/revoke — remove this user's DIRECT access to a resource.
        [HttpPost("{id}/access/revoke")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokeResource(string id, string resourceId)
        {
            var auth = await _context.RDPResourceUserAuthorizations
                .Include(a => a.RDPResource)
                .FirstOrDefaultAsync(a => a.RDPResourceId == resourceId && a.UserId == id);
            if (auth != null)
            {
                _context.RDPResourceUserAuthorizations.Remove(auth);
                await _context.SaveChangesAsync();
                await _audit.LogAsync(AuditCategory.Authorization, "AccessRevoked",
                    targetType: nameof(RDPResource), targetId: resourceId, targetName: auth.RDPResource?.Name,
                    detail: new { userId = id });
                TempData["Status"] = "Direct access revoked.";
            }
            return RedirectToAction(nameof(Manage), new { id });
        }

        // POST: /admin/users/{id}/pools/assign — give this user a DIRECT assignment to a VDI pool.
        [HttpPost("{id}/pools/assign")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignPool(string id, string poolId)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            var pool = await _context.VdiPools.FirstOrDefaultAsync(p => p.Id == poolId);
            if (pool == null)
            {
                TempData["Error"] = "Select a pool to assign.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            var exists = await _context.VdiPoolAssignments.AnyAsync(a => a.PoolId == poolId && a.UserId == id);
            if (!exists)
            {
                _context.VdiPoolAssignments.Add(new VdiPoolAssignment { PoolId = poolId, UserId = id });
                await _context.SaveChangesAsync();
                await _audit.LogAsync(AuditCategory.Authorization, "VdiPoolAssignmentGranted",
                    targetType: nameof(VdiPool), targetId: poolId, targetName: pool.Name,
                    detail: new { userId = id });
                TempData["Status"] = $"Assigned to pool “{pool.Name}”.";
            }
            return RedirectToAction(nameof(Manage), new { id });
        }

        // POST: /admin/users/{id}/pools/unassign — remove this user's DIRECT pool assignment.
        [HttpPost("{id}/pools/unassign")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UnassignPool(string id, string poolId)
        {
            var a = await _context.VdiPoolAssignments
                .Include(x => x.Pool)
                .FirstOrDefaultAsync(x => x.PoolId == poolId && x.UserId == id);
            if (a != null)
            {
                _context.VdiPoolAssignments.Remove(a);
                await _context.SaveChangesAsync();
                await _audit.LogAsync(AuditCategory.Authorization, "VdiPoolAssignmentRevoked",
                    targetType: nameof(VdiPool), targetId: poolId, targetName: a.Pool?.Name,
                    detail: new { userId = id });
                TempData["Status"] = "Direct pool assignment removed.";
            }
            return RedirectToAction(nameof(Manage), new { id });
        }
    }

    /// <summary>Backing model for the tabbed user editor (account/roles/groups/resource access).</summary>
    public class UserManageViewModel
    {
        public ApplicationUser User { get; set; } = null!;
        /// <summary>Sign-in deliberately switched off by an administrator (far-future lockout).</summary>
        public bool IsDisabled { get; set; }
        /// <summary>Temporarily locked out by failed sign-in attempts.</summary>
        public bool IsLockedOut { get; set; }
        /// <summary>The administrator viewing the page is this user — the self-protection guards apply.</summary>
        public bool IsSelf { get; set; }
        /// <summary>The account signs in through an identity provider, so a local password may not exist.</summary>
        public bool HasExternalLogins { get; set; }
        public List<IdentityRole> AllRoles { get; set; } = new();
        public List<string> UserRoles { get; set; } = new();
        public List<UserGroupMembership> Memberships { get; set; } = new();
        public List<UserGroup> AvailableGroups { get; set; } = new();
        public IReadOnlyList<ResourceAccessEntry> Access { get; set; } = new List<ResourceAccessEntry>();
        public List<RDPResource> GrantableResources { get; set; } = new();
        public IReadOnlyList<PoolAccessEntry> Pools { get; set; } = new List<PoolAccessEntry>();
        public List<VdiPool> AssignablePools { get; set; } = new();
    }

    public class UsersIndexViewModel
    {
        public List<ApplicationUser> Users { get; set; } = new();
        public string? Query { get; set; }
    }
}
