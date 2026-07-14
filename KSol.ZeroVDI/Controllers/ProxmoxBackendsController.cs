using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// Admin CRUD for Proxmox VE backends (there may be several), plus an on-demand discover/refresh
/// action per backend.
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin/backends")]
public class ProxmoxBackendsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ProxmoxSyncService _sync;

    public ProxmoxBackendsController(ApplicationDbContext context, ProxmoxSyncService sync)
    {
        _context = context;
        _sync = sync;
    }

    // GET: /admin/backends
    [HttpGet("")]
    public async Task<IActionResult> Index(string? q)
    {
        var query = _context.ProxmoxBackends.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(b =>
                EF.Functions.Like(b.Name, $"%{term}%")
                || (b.Host != null && EF.Functions.Like(b.Host, $"%{term}%")));
        }
        ViewData["Query"] = q;
        return View(await query.OrderBy(b => b.Name).ToListAsync());
    }

    private async Task PopulateConnectorsAsync()
        => ViewBag.Connectors = await _context.Connectors.OrderBy(c => c.Name).ToListAsync();

    // GET: /admin/backends/create
    [HttpGet("create")]
    public async Task<IActionResult> Create()
    {
        await PopulateConnectorsAsync();
        return View(new ProxmoxBackend());
    }

    // POST: /admin/backends/create
    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        [Bind("Name,Host,ApiTokenId,ApiTokenSecret,VerifyTls,ConnectorId,DefaultRdpPort,IdleReapEnabled,IdleTimeoutHours,PauseAction,StartTimeoutSeconds")] ProxmoxBackend backend)
    {
        if (!ModelState.IsValid) { await PopulateConnectorsAsync(); return View(backend); }
        _context.Add(backend);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // GET: /admin/backends/edit/5
    [HttpGet("edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var backend = await _context.ProxmoxBackends.FindAsync(id);
        if (backend == null) return NotFound();
        await PopulateConnectorsAsync();
        return View(backend);
    }

    // POST: /admin/backends/edit/5
    [HttpPost("edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id,
        [Bind("Id,Name,Host,ApiTokenId,ApiTokenSecret,VerifyTls,ConnectorId,DefaultRdpPort,IdleReapEnabled,IdleTimeoutHours,PauseAction,StartTimeoutSeconds")] ProxmoxBackend input)
    {
        if (id != input.Id) return NotFound();

        var backend = await _context.ProxmoxBackends.FindAsync(id);
        if (backend == null) return NotFound();
        if (!ModelState.IsValid) { await PopulateConnectorsAsync(); return View(input); }

        backend.Name = input.Name;
        backend.Host = input.Host;
        backend.ApiTokenId = input.ApiTokenId;
        // Keep the existing secret if the field was left blank (it is never rendered back).
        if (!string.IsNullOrWhiteSpace(input.ApiTokenSecret))
        {
            backend.ApiTokenSecret = input.ApiTokenSecret;
        }
        backend.VerifyTls = input.VerifyTls;
        backend.ConnectorId = string.IsNullOrEmpty(input.ConnectorId) ? null : input.ConnectorId;
        backend.DefaultRdpPort = input.DefaultRdpPort;
        backend.IdleReapEnabled = input.IdleReapEnabled;
        backend.IdleTimeoutHours = input.IdleTimeoutHours;
        backend.PauseAction = input.PauseAction;
        backend.StartTimeoutSeconds = input.StartTimeoutSeconds;

        await _context.SaveChangesAsync();
        TempData["Status"] = $"Backend \"{backend.Name}\" saved.";
        return RedirectToAction(nameof(Index));
    }

    // GET: /admin/backends/delete/5
    [HttpGet("delete/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var backend = await _context.ProxmoxBackends.FirstOrDefaultAsync(b => b.Id == id);
        if (backend == null) return NotFound();
        return View(backend);
    }

    // POST: /admin/backends/delete/5
    [HttpPost("delete/{id:int}"), ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var backend = await _context.ProxmoxBackends.FindAsync(id);
        if (backend != null)
        {
            _context.ProxmoxBackends.Remove(backend);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    // POST: /admin/backends/discover/5 — discover/refresh resources from this backend now.
    [HttpPost("discover/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Discover(int id)
    {
        var backend = await _context.ProxmoxBackends.FindAsync(id);
        if (backend == null) return NotFound();

        var count = await _sync.SyncBackendAsync(backend);
        TempData["Status"] = count < 0
            ? $"Backend \"{backend.Name}\" is not fully configured."
            : $"Discovered/refreshed {count} VM(s) from \"{backend.Name}\".";
        return RedirectToAction(nameof(Index));
    }
}
