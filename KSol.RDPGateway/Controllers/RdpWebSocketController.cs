using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;
using RDPGW.AspNetCore;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// WebSocket endpoint backing the in-browser RDP console. The browser RDP client (wwwroot/lib/rdpweb)
/// connects here; the gateway authorizes the user for the resource, resolves it to a live host/port
/// (starting a Proxmox VM on demand, reusing <see cref="IRDPGWResourceResolver"/> — the same path the
/// native .rdp flow uses), and bridges the connection with <see cref="RdpRelaySession"/>.
/// </summary>
[Authorize]
public class RdpWebSocketController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IRDPGWResourceResolver _resolver;
    private readonly ProxmoxBackendProvider _backends;
    private readonly CredentialProtector _credentials;
    private readonly ILogger<RdpWebSocketController> _logger;

    public RdpWebSocketController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IRDPGWResourceResolver resolver,
        ProxmoxBackendProvider backends,
        CredentialProtector credentials,
        ILogger<RdpWebSocketController> logger)
    {
        _context = context;
        _userManager = userManager;
        _resolver = resolver;
        _backends = backends;
        _credentials = credentials;
        _logger = logger;
    }

    // GET /ws/rdp/{id}
    [HttpGet("ws/rdp/{id}")]
    public async Task Connect(string id)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var userId = _userManager.GetUserId(User);
        if (userId == null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Authorize the user for this resource (same check as HomeController.DownloadRdpFile).
        var authorization = await _context.RDPResourceUserAuthorizations
            .Include(a => a.RDPResource)
            .FirstOrDefaultAsync(a => a.UserId == userId && a.RDPResourceId == id);
        if (authorization?.RDPResource == null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var resource = authorization.RDPResource;
        var requestedPort = (ushort)(resource.Port > 0 ? resource.Port : 3389);

        // Resolve the resource to a live host/port — starts/resumes a Proxmox VM and waits for it.
        var resolved = await _resolver.ResolveAsync(userId, id, requestedPort);
        if (resolved == null)
        {
            // Could not start/reach the backing machine. Accept the socket only to report the error.
            var errSock = await HttpContext.WebSockets.AcceptWebSocketAsync();
            var msg = System.Text.Encoding.UTF8.GetBytes(
                "{\"status\":\"error\",\"message\":\"the desktop could not be started or reached\"}");
            await errSock.SendAsync(msg, System.Net.WebSockets.WebSocketMessageType.Text, true, HttpContext.RequestAborted);
            await errSock.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "unreachable", HttpContext.RequestAborted);
            return;
        }

        var (host, port) = resolved.Value;
        var kerberos = await ResolveKerberosAsync(resource);

        // SSO: if VM credentials are stored for this (user, resource), decrypt them and pass them to
        // the relay so the browser never sends a credentials frame. Decryption returning null (empty
        // store, or keyring lost) falls back to the browser-supplied first-frame credentials.
        RdpRelaySession.VmCredentials? presupplied = null;
        if (authorization.HasStoredCredentials)
        {
            var user = _credentials.Unprotect(authorization.ProtectedUsername);
            var password = _credentials.Unprotect(authorization.ProtectedPassword);
            if (!string.IsNullOrEmpty(user) && password != null)
                presupplied = new RdpRelaySession.VmCredentials(user, password,
                    _credentials.Unprotect(authorization.ProtectedDomain));
        }

        var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var session = new RdpRelaySession(socket, host, port, kerberos, _logger, presupplied);

        await _resolver.OnConnectedAsync(userId, id);
        try
        {
            await session.RunAsync(HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RDP console session for {Resource} ended with error", id);
        }
        finally
        {
            await _resolver.OnDisconnectedAsync(userId, id);
        }
    }

    /// <summary>
    /// Builds the optional Kerberos authenticator from the resource's Proxmox backend config (realm +
    /// KDC). Returns null for manual resources or backends without Kerberos configured — the CredSSP
    /// client then uses NTLMv2.
    /// </summary>
    private async Task<KerberosAuth?> ResolveKerberosAsync(RDPResource resource)
    {
        if (resource.Source != ResourceSource.Proxmox || resource.ProxmoxBackendId == null)
            return null;
        var backend = await _backends.GetAsync(resource.ProxmoxBackendId.Value);
        if (backend == null || string.IsNullOrWhiteSpace(backend.KerberosRealm)
            || string.IsNullOrWhiteSpace(backend.KdcHost))
            return null;
        return new KerberosAuth(backend.KerberosRealm!, backend.KdcHost!, _logger);
    }
}
