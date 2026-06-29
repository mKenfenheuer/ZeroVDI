using System.Collections.Concurrent;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Provisions and tracks the per-user desktops a <see cref="VdiPool"/> hands out. Lazily clones a VM
/// from the pool's template the first time a user connects, surfacing progress through the same
/// <see cref="ReadinessProgress"/> the rest of the connect sequence uses (a new
/// <see cref="ReadinessPhase.Provisioning"/> phase precedes <see cref="ReadinessPhase.Starting"/>).
///
/// The spawned VM becomes an <see cref="RDPResource"/> with <see cref="ResourceSource.VdiClone"/>,
/// bridged to its pool by a <see cref="VdiInstance"/>. From the moment the resource exists, the rest
/// of the connect flow (start, IP, RDP probe) is identical to any Proxmox resource — see
/// <see cref="VdiResourceResolver"/>, which calls into this service as a pre-step.
///
/// Singleton: resolves a scoped <see cref="ApplicationDbContext"/> per call (safe from the hosted
/// resolver). Provisioning per (pool, user) is serialized by a keyed lock so a double-click cannot
/// clone two VMs for one user.
/// </summary>
public class VdiProvisioningService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;
    private readonly CredentialProtector _credentials;
    private readonly ILogger<VdiProvisioningService> _logger;

    // One lock per (pool, user) so concurrent connect attempts don't double-provision.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public VdiProvisioningService(
        IServiceScopeFactory scopeFactory,
        ProxmoxClient proxmox,
        ProxmoxBackendProvider backends,
        CredentialProtector credentials,
        ILogger<VdiProvisioningService> logger)
    {
        _scopeFactory = scopeFactory;
        _proxmox = proxmox;
        _backends = backends;
        _credentials = credentials;
        _logger = logger;
    }

    /// <summary>The outcome of resolving a pool to a connectable resource.</summary>
    public sealed record ProvisionResult(string? ResourceId, string? Error)
    {
        public bool Ok => ResourceId != null;
    }

    /// <summary>
    /// Returns the <see cref="RDPResource"/> id the user should connect to for <paramref name="poolId"/>,
    /// provisioning a clone if the user has none yet. Reports the <see cref="ReadinessPhase.Provisioning"/>
    /// phase through <paramref name="report"/> while a clone is being created. Dedicated pools only in this
    /// slice; floating leasing is added later. Caller must already have verified the user is entitled to
    /// the pool (see <c>ResourceAccessService</c>).
    /// </summary>
    public async Task<ProvisionResult> EnsureResourceForUserAsync(
        string poolId, string userId, Action<ReadinessProgress>? report, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var pool = await db.VdiPools.FirstOrDefaultAsync(p => p.Id == poolId, ct);
        if (pool == null) return new ProvisionResult(null, "This desktop pool no longer exists.");

        if (pool.Kind == VdiPoolKind.Floating)
            return new ProvisionResult(null, "Floating pools are not yet available."); // step 6

        // Fast path: the user already has a usable dedicated instance.
        var existing = await db.VdiInstances
            .FirstOrDefaultAsync(i => i.PoolId == poolId && i.OwnerUserId == userId
                && i.State != VdiInstanceState.Failed && i.State != VdiInstanceState.Deprovisioning, ct);
        if (existing is { RDPResourceId: { } rid, State: VdiInstanceState.Ready })
            return new ProvisionResult(rid, null);

        // Serialize provisioning for this (pool, user).
        var gate = _locks.GetOrAdd(Key(poolId, userId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check inside the lock: another request may have just provisioned.
            existing = await db.VdiInstances
                .FirstOrDefaultAsync(i => i.PoolId == poolId && i.OwnerUserId == userId
                    && i.State != VdiInstanceState.Failed && i.State != VdiInstanceState.Deprovisioning, ct);
            if (existing is { RDPResourceId: { } rid2, State: VdiInstanceState.Ready })
                return new ProvisionResult(rid2, null);

            return await ProvisionDedicatedAsync(db, pool, userId, report, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProvisionResult> ProvisionDedicatedAsync(
        ApplicationDbContext db, VdiPool pool, string userId, Action<ReadinessProgress>? report, CancellationToken ct)
    {
        report?.Invoke(new ReadinessProgress(ReadinessPhase.Provisioning, "Provisioning your desktop…"));

        var backend = await _backends.GetAsync(pool.ProxmoxBackendId, ct);
        if (backend == null || !backend.IsConfigured)
            return new ProvisionResult(null, "The backend for this pool is unavailable. Contact an administrator.");

        // Locate the template's current node.
        var templates = await _proxmox.ListTemplatesAsync(backend, ct);
        var template = templates.FirstOrDefault(t => t.VmId == pool.TemplateVmId);
        if (template == null)
            return new ProvisionResult(null, "The pool's template no longer exists. Contact an administrator.");

        var newVmId = await _proxmox.GetNextVmIdAsync(backend, pool.VmidRangeStart, pool.VmidRangeEnd, ct);
        if (newVmId == null)
            return new ProvisionResult(null, "No free VM id is available for this pool. Contact an administrator.");

        var userName = await db.Users.Where(u => u.Id == userId).Select(u => u.UserName).FirstOrDefaultAsync(ct) ?? userId;
        var cloneName = Sanitize(Expand(pool.NamePattern, pool.Name, userName, newVmId.Value));
        var targetNode = string.IsNullOrWhiteSpace(pool.TargetNode) ? null : pool.TargetNode;

        // Create the tracking rows up front (state Provisioning) so a crash mid-clone leaves a record
        // the reconcile loop can clean up rather than an orphaned VM with no row.
        var resource = new RDPResource
        {
            Source = ResourceSource.VdiClone,
            ProxmoxBackendId = backend.Id,
            ProxmoxNode = targetNode ?? template.Node,
            ProxmoxVmId = newVmId.Value,
            Name = cloneName,
            Port = pool.Port,
            OsType = pool.OsType,
            PowerState = ResourcePowerState.Stopped,
            DefaultConnectionDefaults = pool.ConnectionDefaults,
        };
        db.RDPResources.Add(resource);

        var instance = new VdiInstance
        {
            PoolId = pool.Id,
            RDPResourceId = resource.Id,
            OwnerUserId = userId,
            ProxmoxNode = resource.ProxmoxNode,
            ProxmoxVmId = newVmId.Value,
            State = VdiInstanceState.Provisioning,
        };
        resource.VdiInstanceId = instance.Id;
        db.VdiInstances.Add(instance);
        await db.SaveChangesAsync(ct);

        // Clone the template (async Proxmox task; wait for it).
        var upid = await _proxmox.CloneAsync(backend, template.Node, pool.TemplateVmId, newVmId.Value,
            cloneName, full: pool.CloneMode == CloneMode.Full, targetNode: targetNode,
            targetStorage: string.IsNullOrWhiteSpace(pool.TargetStorage) ? null : pool.TargetStorage, ct);
        if (upid == null)
            return await FailAsync(db, instance, "Failed to start cloning the desktop.", ct);

        var cloneTimeout = TimeSpan.FromSeconds(Math.Max(300, backend.StartTimeoutSeconds * 3));
        if (!await _proxmox.WaitForTaskAsync(backend, template.Node, upid, cloneTimeout, ct))
            return await FailAsync(db, instance, "Cloning the desktop did not complete. Contact an administrator.", ct);

        // Stamp the gateway id into the clone's notes so the binding survives migration/re-discovery,
        // and so ProxmoxSyncService recognises it as a clone to leave alone.
        await _proxmox.SetNotesAsync(backend, resource.ProxmoxNode!, newVmId.Value,
            ProxmoxNotes.WriteId(null, resource.Id), ct);

        // Per-pool identity customization (cloud-init / guest-agent rename+join) is applied here in a
        // later slice (step 3b). For VdiIdentityMode.None the template self-customizes on first boot.
        await ApplyIdentityAsync(backend, pool, resource, userName, ct);

        instance.State = VdiInstanceState.Ready;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("VDI: provisioned {Pool} clone {VmId} ({Name}) for {User} -> resource {Res}",
            pool.Name, newVmId.Value, cloneName, userName, resource.Id);
        return new ProvisionResult(resource.Id, null);
    }

    /// <summary>
    /// Applies the pool's per-clone identity customization. Filled in by step 3b; a no-op for
    /// <see cref="VdiIdentityMode.None"/>. Best-effort: logged, never fatal to provisioning.
    /// </summary>
    private Task ApplyIdentityAsync(ProxmoxBackend backend, VdiPool pool, RDPResource resource, string userName, CancellationToken ct)
    {
        if (pool.IdentityMode == VdiIdentityMode.None) return Task.CompletedTask;
        _logger.LogDebug("VDI: identity mode {Mode} for {Pool} not yet applied (step 3b)", pool.IdentityMode, pool.Name);
        return Task.CompletedTask;
    }

    private async Task<ProvisionResult> FailAsync(ApplicationDbContext db, VdiInstance instance, string error, CancellationToken ct)
    {
        instance.State = VdiInstanceState.Failed;
        await db.SaveChangesAsync(ct);
        _logger.LogWarning("VDI: provisioning failed for instance {Instance}: {Error}", instance.Id, error);
        return new ProvisionResult(null, error);
    }

    private static string Key(string poolId, string userId) => $"{poolId}|{userId}";

    /// <summary>Expands the <c>{pool}</c>/<c>{user}</c>/<c>{n}</c> tokens in a name/hostname pattern.</summary>
    internal static string Expand(string? pattern, string poolName, string userName, int vmid)
        => (pattern ?? "{pool}-{user}")
            .Replace("{pool}", poolName)
            .Replace("{user}", userName)
            .Replace("{n}", vmid.ToString());

    /// <summary>Reduces a name to a DNS/Proxmox-safe label (alphanumerics + hyphen, max 63 chars).</summary>
    internal static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        var s = new string(chars).Trim('-');
        if (s.Length > 63) s = s[..63].TrimEnd('-');
        return string.IsNullOrEmpty(s) ? "vdi" : s;
    }
}
