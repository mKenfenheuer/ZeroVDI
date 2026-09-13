using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Background service that pauses Proxmox-backed VMs which have had no active gateway session for at
/// least their backend's idle timeout. The pause action (suspend / stop / hibernate) is per-backend.
/// </summary>
public class IdleReaperService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);
    // Hold off the first scan so its Proxmox calls don't compete with application startup.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly int ManualIdleTimeoutHours = 24;

    // When the service started. Until at least an idle timeout has elapsed since this point we do not
    // know how long a VM has really been idle (we may have just come up), so we never reap before then.
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;
    private readonly SessionTracker _sessions;
    private readonly ResourceShutdownService _shutdown;
    /// <summary>Name this worker reports liveness under on the operations page.</summary>
    private const string HeartbeatName = "Idle reaper";
    private readonly ServiceHeartbeats _heartbeats;
    private readonly ILogger<IdleReaperService> _logger;

    public IdleReaperService(
        IServiceScopeFactory scopeFactory,
        ProxmoxClient proxmox,
        ProxmoxBackendProvider backends,
        SessionTracker sessions,
        ResourceShutdownService shutdown,
        ServiceHeartbeats heartbeats,
        ILogger<IdleReaperService> logger)
    {
        _scopeFactory = scopeFactory;
        _proxmox = proxmox;
        _backends = backends;
        _sessions = sessions;
        _shutdown = shutdown;
        _heartbeats = heartbeats;
        _logger = logger;
        _heartbeats.Register(HeartbeatName, ScanInterval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defer the first scan so its Proxmox calls don't compete with startup.
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(stoppingToken);
                _heartbeats.Success(HeartbeatName, ScanInterval);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Idle reaper scan failed");
                _heartbeats.Failure(HeartbeatName, ScanInterval, ex.Message);
            }

            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await ScanProxmoxAsync(db, ct);
        await ScanManualAsync(db, ct);
    }

    private async Task ScanProxmoxAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var backends = await _backends.GetAllAsync(ct);
        if (backends.Count == 0) return;
        var byId = backends.ToDictionary(b => b.Id);

        // Discovered Proxmox VMs AND VDI pool clones: both are Proxmox-backed and both burn host resources
        // while idle. (Clones were left out until 0.6.35 and ran forever once started.)
        var candidates = await db.RDPResources
            .Where(r => (r.Source == ResourceSource.Proxmox || r.Source == ResourceSource.VdiClone)
                        && r.ProxmoxBackendId != null && r.ProxmoxNode != null && r.ProxmoxVmId != null
                        && r.PowerState == ResourcePowerState.Running)
            .ToListAsync(ct);

        foreach (var res in candidates)
        {
            if (!byId.TryGetValue(res.ProxmoxBackendId!.Value, out var backend) || !backend.IsConfigured)
                continue;

            if (!backend.IdleReapEnabled) continue;
            if (_sessions.HasActiveSessions(res.Id)) continue;
            var idle = TimeSpan.FromHours(Math.Max(1, backend.IdleTimeoutHours));
            if (!IsReapable(res.LastActivityUtc, idle)) continue;

            if (res.Source == ResourceSource.Proxmox)
            {
                // Discovery owns the exclude marker; a clone's notes carry the gateway binding instead.
                var notes = await _proxmox.GetNotesAsync(backend, res.ProxmoxNode!, res.ProxmoxVmId!.Value, ct);
                if (ProxmoxNotes.ReadExcluded(notes))
                {
                    var auths = await db.RDPResourceUserAuthorizations
                        .Where(a => a.RDPResourceId == res.Id).ToListAsync(ct);
                    db.RDPResourceUserAuthorizations.RemoveRange(auths);
                    db.RDPResources.Remove(res);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Idle reaper: skipping excluded VM {Node}/{VmId}; removed stale row",
                        res.ProxmoxNode, res.ProxmoxVmId);
                    continue;
                }
            }

            _logger.LogInformation("Idle reaper: pausing {Name} ({Backend}:{Node}/{VmId}) via {Action}",
                res.Name, backend.Name, res.ProxmoxNode, res.ProxmoxVmId, backend.PauseAction);

            var ok = await _proxmox.PauseAsync(backend, res.ProxmoxNode!, res.ProxmoxVmId!.Value, backend.PauseAction, ct);
            if (ok)
            {
                res.PowerState = backend.PauseAction == PauseAction.Stop
                    ? ResourcePowerState.Stopped
                    : ResourcePowerState.Suspended;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task ScanManualAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var candidates = await db.RDPResources
            .Where(r => r.Source == ResourceSource.Manual
                        && r.PowerState == ResourcePowerState.Running
                        && !string.IsNullOrEmpty(r.IpAddress))
            .ToListAsync(ct);

        var idle = TimeSpan.FromHours(ManualIdleTimeoutHours);

        foreach (var res in candidates)
        {
            if (_sessions.HasActiveSessions(res.Id)) continue;
            if (!IsReapable(res.LastActivityUtc, idle)) continue;

            _logger.LogInformation("Idle reaper: shutting down manual resource {Name} ({Os}, {Method})",
                res.Name, res.OsType, res.ShutdownMethod);

            var ok = await _shutdown.ShutDownAsync(res, ct);

            if (ok)
            {
                res.PowerState = ResourcePowerState.Stopped;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>
    /// Whether a resource has been idle long enough to reap. Two safeguards protect against reaping a VM
    /// whose true idle time we cannot know:
    /// <list type="bullet">
    /// <item>We always defer at least <paramref name="idle"/> after service start, since a VM that was
    /// active just before a restart would otherwise look idle the instant we come up.</item>
    /// <item>A null <see cref="RDPResource.LastActivityUtc"/> (activity never observed this run) is treated
    /// as "active at startup" rather than "idle forever", so it too gets the full grace period.</item>
    /// </list>
    /// </summary>
    private bool IsReapable(DateTime? lastActivityUtc, TimeSpan idle)
    {
        var now = DateTime.UtcNow;
        // Never reap before a full idle window has elapsed since we started.
        if (now - _startedUtc < idle) return false;
        // Unknown last activity: assume the VM was in use when we started, then require the idle window.
        var since = lastActivityUtc ?? _startedUtc;
        return now - since >= idle;
    }
}
