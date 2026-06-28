using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// The transparent connect workflow. Instead of dropping the user straight into the console (and a
/// blank "starting remote computer" wait), <c>/connect/{id}</c> shows a preflight page that polls the
/// readiness API: it starts/resumes the VM if needed and waits until the guest agent, IP and RDP probe
/// all succeed, with granular progress and meaningful errors. Only then does it launch the console.
///
/// Every action re-checks that the signed-in user is authorized for the resource — users can't probe
/// or start resources they aren't assigned.
/// </summary>
[Authorize]
[Route("connect")]
public class ConnectController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ConnectionReadinessService _readiness;
    private readonly CredentialProtector _credentials;

    public ConnectController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        ConnectionReadinessService readiness,
        CredentialProtector credentials)
    {
        _context = context;
        _userManager = userManager;
        _readiness = readiness;
        _credentials = credentials;
    }

    private async Task<RDPResource?> AuthorizeResourceAsync(string id)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null) return null;
        var auth = await _context.RDPResourceUserAuthorizations
            .Include(a => a.RDPResource)
            .FirstOrDefaultAsync(a => a.UserId == userId && a.RDPResourceId == id);
        return auth?.RDPResource;
    }

    // GET /connect/{id} — the preflight progress page.
    [HttpGet("{id}")]
    public async Task<IActionResult> Index(string id)
    {
        var resource = await AuthorizeResourceAsync(id);
        if (resource == null) return NotFound();

        ViewData["ResourceId"] = id;
        ViewData["ResourceName"] = resource.Name ?? id;
        return View();
    }

    // POST /connect/{id}/begin — kick off (or attach to) the readiness sequence.
    [HttpPost("{id}/begin")]
    public async Task<IActionResult> Begin(string id)
    {
        var resource = await AuthorizeResourceAsync(id);
        if (resource == null) return Json(Error("Not authorized for this resource."));

        var userId = _userManager.GetUserId(User)!;
        var port = (ushort)(resource.Port > 0 ? resource.Port : 3389);
        var progress = _readiness.Begin(userId, id, port);
        return Json(ToDto(progress));
    }

    // GET /connect/{id}/status — poll the current readiness state.
    [HttpGet("{id}/status")]
    public async Task<IActionResult> Status(string id)
    {
        var resource = await AuthorizeResourceAsync(id);
        if (resource == null) return Json(Error("Not authorized for this resource."));

        var userId = _userManager.GetUserId(User)!;
        var progress = _readiness.Status(userId, id);
        if (progress == null)
            return Json(new { phase = "checking", message = "Checking resource…", done = false, failed = false });

        return Json(ToDto(progress));
    }

    // POST /connect/{id}/save — persist credentials and/or connection settings to the current user's
    // own authorization for this resource (the "Store credentials" / "Remember settings" checkboxes).
    [HttpPost("{id}/save")]
    public async Task<IActionResult> Save(string id, [FromBody] SaveConnectionRequest req)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null) return Unauthorized();

        var auth = await _context.RDPResourceUserAuthorizations
            .FirstOrDefaultAsync(a => a.UserId == userId && a.RDPResourceId == id);
        if (auth == null) return NotFound();

        if (req.StoreCredentials)
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
                return BadRequest(new { error = "Username and password are required to store credentials." });

            auth.ProtectedUsername = _credentials.Protect(req.Username);
            auth.ProtectedPassword = _credentials.Protect(req.Password);
            auth.ProtectedDomain = _credentials.Protect(req.Domain);
        }

        if (req.RememberSettings)
        {
            auth.ConnectionDefaults = new ConnectionDefaults
            {
                Audio = req.Audio,
                Clipboard = req.Clipboard,
                Microphone = req.Microphone,
                Camera = req.Camera,
                GfxMode = string.IsNullOrEmpty(req.GfxMode) ? "avc420" : req.GfxMode,
                PerformanceFlags = req.PerformanceFlags,
            };
        }

        await _context.SaveChangesAsync();
        return Ok(new { saved = true });
    }

    private static object ToDto(ReadinessProgress p) => new
    {
        phase = p.Phase.ToString().ToLowerInvariant(),
        message = p.Message,
        done = p.Done,
        failed = p.Failed,
        error = p.Error,
    };

    private static object Error(string message) => new
    {
        phase = "error",
        message,
        done = false,
        failed = true,
        error = message,
    };
}

/// <summary>Body of the in-flow "store credentials / remember settings" POST from the console.</summary>
public class SaveConnectionRequest
{
    public bool StoreCredentials { get; set; }
    public bool RememberSettings { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }
    public bool Audio { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public bool Microphone { get; set; } = true;
    public bool Camera { get; set; } = true;
    public string? GfxMode { get; set; }
    public int PerformanceFlags { get; set; }
}
