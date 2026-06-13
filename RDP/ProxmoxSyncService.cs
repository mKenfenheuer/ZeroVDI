using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Discovers and refreshes Proxmox-backed <see cref="RDPResource"/> rows.
///
/// A VM is bound to its resource row by the <c>ksol-rdpgw-id</c> stamped in the VM notes (see
/// <see cref="ProxmoxNotes"/>), NOT by its (node, VMID) — so the binding, the resource GUID, the
/// user authorizations and the issued .rdp files all survive a live-migration to another node. The
/// stored node/IP/power-state are mutable data, refreshed from the live inventory; a VM seen for the
/// first time gets a new resource row and the id is written back into its notes.
///
/// Runs periodically and can be invoked on demand from the admin UI via <see cref="SyncAllAsync"/> /
/// <see cref="SyncBackendAsync"/>.
/// </summary>
public class ProxmoxSyncService : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;
    private readonly ILogger<ProxmoxSyncService> _logger;

    public ProxmoxSyncService(
        IServiceScopeFactory scopeFactory,
        ProxmoxClient proxmox,
        ProxmoxBackendProvider backends,
        ILogger<ProxmoxSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _proxmox = proxmox;
        _backends = backends;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncAllAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Proxmox sync failed");
            }

            try { await Task.Delay(SyncInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Discovers/refreshes every configured backend. Returns the total VMs processed.</summary>
    public async Task<int> SyncAllAsync(CancellationToken ct = default)
    {
        var backends = await _backends.GetAllAsync(ct);
        var total = 0;
        foreach (var backend in backends.Where(b => b.IsConfigured))
        {
            var n = await SyncBackendAsync(backend, ct);
            if (n > 0) total += n;
        }
        return total;
    }

    /// <summary>
    /// Discovers/refreshes resources from a single backend's inventory. Returns the number of VMs
    /// processed, or -1 if the backend is not configured.
    /// </summary>
    public async Task<int> SyncBackendAsync(ProxmoxBackend backend, CancellationToken ct = default)
    {
        if (!backend.IsConfigured) return -1;

        var vms = await _proxmox.ListVmsAsync(backend, ct);
        if (vms.Count == 0) return 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var vm in vms)
        {
            // The notes carry the binding id (and any rdp options / description).
            var notes = await _proxmox.GetNotesAsync(backend, vm.Node, vm.VmId, ct);
            var id = ProxmoxNotes.ReadId(notes);

            RDPResource? res = id != null
                ? await db.RDPResources.FirstOrDefaultAsync(r => r.Id == id, ct)
                : null;

            if (res == null)
            {
                // First time we see this VM (no id, or the id points at a deleted row): create a row
                // and stamp the new id back into the VM notes so the binding is durable.
                res = new RDPResource
                {
                    Source = ResourceSource.Proxmox,
                    ProxmoxBackendId = backend.Id,
                    Port = backend.DefaultRdpPort,
                };
                db.RDPResources.Add(res);
                await db.SaveChangesAsync(ct); // materialize the GUID

                var stamped = ProxmoxNotes.WriteId(notes, res.Id);
                await _proxmox.SetNotesAsync(backend, vm.Node, vm.VmId, stamped, ct);
                notes = stamped;
                _logger.LogInformation("Proxmox sync[{Backend}]: discovered VM {VmId} on {Node} -> {Id}",
                    backend.Name, vm.VmId, vm.Node, res.Id);
            }

            // Refresh mutable data. Node and backend are updated every time so a migrated VM
            // (or a row created against the wrong node) self-heals to the current location.
            res.ProxmoxBackendId = backend.Id;
            res.ProxmoxNode = vm.Node;
            res.ProxmoxVmId = vm.VmId;
            res.Name = string.IsNullOrWhiteSpace(vm.Name) ? $"vm-{vm.VmId}" : vm.Name;
            res.PowerState = MapState(vm.Status);
            res.ConfigJson = notes;

            var desc = ProxmoxNotes.ReadDescription(notes);
            if (desc != null) res.Description = desc;
            var opts = ProxmoxNotes.ReadRdpOptions(notes);
            if (opts != null) res.RdpOptions = opts;

            if (res.PowerState == ResourcePowerState.Running)
            {
                var ip = await _proxmox.GetGuestIpAsync(backend, vm.Node, vm.VmId, ct);
                if (ip != null) res.IpAddress = ip;
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Proxmox sync[{Backend}]: processed {Count} VMs", backend.Name, vms.Count);
        return vms.Count;
    }

    private static ResourcePowerState MapState(string status) => status.ToLowerInvariant() switch
    {
        "running" => ResourcePowerState.Running,
        "stopped" => ResourcePowerState.Stopped,
        "suspended" or "paused" => ResourcePowerState.Suspended,
        _ => ResourcePowerState.Unknown,
    };
}
