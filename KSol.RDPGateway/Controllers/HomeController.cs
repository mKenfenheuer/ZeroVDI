using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;
using Microsoft.AspNetCore.Authorization;

namespace KSol.RDPGateway.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RDP.CredentialProtector _credentials;
    private readonly RDP.RecordingPolicy _recordingPolicy;
    private readonly RDP.DevicePolicyService _devicePolicy;
    private readonly RDP.ResourceAccessService _access;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext context, UserManager<ApplicationUser> userManager, RDP.CredentialProtector credentials, RDP.RecordingPolicy recordingPolicy, RDP.DevicePolicyService devicePolicy, RDP.ResourceAccessService access)
    {
        _logger = logger;
        _context = context;
        _userManager = userManager;
        _credentials = credentials;
        _recordingPolicy = recordingPolicy;
        _devicePolicy = devicePolicy;
        _access = access;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return View(new List<DashboardResourceViewModel>());
        }

        // Effective access = direct grants ∪ group grants. Direct rows carry the per-user SSO/defaults
        // state; group-only resources have no per-user row (Auth stays a bare placeholder → login overlay).
        var resourceIds = await _access.AccessibleResourceIdsAsync(userId);
        var resources = await _context.RDPResources
            .Where(r => resourceIds.Contains(r.Id))
            .ToListAsync();
        var directAuths = await _context.RDPResourceUserAuthorizations
            .Where(a => a.UserId == userId && a.RDPResourceId != null)
            .ToDictionaryAsync(a => a.RDPResourceId!);

        var items = resources.Select(r => new DashboardResourceViewModel
        {
            Resource = r,
            Auth = directAuths.TryGetValue(r.Id, out var a)
                ? a
                : new RDPResourceUserAuthorization { UserId = userId, RDPResourceId = r.Id, RDPResource = r },
        }).ToList();

        // VDI pool entry points the user is entitled to (direct ∪ group). A pool clones a desktop on
        // first connect; its card uses the pool id as the connect target — the readiness pre-step
        // (VdiResourceResolver) swaps in the provisioned clone. Per-user creds/settings don't apply
        // pre-provision, so pool cards carry a placeholder auth and the view hides the settings gear.
        // Pools whose clone already exists are shown via that concrete resource (already in resourceIds
        // above), so skip the pool entry to avoid a duplicate card.
        var poolsWithOwnClone = await _context.VdiInstances
            .Where(i => i.OwnerUserId == userId
                && i.State != VdiInstanceState.Deprovisioning && i.State != VdiInstanceState.Failed)
            .Select(i => i.PoolId)
            .ToListAsync();

        foreach (var pool in await _access.AccessiblePoolsAsync(userId))
        {
            if (poolsWithOwnClone.Contains(pool.Id)) continue; // already provisioned → shown as its resource
            items.Add(new DashboardResourceViewModel
            {
                Resource = new RDPResource
                {
                    Id = pool.Id,
                    Name = pool.Name,
                    Description = pool.Description,
                    Source = ResourceSource.VdiClone,
                    Port = pool.Port,
                    OsType = pool.OsType,
                },
                Auth = new RDPResourceUserAuthorization { UserId = userId, RDPResourceId = pool.Id },
                IsPool = true,
            });
        }

        return View(items.OrderBy(i => i.Resource.Name).ToList());
    }

    /// <summary>
    /// In-browser RDP console page for an authorized resource. Renders the HTML5 client that connects
    /// to the WebSocket relay (<c>/ws/rdp/{id}</c>).
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<IActionResult> Console(string id)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            return Unauthorized();
        }

        // Access may be direct OR via a group. Resolve the resource through the access service; the
        // per-user authorization row (carrying SSO creds / personal defaults) is loaded separately and
        // may be null for a group-only grant — in which case the console just shows the login overlay.
        var resource = await _access.GetAuthorizedResourceAsync(userId, id);
        if (resource == null)
        {
            return NotFound();
        }
        var authorization = await _context.RDPResourceUserAuthorizations
            .FirstOrDefaultAsync(r => r.UserId == userId && r.RDPResourceId == id);

        ViewData["ResourceId"] = id;
        ViewData["ResourceName"] = resource.Name ?? id;

        // Recording disclosure: if the rules engine would record this session AND the matched rule asks
        // to notify, the console shows a "this session is recorded" banner. Mirrors the decision made in
        // RdpWebSocketController.Connect (same user/resource/roles), so the notice matches what's captured.
        var roles = await _userManager.GetRolesAsync((await _userManager.FindByIdAsync(userId))!);
        ViewData["RecordingNotice"] = (await _recordingPolicy.EvaluateAsync(userId, id, roles)).Notify;

        // Device/channel redirection policy: pass the policy to the view so locked features render as
        // disabled checkboxes, and clamp the defaults the form starts from so a locked-off feature is
        // never pre-ticked. (The relay also clamps at connect; this is the UI half.)
        var devicePolicy = _devicePolicy.Get();
        ViewData["DevicePolicy"] = devicePolicy;

        // SSO: when VM credentials are stored for this (user, resource), the console auto-connects
        // without the login overlay. NO stored credential (username, password or domain) is EVER sent
        // to the browser. The gateway injects the real credentials entirely server-side for both the
        // client-facing NLA and the host logon (see RdpWebSocketController.presuppliedCreds and
        // MitmRdpStream._hostCreds; the client's delegated creds are terminated at the gateway and
        // discarded). The browser uses harmless placeholders so its handshake frames are well-formed;
        // those placeholders never reach the host.
        if (authorization?.HasStoredCredentials == true)
        {
            // Confirm the stored credentials are decryptable (keyring intact) before offering
            // auto-connect, surfacing only a boolean - never the plaintext. If not usable, fall through
            // to the manual login overlay.
            var usable = !string.IsNullOrEmpty(_credentials.Unprotect(authorization.ProtectedUsername))
                      && !string.IsNullOrEmpty(_credentials.Unprotect(authorization.ProtectedPassword));
            if (usable)
            {
                ViewData["AutoConnect"] = true;
                ViewData["Defaults"] = _devicePolicy.Apply(authorization.ConnectionDefaults ?? new ConnectionDefaults());
                // StoredUser/StoredPassword/StoredDomain intentionally NOT set.
            }
        }
        // VDI pool entry point: on the FIRST connect no concrete clone (and thus no per-user SSO row keyed
        // to the clone) exists yet — it's provisioned during the preflight. But if the pool generates a
        // per-user credential, the relay WILL have stored SSO for the clone by the time the socket opens,
        // so the console must auto-connect (suppress the login overlay) rather than prompt for credentials
        // the user doesn't know. Detect the pool case here and pre-arm auto-connect with the pool defaults.
        else if (!ViewData.ContainsKey("AutoConnect")
            && await _context.VdiPools.AsNoTracking()
                .AnyAsync(p => p.Id == id && p.IdentityMode == VdiIdentityMode.CloudInit && p.GenerateCredentials))
        {
            ViewData["AutoConnect"] = true;
            ViewData["Defaults"] = _devicePolicy.Apply(resource.DefaultConnectionDefaults ?? new ConnectionDefaults());
        }

        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

public class DashboardResourceViewModel
{
    public RDPResource Resource { get; set; } = null!;
    public RDPResourceUserAuthorization Auth { get; set; } = null!;

    /// <summary>True when this card is a VDI pool entry point (clones a desktop on first connect),
    /// not a concrete resource. The view shows a "pool" badge and hides per-user settings.</summary>
    public bool IsPool { get; set; }
}
