using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;

namespace KSol.RDPGateway.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RdpFileGenerator _rdpGenerator;
    private readonly PaaTokenService _paa;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext context, UserManager<ApplicationUser> userManager, RdpFileGenerator rdpGenerator, PaaTokenService paa)
    {
        _logger = logger;
        _context = context;
        _userManager = userManager;
        _rdpGenerator = rdpGenerator;
        _paa = paa;
    }

    /// <summary>
    /// Issues a short-lived PAA pre-auth token for the signed-in user. Clients that support the
    /// extended HTTP_EXTENDED_AUTH_PAA flow can present this token to open a tunnel without
    /// re-entering credentials.
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize]
    public IActionResult PaaToken()
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
            return Unauthorized();
        return Content(_paa.Issue(userId), "text/plain");
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return View(new List<RDPResource>());
        }

        // Get resources the user has access to
        var resources = await _context.RDPResourceUserAuthorizations
            .Where(r => r.UserId == userId)
            .Select(r => r.RDPResource)
            .ToListAsync();

        return View(resources ?? new List<RDPResource>());
    }

    public async Task<IActionResult> DownloadRdpFile(string id)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return Unauthorized();
        }

        // Check if user has access to this resource
        var authorization = await _context.RDPResourceUserAuthorizations
            .Include(r => r.RDPResource)
            .FirstOrDefaultAsync(r => r.UserId == userId && r.RDPResourceId == id);

        if (authorization?.RDPResource == null)
        {
            return NotFound();
        }

        var resource = authorization.RDPResource;
        var host = Request.Host.Host;

        // Generate RDP file content via the shared generator (also used by the RDWeb feed).
        var rdpContent = _rdpGenerator.Generate(resource, host, _userManager.GetUserName(User));
        var fileBytes = Encoding.UTF8.GetBytes(rdpContent);

        return File(fileBytes, "application/x-rdp", $"{resource.Name ?? resource.ResourceIdentifier}.rdp");
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
