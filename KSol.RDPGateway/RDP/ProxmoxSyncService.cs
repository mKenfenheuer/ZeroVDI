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
            // The notes carry the binding id, exclusion marker, and any rdp options / description.
            var notes = await _proxmox.GetNotesAsync(backend, vm.Node, vm.VmId, ct);

            // Excluded VMs are not indexed: skip creating a row and delete any existing row (and its
            // authorizations) for this VM.
            if (ProxmoxNotes.ReadExcluded(notes))
            {
                await DeleteResourcesForVmAsync(db, backend.Id, vm.VmId, ct);
                continue;
            }

            var id = ProxmoxNotes.ReadId(notes);

            // Find every existing row that represents this VM: the one bound by the notes id, plus
            // any rows matching backend+VMID (these arise if a previous notes stamp failed and the
            // VM was re-discovered as a fresh row). Keep one and collapse the rest so duplicates from
            // earlier runs self-heal on each discover.
            var matches = await db.RDPResources
                .Where(r => (id != null && r.Id == id)
                            || (r.Source == ResourceSource.Proxmox
                                && r.ProxmoxBackendId == backend.Id && r.ProxmoxVmId == vm.VmId))
                .ToListAsync(ct);

            // Prefer the notes-id row as the survivor (it matches issued .rdp files / authorizations).
            RDPResource? res = matches.FirstOrDefault(r => r.Id == id) ?? matches.FirstOrDefault();

            if (matches.Count > 1)
            {
                var dupes = matches.Where(r => r.Id != res!.Id).ToList();
                var dupeIds = dupes.Select(r => r.Id).ToList();
                var dupeAuths = await db.RDPResourceUserAuthorizations
                    .Where(a => a.RDPResourceId != null && dupeIds.Contains(a.RDPResourceId))
                    .ToListAsync(ct);
                db.RDPResourceUserAuthorizations.RemoveRange(dupeAuths);
                db.RDPResources.RemoveRange(dupes);
                _logger.LogInformation("Proxmox sync[{Backend}]: collapsed {Count} duplicate row(s) for VM {VmId}",
                    backend.Name, dupes.Count, vm.VmId);
            }

            if (res == null)
            {
                // First time we see this VM: create a row.
                res = new RDPResource
                {
                    Source = ResourceSource.Proxmox,
                    ProxmoxBackendId = backend.Id,
                    Port = backend.DefaultRdpPort,
                };
                db.RDPResources.Add(res);
                await db.SaveChangesAsync(ct); // materialize the GUID
                _logger.LogInformation("Proxmox sync[{Backend}]: discovered VM {VmId} on {Node} -> {Id}",
                    backend.Name, vm.VmId, vm.Node, res.Id);
            }

            // Ensure the VM notes carry this resource's id (durable binding that survives migration
            // and re-discovery). Re-attempt whenever the stamped id is missing or stale.
            if (id != res.Id)
            {
                var stamped = ProxmoxNotes.WriteId(notes, res.Id);
                var ok = await _proxmox.SetNotesAsync(backend, vm.Node, vm.VmId, stamped, ct);
                if (ok) notes = stamped;
            }

            // Refresh mutable data. Node and backend are updated every time so a migrated VM
            // (or a row created against the wrong node) self-heals to the current location.
            res.ProxmoxBackendId = backend.Id;
            res.ProxmoxNode = vm.Node;
            res.ProxmoxVmId = vm.VmId;
            res.Name = string.IsNullOrWhiteSpace(vm.Name) ? $"vm-{vm.VmId}" : vm.Name;
            res.ConfigJson = notes;

            var desc = ProxmoxNotes.ReadDescription(notes);
            if (desc != null) res.Description = desc;

            // Refresh the guest IP while the VM is up so the status probe loop (ResourceStatusService)
            // and connect-time resolve have a current address. Power state itself is owned by that loop.
            if (string.Equals(vm.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                var ip = await _proxmox.GetGuestIpAsync(backend, vm.Node, vm.VmId, ct);
                if (ip != null) res.IpAddress = ip;
            }
        }

        // Prune resources for this backend whose VM no longer exists in the inventory (deleted in
        // Proxmox). The early return above when the inventory is empty guards against wiping every
        // resource on a transient API blip, so we only reach here with a non-empty, trusted list.
        var liveVmIds = vms.Select(v => v.VmId).ToHashSet();
        var stale = await db.RDPResources
            .Where(r => r.Source == ResourceSource.Proxmox && r.ProxmoxBackendId == backend.Id
                        && r.ProxmoxVmId != null && !liveVmIds.Contains(r.ProxmoxVmId.Value))
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            // Remove dependent authorizations first (the FK is NO ACTION, not cascade), then the
            // resources themselves.
            var staleIds = stale.Select(r => r.Id).ToList();
            var auths = await db.RDPResourceUserAuthorizations
                .Where(a => a.RDPResourceId != null && staleIds.Contains(a.RDPResourceId))
                .ToListAsync(ct);
            db.RDPResourceUserAuthorizations.RemoveRange(auths);
            db.RDPResources.RemoveRange(stale);
            _logger.LogInformation("Proxmox sync[{Backend}]: pruned {Count} removed VM(s)",
                backend.Name, stale.Count);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Proxmox sync[{Backend}]: processed {Count} VMs", backend.Name, vms.Count);
        return vms.Count;
    }

    /// <summary>Deletes every resource row for the given backend+VMID and their authorizations.</summary>
    private async Task DeleteResourcesForVmAsync(ApplicationDbContext db, int backendId, int vmid, CancellationToken ct)
    {
        var rows = await db.RDPResources
            .Where(r => r.Source == ResourceSource.Proxmox
                        && r.ProxmoxBackendId == backendId && r.ProxmoxVmId == vmid)
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        var ids = rows.Select(r => r.Id).ToList();
        var auths = await db.RDPResourceUserAuthorizations
            .Where(a => a.RDPResourceId != null && ids.Contains(a.RDPResourceId))
            .ToListAsync(ct);
        db.RDPResourceUserAuthorizations.RemoveRange(auths);
        db.RDPResources.RemoveRange(rows);
        _logger.LogInformation("Proxmox sync[{BackendId}]: removed {Count} excluded/template row(s) for VM {VmId}",
            backendId, rows.Count, vmid);
    }
}
