using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// Lists recorded sessions and serves their MP4s for in-browser replay. Recording files live outside
/// wwwroot (under the configured recordings dir) and are reachable only through the authorized
/// <see cref="File"/> action, which streams with HTTP range support so the &lt;video&gt; element can seek.
/// </summary>
[Authorize]
public class RecordingsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public RecordingsController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User);
        var isAdmin = User.IsInRole("Admin");

        var query = _context.Recordings
            .Include(r => r.User)
            .Include(r => r.RDPResource)
            .OrderByDescending(r => r.StartedUtc)
            .AsQueryable();
        if (!isAdmin) query = query.Where(r => r.UserId == userId);

        return View(await query.ToListAsync());
    }

    public async Task<IActionResult> Play(string id)
    {
        var rec = await LoadAuthorizedAsync(id);
        if (rec == null) return NotFound();
        return View(rec);
    }

    // GET /Recordings/File/{id}?which=desktop|camera — streams the MP4 with range support.
    public async Task<IActionResult> GetFile(string id, string which)
    {
        var rec = await LoadAuthorizedAsync(id);
        if (rec == null) return NotFound();

        var path = which == "camera" ? rec.CameraFilePath : rec.DesktopFilePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return NotFound();

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "video/mp4", enableRangeProcessing: true);
    }

    /// <summary>Loads a recording the caller may view (Admin: any; otherwise only their own).</summary>
    private async Task<Recording?> LoadAuthorizedAsync(string id)
    {
        var rec = await _context.Recordings
            .Include(r => r.User)
            .Include(r => r.RDPResource)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (rec == null) return null;
        if (User.IsInRole("Admin")) return rec;
        return rec.UserId == _userManager.GetUserId(User) ? rec : null;
    }
}
