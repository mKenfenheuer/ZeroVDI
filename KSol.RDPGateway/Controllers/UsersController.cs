using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("admin/users")]
    public class UsersController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly KSol.RDPGateway.RDP.IAuditLogger _audit;

        public UsersController(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager,
            KSol.RDPGateway.RDP.IAuditLogger audit)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _audit = audit;
        }

        // GET: /admin/users
        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var users = await _userManager.Users.ToListAsync();
            return View(users);
        }

        // GET: /admin/users/details/5
        [HttpGet("details/{id}")]
        public async Task<IActionResult> Details(string id)
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
                        await _audit.LogAsync(KSol.RDPGateway.Models.AuditCategory.User, "UserCreated",
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
        [HttpGet("edit/{id}")]
        public async Task<IActionResult> Edit(string id)
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
            return View(user);
        }

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
                        return RedirectToAction(nameof(Index));
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
            return View(user);
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
                var result = await _userManager.DeleteAsync(user);
                if (!result.Succeeded)
                {
                    foreach (var error in result.Errors)
                    {
                        ModelState.AddModelError("", error.Description);
                    }
                    return View(user);
                }
                await _audit.LogAsync(KSol.RDPGateway.Models.AuditCategory.User, "UserDeleted",
                    targetType: "User", targetId: user.Id, targetName: user.UserName);
            }
            return RedirectToAction(nameof(Index));
        }

        // GET: /admin/users/assignroles/5
        [HttpGet("assignroles/{id}")]
        public async Task<IActionResult> AssignRoles(string id)
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

            var roles = await _roleManager.Roles.ToListAsync();
            var userRoles = await _userManager.GetRolesAsync(user);

            ViewBag.AllRoles = roles;
            ViewBag.UserRoles = userRoles;
            return View(user);
        }

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
            var rolesToRemove = userRoles.Except(selectedRoles ?? new string[] { }).ToList();
            var rolesToAdd = (selectedRoles ?? new string[] { }).Except(userRoles).ToList();

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
                    await _audit.LogAsync(KSol.RDPGateway.Models.AuditCategory.Authorization, "RolesChanged",
                        targetType: "User", targetId: user.Id, targetName: user.UserName,
                        detail: new { added = rolesToAdd, removed = rolesToRemove });
                }

                return RedirectToAction(nameof(Details), new { id = user.Id });
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Error assigning roles: {ex.Message}");
                var roles = await _roleManager.Roles.ToListAsync();
                ViewBag.AllRoles = roles;
                ViewBag.UserRoles = await _userManager.GetRolesAsync(user);
                return View(user);
            }
        }
    }
}
