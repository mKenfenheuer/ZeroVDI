using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// Admin landing page at <c>/admin/</c>: an at-a-glance dashboard with counts and quick links into
/// the three management sections. Admin-only (the audit surfaces stay role-gated on their own
/// controllers).
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin")]
public class AdminController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public AdminController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var vm = new AdminDashboardViewModel
        {
            ResourceCount = await _context.RDPResources.CountAsync(),
            RunningCount = await _context.RDPResources.CountAsync(r => r.PowerState == ResourcePowerState.Running),
            BackendCount = await _context.ProxmoxBackends.CountAsync(),
            UserCount = await _userManager.Users.CountAsync(),
            AuthorizationCount = await _context.RDPResourceUserAuthorizations.CountAsync(),
            RecordingCount = await _context.Recordings.CountAsync(),
            RecentResources = await _context.RDPResources
                .OrderByDescending(r => r.LastActivityUtc)
                .Take(5)
                .ToListAsync(),
        };
        return View(vm);
    }
}

public class AdminDashboardViewModel
{
    public int ResourceCount { get; set; }
    public int RunningCount { get; set; }
    public int BackendCount { get; set; }
    public int UserCount { get; set; }
    public int AuthorizationCount { get; set; }
    public int RecordingCount { get; set; }
    public List<RDPResource> RecentResources { get; set; } = new();
}
