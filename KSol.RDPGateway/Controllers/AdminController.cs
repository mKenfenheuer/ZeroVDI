using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;

namespace KSol.RDPGateway.Controllers;

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

    [HttpGet("api/backends/status")]
    public async Task<IActionResult> BackendStatus()
    {
        var allBackends = await _backends.GetAllAsync();
        var result = new List<BackendInfoViewModel>();

        foreach (var b in allBackends)
        {
            var info = new BackendInfoViewModel { Id = b.Id, Name = b.Name, HostUrl = b.Host };
            if (!b.IsConfigured)
            {
                info.Error = "Not configured";
                result.Add(info);
                continue;
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
            result.Add(info);
        }

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
