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
    private static readonly int ManualIdleTimeoutHours = 24;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;
    private readonly SessionTracker _sessions;
    private readonly ResourceShutdownService _shutdown;
    private readonly ILogger<IdleReaperService> _logger;

    public IdleReaperService(
        IServiceScopeFactory scopeFactory,
        ProxmoxClient proxmox,
        ProxmoxBackendProvider backends,
        SessionTracker sessions,
        ResourceShutdownService shutdown,
        ILogger<IdleReaperService> logger)
    {
        _scopeFactory = scopeFactory;
        _proxmox = proxmox;
        _backends = backends;
        _sessions = sessions;
        _shutdown = shutdown;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Idle reaper scan failed");
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

        var candidates = await db.RDPResources
            .Where(r => r.Source == ResourceSource.Proxmox
                        && r.ProxmoxBackendId != null && r.ProxmoxNode != null && r.ProxmoxVmId != null
                        && r.PowerState == ResourcePowerState.Running)
            .ToListAsync(ct);

        foreach (var res in candidates)
        {
            if (!byId.TryGetValue(res.ProxmoxBackendId!.Value, out var backend) || !backend.IsConfigured)
                continue;

            if (_sessions.HasActiveSessions(res.Id)) continue;
            var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, backend.IdleTimeoutHours));
            if (res.LastActivityUtc == null || res.LastActivityUtc > cutoff) continue;

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

        var cutoff = DateTime.UtcNow.AddHours(-ManualIdleTimeoutHours);

        foreach (var res in candidates)
        {
            if (_sessions.HasActiveSessions(res.Id)) continue;
            if (res.LastActivityUtc == null || res.LastActivityUtc > cutoff) continue;

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
}
