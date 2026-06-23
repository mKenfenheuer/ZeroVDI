using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// Admin CRUD for the session-recording rules engine. Rules are evaluated in ascending Order by
/// <see cref="RDP.RecordingPolicy"/> at session start; the first scope-match decides record/skip.
/// </summary>
[Authorize(Roles = "Admin")]
public class RecordingRulesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly RoleManager<IdentityRole> _roleManager;

    public RecordingRulesController(ApplicationDbContext context, RoleManager<IdentityRole> roleManager)
    {
        _context = context;
        _roleManager = roleManager;
    }

    public async Task<IActionResult> Index()
        => View(await _context.RecordingRules.OrderBy(r => r.Order).ToListAsync());

    public IActionResult Create()
    {
        PopulateSelectLists();
        return View(new RecordingRule());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        [Bind("Order,Scope,Action,Enabled,UserId,RDPResourceId,RoleName,Description")] RecordingRule rule)
    {
        if (ModelState.IsValid)
        {
            _context.RecordingRules.Add(rule);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        PopulateSelectLists(rule);
        return View(rule);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var rule = await _context.RecordingRules.FindAsync(id);
        if (rule == null) return NotFound();
        PopulateSelectLists(rule);
        return View(rule);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id,
        [Bind("Id,Order,Scope,Action,Enabled,UserId,RDPResourceId,RoleName,Description")] RecordingRule rule)
    {
        if (id != rule.Id) return NotFound();
        if (ModelState.IsValid)
        {
            _context.Update(rule);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        PopulateSelectLists(rule);
        return View(rule);
    }

    public async Task<IActionResult> Delete(int id)
    {
        var rule = await _context.RecordingRules.FindAsync(id);
        if (rule == null) return NotFound();
        return View(rule);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var rule = await _context.RecordingRules.FindAsync(id);
        if (rule != null) _context.RecordingRules.Remove(rule);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private void PopulateSelectLists(RecordingRule? rule = null)
    {
        ViewData["UserId"] = new SelectList(_context.Users, "Id", "UserName", rule?.UserId);
        ViewData["RDPResourceId"] = new SelectList(_context.RDPResources, "Id", "Name", rule?.RDPResourceId);
        ViewData["RoleName"] = new SelectList(_roleManager.Roles, "Name", "Name", rule?.RoleName);
    }
}
