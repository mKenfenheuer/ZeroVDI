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
    /// TEMPORARY DEBUG: accept a raw binary body and dump it to /tmp/rdpgw-dump/&lt;name&gt; so a decoded
    /// H.264 keyframe captured in the browser can be decoded offline with ffmpeg. Remove after debugging.
    /// </summary>
    [HttpPost("/debug/dump/{name}")]
    public async Task<IActionResult> DebugDump(string name)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(name, "[^a-zA-Z0-9._-]", "_");
        var dir = "/tmp/rdpgw-dump";
        Directory.CreateDirectory(dir);
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var path = Path.Combine(dir, safe);
        // ?append=1 concatenates (used to capture a whole GFX H.264 stream across many frames);
        // otherwise overwrite (single keyframe dump).
        if (Request.Query.ContainsKey("append"))
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await fs.WriteAsync(bytes);
        }
        else
        {
            await System.IO.File.WriteAllBytesAsync(path, bytes);
        }
        return Ok(new { written = bytes.Length, path });
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

        return File(fileBytes, "application/x-rdp", $"{resource.Name ?? resource.Id}.rdp");
    }

    /// <summary>
    /// In-browser RDP console page for an authorized resource. Renders the HTML5 client that connects
    /// to the WebSocket relay (<c>/ws/rdp/{id}</c>). Authorization mirrors <see cref="DownloadRdpFile"/>.
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
        ViewData["DefaultUser"] = _userManager.GetUserName(User);
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
