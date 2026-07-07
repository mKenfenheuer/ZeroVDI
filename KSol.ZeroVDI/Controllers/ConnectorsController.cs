using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// Admin CRUD for connectors — remote proxy agents that tunnel RDP/HTTP over an outbound WebSocket. A
/// connector is created here; a one-time registration token is shown once for the agent to enroll with,
/// after which the connector holds its control channel open (live status shown on the Index).
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin/connectors")]
public class ConnectorsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ConnectorHub _hub;

    public ConnectorsController(ApplicationDbContext context, ConnectorHub hub)
    {
        _context = context;
        _hub = hub;
    }

    // GET: /admin/connectors
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var connectors = await _context.Connectors.OrderBy(c => c.Name).ToListAsync();
        ViewBag.OnlineIds = _hub.OnlineIds;
        return View(connectors);
    }

    // GET: /admin/connectors/create
    [HttpGet("create")]
    public IActionResult Create() => View(new Connector());

    // POST: /admin/connectors/create — mints the one-time registration token, shown once on the Index.
    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Name,Description,Enabled,AllowScope")] Connector connector)
    {
        if (!ModelState.IsValid) return View(connector);
        connector.RegistrationToken = ConnectorTokens.NewToken();
        _context.Add(connector);
        await _context.SaveChangesAsync();
        TempData["NewRegistrationToken"] = connector.RegistrationToken;
        TempData["NewConnectorId"] = connector.Id;
        TempData["Status"] = $"Connector \"{connector.Name}\" created.";
        return RedirectToAction(nameof(Index));
    }

    // GET: /admin/connectors/edit/{id}
    [HttpGet("edit/{id}")]
    public async Task<IActionResult> Edit(string id)
    {
        var connector = await _context.Connectors.FindAsync(id);
        if (connector == null) return NotFound();
        return View(connector);
    }

    // POST: /admin/connectors/edit/{id}
    [HttpPost("edit/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id,
        [Bind("Id,Name,Description,Enabled,AllowScope")] Connector input)
    {
        if (id != input.Id) return NotFound();
        var connector = await _context.Connectors.FindAsync(id);
        if (connector == null) return NotFound();
        if (!ModelState.IsValid) return View(input);

        connector.Name = input.Name;
        connector.Description = input.Description;
        connector.Enabled = input.Enabled;
        connector.AllowScope = input.AllowScope;
        await _context.SaveChangesAsync();
        TempData["Status"] = $"Connector \"{connector.Name}\" saved.";
        return RedirectToAction(nameof(Index));
    }

    // POST: /admin/connectors/regenerate/{id} — invalidate the current enrollment and issue a fresh
    // one-time registration token (the agent must re-register; its old auth token stops working).
    [HttpPost("regenerate/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Regenerate(string id)
    {
        var connector = await _context.Connectors.FindAsync(id);
        if (connector == null) return NotFound();

        connector.RegistrationToken = ConnectorTokens.NewToken();
        connector.RegistrationTokenUsedUtc = null;
        connector.AuthTokenHash = null;
        await _context.SaveChangesAsync();
        TempData["NewRegistrationToken"] = connector.RegistrationToken;
        TempData["NewConnectorId"] = connector.Id;
        TempData["Status"] = $"New registration token issued for \"{connector.Name}\". The agent must re-register.";
        return RedirectToAction(nameof(Index));
    }

    // GET: /admin/connectors/delete/{id}
    [HttpGet("delete/{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var connector = await _context.Connectors.FirstOrDefaultAsync(c => c.Id == id);
        if (connector == null) return NotFound();
        return View(connector);
    }

    // POST: /admin/connectors/delete/{id}
    [HttpPost("delete/{id}"), ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(string id)
    {
        var connector = await _context.Connectors.FindAsync(id);
        if (connector != null)
        {
            _context.Connectors.Remove(connector);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }
}
