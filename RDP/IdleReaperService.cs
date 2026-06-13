using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Background service that pauses Proxmox-backed VMs which have had no active gateway session for at
/// least their backend's idle timeout. The pause action (suspend / stop / hibernate) is per-backend.
/// </summary>
public class IdleReaperService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;
    private readonly SessionTracker _sessions;
    private readonly ILogger<IdleReaperService> _logger;

    public IdleReaperService(
        IServiceScopeFactory scopeFactory,
        ProxmoxClient proxmox,
        ProxmoxBackendProvider backends,
        SessionTracker sessions,
        ILogger<IdleReaperService> logger)
    {
        _scopeFactory = scopeFactory;
        _proxmox = proxmox;
        _backends = backends;
        _sessions = sessions;
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
        var backends = await _backends.GetAllAsync(ct);
        if (backends.Count == 0) return;
        var byId = backends.ToDictionary(b => b.Id);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var candidates = await db.RDPResources
            .Where(r => r.Source == ResourceSource.Proxmox
                        && r.ProxmoxBackendId != null && r.ProxmoxNode != null && r.ProxmoxVmId != null
                        && r.PowerState == ResourcePowerState.Running)
            .ToListAsync(ct);

        foreach (var res in candidates)
        {
            if (!byId.TryGetValue(res.ProxmoxBackendId!.Value, out var backend) || !backend.IsConfigured)
                continue;

            // Never pause a resource with a live tunnel.
            if (_sessions.HasActiveSessions(res.Id)) continue;
            // Require a known last-activity older than the cutoff (null = never used since startup).
            var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, backend.IdleTimeoutHours));
            if (res.LastActivityUtc == null || res.LastActivityUtc > cutoff) continue;

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
}
