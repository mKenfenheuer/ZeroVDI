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
    private readonly RecordingPolicy _recordingPolicy;
    private readonly IConfiguration _config;
    private readonly ILogger<RdpWebSocketController> _logger;

    public RdpWebSocketController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IRDPGWResourceResolver resolver,
        ProxmoxBackendProvider backends,
        CredentialProtector credentials,
        RecordingPolicy recordingPolicy,
        IConfiguration config,
        ILogger<RdpWebSocketController> logger)
    {
        _context = context;
        _userManager = userManager;
        _resolver = resolver;
        _backends = backends;
        _credentials = credentials;
        _recordingPolicy = recordingPolicy;
        _config = config;
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

        // Recording: evaluate the rules engine for this (user, resource, roles). When it says record,
        // create a Recording row and a SessionRecorder (the media sink the relay feeds through RdpWire).
        Recording? recording = null;
        SessionRecorder? recorder = null;
        try
        {
            var roles = await _userManager.GetRolesAsync(authorization.User ?? (await _userManager.FindByIdAsync(userId))!);
            if ((await _recordingPolicy.EvaluateAsync(userId, id, roles)).Record)
            {
                var dir = _config["Recording:Directory"]
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "Data", "recordings");
                var recId = Guid.NewGuid().ToString();
                var baseDir = Path.Combine(dir, recId);
                Directory.CreateDirectory(baseDir);
                // One combined MP4 per session with up to four tracks (desktop video, camera video,
                // remote audio, mic audio). DesktopFilePath holds that single file; CameraFilePath is
                // unused now (kept on the model to avoid a migration).
                var sessionPath = Path.Combine(baseDir, "session.mp4");
                recording = new Recording
                {
                    Id = recId,
                    UserId = userId,
                    RDPResourceId = id,
                    StartedUtc = DateTime.UtcNow,
                    Status = RecordingStatus.Recording,
                    DesktopFilePath = sessionPath,
                    CameraFilePath = null,
                };
                _context.Recordings.Add(recording);
                await _context.SaveChangesAsync();
                // Capture RAW elementary streams under baseDir during the session; a background job
                // (RecordingMuxService) muxes them into the MP4 paths above after the session ends.
                recorder = new SessionRecorder(baseDir, _logger);
                _logger.LogInformation("RDP console: recording session {RecId} for {Resource}", recId, id);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RDP console: recording setup failed; continuing unrecorded"); }

        var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var session = new RdpRelaySession(socket, host, port, kerberos, _logger, presupplied, recorder);

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

            // Finalize: close the raw streams (Dispose writes manifest.json), then hand off to the
            // background mux job by marking the recording Processing. The job reads manifest.json from the
            // base dir, builds the MP4(s), deletes the raws, and sets Completed/UnsupportedCodec/Failed.
            if (recording != null)
            {
                try
                {
                    recorder?.Dispose();
                    recording.EndedUtc = DateTime.UtcNow;
                    recording.VideoCodec = recorder?.VideoCodec ?? "none";
                    if (recorder == null) recording.Status = RecordingStatus.Failed;
                    else if (!recorder.HasDesktop && !recorder.HasCamera) recording.Status = RecordingStatus.Failed; // nothing captured
                    else recording.Status = RecordingStatus.Processing;
                    _context.Recordings.Update(recording);
                    await _context.SaveChangesAsync();
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
