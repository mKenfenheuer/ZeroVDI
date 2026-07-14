using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// Lists recorded sessions and serves their MP4s for in-browser replay. Recording files live outside
/// wwwroot (under the configured recordings dir) and are reachable only through the authorized
/// <see cref="File"/> action, which streams with HTTP range support so the &lt;video&gt; element can seek.
///
/// Session recordings are sensitive and are NEVER accessible to ordinary users: the whole controller
/// is restricted to the Admin and Auditor roles. Anyone with access may view every recording (there is
/// no per-user scoping — an Auditor reviews all sessions by design).
/// </summary>
[Authorize(Roles = "Admin,Auditor")]
[Route("admin/recordings")]
public class RecordingsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly KSol.ZeroVDI.RDP.RecordingCryptor _cryptor;
    private readonly KSol.ZeroVDI.RDP.IAuditLogger _audit;

    public RecordingsController(ApplicationDbContext context,
        KSol.ZeroVDI.RDP.RecordingCryptor cryptor, KSol.ZeroVDI.RDP.IAuditLogger audit)
    {
        _context = context;
        _cryptor = cryptor;
        _audit = audit;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, int page = 1)
    {
        const int pageSize = 25;
        if (page < 1) page = 1;

        // Admin/Auditor see every recording — recordings are an audit surface, not a per-user feature.
        var query = _context.Recordings
            .Include(r => r.User)
            .Include(r => r.RDPResource)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(r =>
                (r.User != null && r.User.UserName != null && EF.Functions.Like(r.User.UserName, $"%{term}%"))
                || (r.RDPResource != null && r.RDPResource.Name != null && EF.Functions.Like(r.RDPResource.Name, $"%{term}%")));
        }

        var total = await query.CountAsync();
        var recordings = await query
            .OrderByDescending(r => r.StartedUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return View(new RecordingsIndexViewModel
        {
            Recordings = recordings,
            Query = q,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        });
    }

    [HttpGet("play/{id}")]
    public async Task<IActionResult> Play(string id)
    {
        var rec = await LoadAuthorizedAsync(id);
        if (rec == null) return NotFound();
        return View(rec);
    }

    // GET /admin/recordings/file/{id}?track=desktop|camera|audio|mic — streams one per-track file with
    // range support so the web player's <video>/<audio> elements can seek independently. Defaults to desktop.
    [HttpGet("file/{id}")]
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

        // Encrypted recordings are decrypted on the fly with a SEEKABLE stream, so HTTP range requests
        // (the player seeking) still map onto a partial decrypt. Plaintext recordings stream directly.
        Stream stream = rec.Encrypted
            ? _cryptor.OpenDecryptingStream(path)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, contentType, enableRangeProcessing: true);
    }

    // POST /admin/recordings/verify/{id} — re-hash the on-disk tracks and compare with the stored
    // tamper-evidence hashes. Reports per-track intact/tampered/missing and audits the outcome.
    [HttpPost("verify/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(string id)
    {
        var rec = await LoadAuthorizedAsync(id);
        if (rec == null) return NotFound();

        var result = KSol.ZeroVDI.RDP.RecordingIntegrity.Verify(rec);
        await _audit.LogAsync(AuditCategory.Recording, "RecordingVerified",
            success: result.AllIntact, targetType: nameof(Recording), targetId: rec.Id,
            detail: new { result.AllIntact, tracks = result.Tracks.Select(t => new { t.Track, state = t.State.ToString() }) });

        TempData[result.AllIntact ? "Status" : "Error"] = result.AllIntact
            ? "Integrity verified — all recorded tracks match their stored hashes."
            : "Integrity check FAILED — one or more tracks were altered or are missing.";
        return RedirectToAction(nameof(Play), new { id });
    }

    // POST /admin/recordings/delete/{id} — removes the DB row and the recording's files/base directory.
    [HttpPost("delete/{id}")]
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
        await _audit.LogAsync(AuditCategory.Recording, "RecordingDeleted",
            targetType: nameof(Recording), targetId: rec.Id,
            targetName: rec.RDPResource?.Name);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Loads a recording. The controller is Admin/Auditor-only, so any of them may view any recording.</summary>
    private async Task<Recording?> LoadAuthorizedAsync(string id)
    {
        return await _context.Recordings
            .Include(r => r.User)
            .Include(r => r.RDPResource)
            .FirstOrDefaultAsync(r => r.Id == id);
    }
}

public class RecordingsIndexViewModel
{
    public List<Recording> Recordings { get; set; } = new();
    public string? Query { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize));
}
