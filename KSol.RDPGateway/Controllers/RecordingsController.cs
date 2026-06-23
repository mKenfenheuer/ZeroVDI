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

    // GET /Recordings/File/{id}?track=desktop|camera|audio|mic — streams one per-track file with range
    // support so the web player's <video>/<audio> elements can seek independently. Defaults to desktop.
    public async Task<IActionResult> GetFile(string id, string track = "desktop")
    {
        var rec = await LoadAuthorizedAsync(id);
        if (rec == null) return NotFound();

        var (path, contentType) = track switch
        {
            "camera" => (rec.CameraFilePath, "video/mp4"),
            "audio" => (rec.AudioFilePath, "audio/mp4"),
            "mic" => (rec.MicFilePath, "audio/mp4"),
            _ => (rec.DesktopFilePath, "video/mp4"),
        };
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return NotFound();

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, contentType, enableRangeProcessing: true);
    }

    // POST /Recordings/Delete/{id} — removes the DB row and the recording's files/base directory.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id)
    {
        var rec = await LoadAuthorizedAsync(id);
        if (rec == null) return NotFound();

        // Delete the whole recording directory (combined MP4 plus any leftover raw streams / sidecars).
        // The base dir is the parent of the session file; fall back to deleting the file itself.
        try
        {
            var path = rec.DesktopFilePath;
            if (!string.IsNullOrEmpty(path))
            {
                var baseDir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                    Directory.Delete(baseDir, recursive: true);
                else if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Best-effort file cleanup: still drop the DB row so the orphaned files don't reappear in the UI.
            HttpContext.RequestServices.GetService<ILogger<RecordingsController>>()?
                .LogWarning(ex, "Deleting files for recording {RecId} failed; removing DB row anyway", rec.Id);
        }

        _context.Recordings.Remove(rec);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
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
