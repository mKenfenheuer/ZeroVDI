using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// Admin view of live gateway sessions, with force-disconnect. Sessions live in the in-memory
/// <see cref="SessionTracker"/> registry (single-instance; see roadmap #8 for HA). Viewing is open to
/// Admin and Auditor; force-disconnect is an action and is restricted to Admin.
/// </summary>
[Authorize(Roles = "Admin,Auditor")]
[Route("admin/sessions")]
public class SessionsController : Controller
{
    private readonly SessionTracker _sessions;
    private readonly IAuditLogger _audit;

    public SessionsController(SessionTracker sessions, IAuditLogger audit)
    {
        _sessions = sessions;
        _audit = audit;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View(_sessions.All());
    }

    [HttpPost("{sessionId}/disconnect")]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(string sessionId)
    {
        var session = _sessions.ForceDisconnect(sessionId);
        if (session != null)
        {
            await _audit.LogAsync(AuditCategory.Session, "SessionForceDisconnected",
                targetType: nameof(RDPResource), targetId: session.ResourceId, targetName: session.ResourceName,
                detail: new { session.SessionId, session.UserName, session.ClientIp });
            TempData["Status"] = $"Disconnected session for {session.UserName ?? session.UserId}.";
        }
        else
        {
            TempData["Error"] = "That session has already ended.";
        }
        return RedirectToAction(nameof(Index));
    }
}
