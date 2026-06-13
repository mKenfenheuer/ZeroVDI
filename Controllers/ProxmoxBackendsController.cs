using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// Admin CRUD for Proxmox VE backends (there may be several), plus an on-demand discover/refresh
/// action per backend.
/// </summary>
[Authorize(Roles = "Admin")]
public class ProxmoxBackendsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ProxmoxSyncService _sync;

    public ProxmoxBackendsController(ApplicationDbContext context, ProxmoxSyncService sync)
    {
        _context = context;
        _sync = sync;
    }

    // GET: ProxmoxBackends
    public async Task<IActionResult> Index()
    {
        return View(await _context.ProxmoxBackends.ToListAsync());
    }

    // GET: ProxmoxBackends/Create
    public IActionResult Create() => View(new ProxmoxBackend());

    // POST: ProxmoxBackends/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        [Bind("Name,Host,ApiTokenId,ApiTokenSecret,VerifyTls,DefaultRdpPort,IdleTimeoutHours,PauseAction,StartTimeoutSeconds")] ProxmoxBackend backend)
    {
        if (!ModelState.IsValid) return View(backend);
        _context.Add(backend);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // GET: ProxmoxBackends/Edit/5
    public async Task<IActionResult> Edit(int id)
    {
        var backend = await _context.ProxmoxBackends.FindAsync(id);
        if (backend == null) return NotFound();
        return View(backend);
    }

    // POST: ProxmoxBackends/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id,
        [Bind("Id,Name,Host,ApiTokenId,ApiTokenSecret,VerifyTls,DefaultRdpPort,IdleTimeoutHours,PauseAction,StartTimeoutSeconds")] ProxmoxBackend input)
    {
        if (id != input.Id) return NotFound();

        var backend = await _context.ProxmoxBackends.FindAsync(id);
        if (backend == null) return NotFound();
        if (!ModelState.IsValid) return View(input);

        backend.Name = input.Name;
        backend.Host = input.Host;
        backend.ApiTokenId = input.ApiTokenId;
        // Keep the existing secret if the field was left blank (it is never rendered back).
        if (!string.IsNullOrWhiteSpace(input.ApiTokenSecret))
        {
            backend.ApiTokenSecret = input.ApiTokenSecret;
        }
        backend.VerifyTls = input.VerifyTls;
        backend.DefaultRdpPort = input.DefaultRdpPort;
        backend.IdleTimeoutHours = input.IdleTimeoutHours;
        backend.PauseAction = input.PauseAction;
        backend.StartTimeoutSeconds = input.StartTimeoutSeconds;

        await _context.SaveChangesAsync();
        TempData["Status"] = $"Backend \"{backend.Name}\" saved.";
        return RedirectToAction(nameof(Index));
    }

    // GET: ProxmoxBackends/Delete/5
    public async Task<IActionResult> Delete(int id)
    {
        var backend = await _context.ProxmoxBackends.FirstOrDefaultAsync(b => b.Id == id);
        if (backend == null) return NotFound();
        return View(backend);
    }

    // POST: ProxmoxBackends/Delete/5
    [HttpPost, ActionName("Delete")]
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

    // POST: ProxmoxBackends/Discover/5 — discover/refresh resources from this backend now.
    [HttpPost]
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
