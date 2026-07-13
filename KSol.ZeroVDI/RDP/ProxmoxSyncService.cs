using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.RDP;

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

            // VDI clones are owned by the provisioner, not by discovery: their VMs ARE in the cluster
            // inventory and carry a stamped gateway id, but the row is ResourceSource.VdiClone (or is
            // tracked by a VdiInstance). Skip them entirely here so sync never adopts a clone as a
            // Proxmox resource, rewrites its fields, or prunes it. The provisioner/reconcile loop owns
            // their whole lifecycle.
            if (await IsVdiCloneAsync(db, id, backend.Id, vm.VmId, ct))
                continue;

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
                // First time we see this VM in our DB. If the VM notes already carry a stamped gateway id
                // (e.g. the row was deleted but the VM was never un-stamped, or the DB was restored),
                // ADOPT that id so the VM keeps its identity — never mint a fresh GUID over a stamped one.
                res = new RDPResource
                {
                    Source = ResourceSource.Proxmox,
                    ProxmoxBackendId = backend.Id,
                };
                if (!string.IsNullOrWhiteSpace(id)) res.Id = id!;

                // Seed the OS type from the guest OS — ONLY at first discovery (all later syncs preserve
                // whatever the admin/previous run set).
                var (vga, ostype) = await _proxmox.GetDisplayInfoAsync(backend, vm.Node, vm.VmId, ct);
                if (ProxmoxClient.IsWindowsOsType(ostype)) res.OsType = OsType.Windows;
                res.Port = backend.DefaultRdpPort;

                db.RDPResources.Add(res);
                await db.SaveChangesAsync(ct); // materialize the row
                _logger.LogInformation("Proxmox sync[{Backend}]: discovered VM {VmId} on {Node} -> {Id} (vga={Vga} os={Os})",
                    backend.Name, vm.VmId, vm.Node, res.Id, vga ?? "std", ostype ?? "?");
            }

            // Ensure the VM notes carry this resource's id (durable binding that survives migration
            // and re-discovery). Re-attempt whenever the stamped id is missing or stale.
            if (id != res.Id)
            {
                var stamped = ProxmoxNotes.WriteId(notes, res.Id);
                var ok = await _proxmox.SetNotesAsync(backend, vm.Node, vm.VmId, stamped, ct);
                if (ok) notes = stamped;
            }

            // Refresh location/binding data every time: a migrated VM (or a row created against the wrong
            // node) self-heals to the current node/backend. These are not admin-editable.
            res.ProxmoxBackendId = backend.Id;
            res.ProxmoxNode = vm.Node;
            res.ProxmoxVmId = vm.VmId;
            res.ConfigJson = notes;

            // Name/Description are admin-editable — only seed them on FIRST discovery (when empty), never
            // clobber an admin's edits on re-sync. Port is likewise preserved (only defaulted on the create
            // path above), so a discovered VM the admin re-ports keeps that setting.
            if (string.IsNullOrWhiteSpace(res.Name))
                res.Name = string.IsNullOrWhiteSpace(vm.Name) ? $"vm-{vm.VmId}" : vm.Name;
            if (string.IsNullOrWhiteSpace(res.Description))
            {
                var desc = ProxmoxNotes.ReadDescription(notes);
                if (desc != null) res.Description = desc;
            }

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

    /// <summary>
    /// True if this VM belongs to a VDI clone the provisioner owns: either the notes-id resolves to a
    /// <see cref="ResourceSource.VdiClone"/> resource, or a <see cref="VdiInstance"/> tracks this
    /// backend+VMID. Such VMs are skipped by discovery so sync never fights the provisioner.
    /// </summary>
    private static async Task<bool> IsVdiCloneAsync(
        ApplicationDbContext db, string? notesId, int backendId, int vmid, CancellationToken ct)
    {
        if (notesId != null && await db.RDPResources
                .AnyAsync(r => r.Id == notesId && r.Source == ResourceSource.VdiClone, ct))
            return true;

        return await db.VdiInstances
            .Include(i => i.Pool)
            .AnyAsync(i => i.ProxmoxVmId == vmid && i.Pool!.ProxmoxBackendId == backendId, ct);
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
