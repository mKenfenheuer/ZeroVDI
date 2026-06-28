using System.Diagnostics;
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
    private readonly RDP.CredentialProtector _credentials;
    private readonly RDP.RecordingPolicy _recordingPolicy;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext context, UserManager<ApplicationUser> userManager, RDP.CredentialProtector credentials, RDP.RecordingPolicy recordingPolicy)
    {
        _logger = logger;
        _context = context;
        _userManager = userManager;
        _credentials = credentials;
        _recordingPolicy = recordingPolicy;
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

        return View(resources ?? new List<RDPResource?>());
    }

    /// <summary>
    /// In-browser RDP console page for an authorized resource. Renders the HTML5 client that connects
    /// to the WebSocket relay (<c>/ws/rdp/{id}</c>).
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<IActionResult> Console(string id)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return Unauthorized();
        }

        var authorization = await _context.RDPResourceUserAuthorizations
            .Include(r => r.RDPResource)
            .FirstOrDefaultAsync(r => r.UserId == userId && r.RDPResourceId == id);

        if (authorization?.RDPResource == null)
        {
            return NotFound();
        }

        ViewData["ResourceId"] = id;
        ViewData["ResourceName"] = authorization.RDPResource.Name ?? id;

        // Recording disclosure: if the rules engine would record this session AND the matched rule asks
        // to notify, the console shows a "this session is recorded" banner. Mirrors the decision made in
        // RdpWebSocketController.Connect (same user/resource/roles), so the notice matches what's captured.
        var roles = await _userManager.GetRolesAsync(authorization.User ?? (await _userManager.FindByIdAsync(userId))!);
        ViewData["RecordingNotice"] = (await _recordingPolicy.EvaluateAsync(userId, id, roles)).Notify;

        // SSO: when VM credentials are stored for this (user, resource), the console auto-connects
        // without the login overlay. NO stored credential (username, password or domain) is EVER sent
        // to the browser. The gateway injects the real credentials entirely server-side for both the
        // client-facing NLA and the host logon (see RdpWebSocketController.presuppliedCreds and
        // MitmRdpStream._hostCreds; the client's delegated creds are terminated at the gateway and
        // discarded). The browser uses harmless placeholders so its handshake frames are well-formed;
        // those placeholders never reach the host.
        if (authorization.HasStoredCredentials)
        {
            // Confirm the stored credentials are decryptable (keyring intact) before offering
            // auto-connect, surfacing only a boolean - never the plaintext. If not usable, fall through
            // to the manual login overlay.
            var usable = !string.IsNullOrEmpty(_credentials.Unprotect(authorization.ProtectedUsername))
                      && !string.IsNullOrEmpty(_credentials.Unprotect(authorization.ProtectedPassword));
            if (usable)
            {
                ViewData["AutoConnect"] = true;
                ViewData["Defaults"] = authorization.ConnectionDefaults ?? new ConnectionDefaults();
                // StoredUser/StoredPassword/StoredDomain intentionally NOT set.
            }
        }

        return View();
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
