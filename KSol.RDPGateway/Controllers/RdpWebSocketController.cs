using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// WebSocket endpoint backing the in-browser RDP console. The browser RDP client (wwwroot/lib/rdpweb)
/// connects here; the gateway authorizes the user for the resource, resolves it to a live host/port
/// (starting a Proxmox VM on demand, via <see cref="VdiResourceResolver"/>), and bridges the
/// connection with <see cref="RdpRelaySession"/>.
/// </summary>
[Authorize]
public class RdpWebSocketController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly VdiResourceResolver _resolver;
    private readonly ProxmoxBackendProvider _backends;
    private readonly CredentialProtector _credentials;
    private readonly RecordingPolicy _recordingPolicy;
    private readonly RedirectionTokenCache _redirections;
    private readonly IConfiguration _config;
    private readonly IAuditLogger _audit;
    private readonly ILogger<RdpWebSocketController> _logger;

    public RdpWebSocketController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        VdiResourceResolver resolver,
        ProxmoxBackendProvider backends,
        CredentialProtector credentials,
        RecordingPolicy recordingPolicy,
        RedirectionTokenCache redirections,
        IConfiguration config,
        IAuditLogger audit,
        ILogger<RdpWebSocketController> logger)
    {
        _context = context;
        _userManager = userManager;
        _resolver = resolver;
        _backends = backends;
        _credentials = credentials;
        _recordingPolicy = recordingPolicy;
        _redirections = redirections;
        _config = config;
        _audit = audit;
        _logger = logger;
    }

    // GET /ws/rdp/{id}
    [HttpGet("ws/rdp/{id}")]
    [EnableRateLimiting("ws")]
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
        // Server Redirection: consume any pending redirection (routing token + one-time session creds, plus
        // recording-continuation state) left by a prior leg of a redirect/handover chain for this
        // (user, resource); pass a callback to stash a new one when THIS leg gets redirected.
        var pending = _redirections.Consume(userId, id);
        RdpRelaySession.VmCredentials? redirectCreds = null;
        if (pending?.Username != null && pending.Password != null)
            redirectCreds = new RdpRelaySession.VmCredentials(pending.Username, pending.Password, pending.Domain);

        // Recording: GNOME "Remote Login" redirects/hands-over up to 3 times (initial → greeter → session).
        // We record ALL legs into ONE recording: the FIRST leg creates the Recording row + base dir; later
        // legs (carried in the redirect Pending) reuse that id/base dir. Each leg's raw streams go into
        // {baseDir}/leg{N}; the mux job concatenates them in order, dropping empty legs (e.g. the initial
        // no-GFX leg). Only the leg that ends WITHOUT arming a continuation finalizes the recording.
        Recording? recording = null;
        SessionRecorder? recorder = null;
        string? baseDir = null;
        string? recId = pending?.RecordingId;
        int leg = pending?.Leg ?? 0;
        try
        {
            bool record;
            if (recId != null && pending?.BaseDir != null)
            {
                // Continuation leg: reuse the prior recording; the row already exists.
                record = true;
                baseDir = pending.BaseDir;
                recording = await _context.Recordings.FindAsync(recId);
            }
            else
            {
                var roles = await _userManager.GetRolesAsync(authorization.User ?? (await _userManager.FindByIdAsync(userId))!);
                record = (await _recordingPolicy.EvaluateAsync(userId, id, roles)).Record;
            }
            if (record)
            {
                var dir = _config["Recording:Directory"]
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "Data", "recordings");
                if (recId == null || baseDir == null)
                {
                    recId = Guid.NewGuid().ToString();
                    baseDir = Path.Combine(dir, recId);
                    recording = new Recording
                    {
                        Id = recId, UserId = userId, RDPResourceId = id,
                        StartedUtc = DateTime.UtcNow, Status = RecordingStatus.Recording,
                        DesktopFilePath = Path.Combine(baseDir, "desktop.mp4"), CameraFilePath = null,
                    };
                    _context.Recordings.Add(recording);
                    await _context.SaveChangesAsync();
                }
                // Each leg records into its own subdir so concurrent/sequential legs never overwrite.
                var legDir = Path.Combine(baseDir, "leg" + leg);
                Directory.CreateDirectory(legDir);
                recorder = new SessionRecorder(legDir, _logger);
                _logger.LogInformation("RDP console: recording session {RecId} leg {Leg} for {Resource}", recId, leg, id);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RDP console: recording setup failed; continuing unrecorded"); }

        // Whether THIS leg armed a continuation (redirect/handover). If so, the recording is NOT finalized
        // here — the next leg continues it. The store callback threads the recording id/base dir + next leg.
        bool continuationArmed = false;
        var session = new RdpRelaySession(socket, host, port, kerberos, _logger, presupplied, recorder,
            pending?.Token, redirectCreds,
            redir =>
            {
                continuationArmed = true;
                _redirections.Store(userId, id,
                    new RedirectionTokenCache.Pending(redir.LoadBalanceInfo!, redir.Username, redir.Domain, redir.Password,
                        recId, baseDir, leg + 1));
            });

        await _resolver.OnConnectedAsync(userId, id);
        var sessionStartUtc = DateTime.UtcNow;
        await _audit.LogAsync(AuditCategory.Session, "SessionConnected",
            targetType: nameof(RDPResource), targetId: id, targetName: resource.Name,
            detail: new { host, port, recorded = recorder != null });
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
            await _audit.LogAsync(AuditCategory.Session, "SessionDisconnected",
                targetType: nameof(RDPResource), targetId: id, targetName: resource.Name,
                detail: new { durationSeconds = (int)(DateTime.UtcNow - sessionStartUtc).TotalSeconds });

            // Close this leg's raw streams (Dispose writes the leg's manifest.json). Only FINALIZE the
            // recording (hand off to the mux job) when no continuation was armed — i.e. this is the last
            // leg. The mux job reads each leg's manifest, concatenates the per-track raws, builds the MP4s,
            // and sets Completed/UnsupportedCodec/Failed.
            if (recording != null && baseDir != null)
            {
                try
                {
                    recorder?.Dispose();
                    if (!continuationArmed)
                    {
                        recording.EndedUtc = DateTime.UtcNow;
                        recording.Status = RecordingStatus.Processing; // mux job inspects legs and decides Failed vs Completed
                        // Record how many legs exist so the mux job can find leg0..legN.
                        recording.VideoCodec = recorder?.VideoCodec ?? recording.VideoCodec ?? "none";
                        _context.Recordings.Update(recording);
                        await _context.SaveChangesAsync();
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "RDP console: finalizing recording {RecId} failed", recording.Id); }
            }
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
