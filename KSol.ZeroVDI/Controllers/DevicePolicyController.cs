using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// Admin editor for the tenant-wide <see cref="DevicePolicy"/> (clipboard / audio / microphone / camera
/// redirection). The policy is a single row; editing it constrains every console session regardless of
/// per-user preferences. Admin-only.
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin/device-policy")]
public class DevicePolicyController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly DevicePolicyService _policy;
    private readonly IAuditLogger _audit;

    public DevicePolicyController(ApplicationDbContext context, DevicePolicyService policy, IAuditLogger audit)
    {
        _context = context;
        _policy = policy;
        _audit = audit;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var policy = await _context.DevicePolicies.FirstOrDefaultAsync(p => p.Id == DevicePolicy.SingletonId)
            ?? new DevicePolicy();
        return View(policy);
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(DevicePolicy input)
    {
        var policy = await _context.DevicePolicies.FirstOrDefaultAsync(p => p.Id == DevicePolicy.SingletonId);
        var creating = policy == null;
        if (creating)
        {
            policy = new DevicePolicy { Id = DevicePolicy.SingletonId };
            _context.DevicePolicies.Add(policy);
        }

        policy!.Clipboard = input.Clipboard;
        policy.Audio = input.Audio;
        policy.Microphone = input.Microphone;
        policy.Camera = input.Camera;

        await _context.SaveChangesAsync();
        _policy.Invalidate();

        await _audit.LogAsync(AuditCategory.Admin, "DevicePolicyUpdated",
            detail: new
            {
                policy.Clipboard,
                policy.Audio,
                policy.Microphone,
                policy.Camera,
            });

        TempData["Status"] = "Device policy saved. New console sessions will use the updated policy.";
        return RedirectToAction(nameof(Index));
    }
}
