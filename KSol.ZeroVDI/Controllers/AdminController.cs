using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Controllers;

[Authorize(Roles = "Admin")]
[Route("admin")]
public class AdminController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;

    public AdminController(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
        ProxmoxClient proxmox, ProxmoxBackendProvider backends)
    {
        _context = context;
        _userManager = userManager;
        _proxmox = proxmox;
        _backends = backends;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        // Effective access edges = distinct (user, resource) pairs reachable directly OR via a group.
        // A flat COUNT of direct authorization rows would undercount resources granted only via groups.
        var directEdges = await _context.RDPResourceUserAuthorizations
            .Where(a => a.UserId != null && a.RDPResourceId != null)
            .Select(a => new { a.UserId, a.RDPResourceId })
            .ToListAsync();
        var groupEdges = await _context.UserGroupMemberships
            .Join(_context.RDPResourceGroupAuthorizations, m => m.GroupId, g => g.GroupId,
                (m, g) => new { UserId = (string?)m.UserId, RDPResourceId = (string?)g.RDPResourceId })
            .ToListAsync();
        var accessEdgeCount = directEdges.Concat(groupEdges)
            .Select(e => (e.UserId, e.RDPResourceId))
            .Distinct()
            .Count();

        var vm = new AdminDashboardViewModel
        {
            ResourceCount = await _context.RDPResources.CountAsync(),
            RunningCount = await _context.RDPResources.CountAsync(r => r.PowerState == ResourcePowerState.Running),
            BackendCount = await _context.ProxmoxBackends.CountAsync(),
            UserCount = await _userManager.Users.CountAsync(),
            AuthorizationCount = accessEdgeCount,
            RecordingCount = await _context.Recordings.CountAsync(),
            RecentResources = await _context.RDPResources
                .OrderByDescending(r => r.LastActivityUtc)
                .Take(5)
                .ToListAsync(),
        };
        return View(vm);
    }

    [HttpGet("api/backends/status")]
    public async Task<IActionResult> BackendStatus()
    {
        var allBackends = await _backends.GetAllAsync();

        // Probe every backend concurrently — a serial loop made the dashboard tile as slow as the sum
        // of all backends, and one unreachable backend stalled the rest. WhenAll bounds it to the
        // slowest single probe; each task swallows its own error into the per-backend view model.
        var result = await Task.WhenAll(allBackends.Select(async b =>
        {
            var info = new BackendInfoViewModel { Id = b.Id, Name = b.Name, HostUrl = b.Host };
            if (!b.IsConfigured)
            {
                info.Error = "Not configured";
                return info;
            }
            try
            {
                var vms = await _proxmox.ListVmsAsync(b);
                info.IsOnline = true;
                info.TotalVms = vms.Count;
                info.RunningVms = vms.Count(v => string.Equals(v.Status, "running", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }));

        return Json(result);
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

public class BackendInfoViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? HostUrl { get; set; }
    public bool IsOnline { get; set; }
    public string? Error { get; set; }
    public int TotalVms { get; set; }
    public int RunningVms { get; set; }
}
