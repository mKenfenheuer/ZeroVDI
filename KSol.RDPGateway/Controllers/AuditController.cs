using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// Read-only viewer over the immutable <see cref="AuditEvent"/> trail. Like recordings, the audit log is
/// a compliance surface restricted to Admin and Auditor; ordinary users never see it. There is
/// deliberately no create/edit/delete action — the trail is append-only (written by
/// <see cref="RDP.AuditLogger"/>) so it can serve as evidence.
/// </summary>
[Authorize(Roles = "Admin,Auditor")]
[Route("admin/audit")]
public class AuditController : Controller
{
    private const int PageSize = 50;

    private readonly ApplicationDbContext _context;

    public AuditController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(AuditCategory? category, string? q, int page = 1)
    {
        if (page < 1) page = 1;

        var query = _context.AuditEvents.AsQueryable();
        if (category.HasValue)
            query = query.Where(e => e.Category == category.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(e =>
                (e.ActorName != null && EF.Functions.Like(e.ActorName, $"%{term}%"))
                || (e.TargetName != null && EF.Functions.Like(e.TargetName, $"%{term}%"))
                || EF.Functions.Like(e.Action, $"%{term}%"));
        }

        var total = await query.CountAsync();
        var events = await query
            .OrderByDescending(e => e.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        var vm = new AuditIndexViewModel
        {
            Events = events,
            Category = category,
            Query = q,
            Page = page,
            PageSize = PageSize,
            TotalCount = total,
        };
        return View(vm);
    }
}

public class AuditIndexViewModel
{
    public List<AuditEvent> Events { get; set; } = new();
    public AuditCategory? Category { get; set; }
    public string? Query { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}
