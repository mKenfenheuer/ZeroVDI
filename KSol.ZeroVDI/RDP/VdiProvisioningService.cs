using System.Collections.Concurrent;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.RDP;

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
    // Serialises VMID allocation + clone start across ALL pools: Proxmox's cluster/nextid does not know
    // about a clone whose task has not registered its VMID yet, so two provisions racing through
    // "nextid → clone" could both pick the same id (the second clone then fails "already exists").
    private static readonly SemaphoreSlim _vmidGate = new(1, 1);

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

        // Fast path: the user already has a usable dedicated instance whose VM still exists.
        var existing = await db.VdiInstances
            .FirstOrDefaultAsync(i => i.PoolId == poolId && i.OwnerUserId == userId
                && i.State != VdiInstanceState.Failed && i.State != VdiInstanceState.Deprovisioning, ct);
        if (existing is { RDPResourceId: { } rid, State: VdiInstanceState.Ready }
            && await VmStillExistsAsync(db, existing, ct))
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
            if (existing is { RDPResourceId: { } rid2, State: VdiInstanceState.Ready }
                && await VmStillExistsAsync(db, existing, ct))
                return new ProvisionResult(rid2, null);

            // A Ready instance whose VM has vanished (deleted out-of-band, lost node) cannot be started.
            // Mark it Deprovisioning so the reprovision path below clears it and clones a fresh desktop.
            if (existing is { State: VdiInstanceState.Ready })
            {
                _logger.LogWarning("VDI: instance {Instance} (VM {VmId}) no longer exists on the backend; recloning",
                    existing.Id, existing.ProxmoxVmId);
                existing.State = VdiInstanceState.Deprovisioning;
                await db.SaveChangesAsync(ct);
            }

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

        // A prior attempt may have left a Failed/Deprovisioning row for this (pool, user). The
        // fast-path/re-check queries skip those states, but the unique (PoolId, OwnerUserId) index
        // does not — so without clearing it first, the INSERT below collides and the user can never
        // reconnect after a single failed provision. Destroy its VM (verified by notes) and drop the
        // rows before creating fresh tracking rows.
        await ClearStaleInstanceAsync(db, backend, pool.Id, userId, ct);

        // Locate the template's current node.
        var templates = await _proxmox.ListTemplatesAsync(backend, ct);
        var template = templates.FirstOrDefault(t => t.VmId == pool.TemplateVmId);
        if (template == null)
            return new ProvisionResult(null, "The pool's template no longer exists. Contact an administrator.");

        var userName = await db.Users.Where(u => u.Id == userId).Select(u => u.UserName).FirstOrDefaultAsync(ct) ?? userId;
        var targetNode = string.IsNullOrWhiteSpace(pool.TargetNode) ? null : pool.TargetNode;

        RDPResource resource;
        VdiInstance instance;
        string cloneName;
        string? upid;
        // VMID allocation, tracking rows and the clone START are one critical section (see _vmidGate).
        await _vmidGate.WaitAsync(ct);
        try
        {
            var newVmId = await _proxmox.GetNextVmIdAsync(backend, pool.VmidRangeStart, pool.VmidRangeEnd, ct);
            if (newVmId == null)
                return new ProvisionResult(null, "No free VM id is available for this pool. Contact an administrator.");
            // cluster/nextid ignores ids our own in-flight rows already claim (a clone that has not yet
            // registered its VMID, or a Failed row awaiting cleanup) — step past those.
            var claimed = (await db.VdiInstances.Select(i => i.ProxmoxVmId).ToListAsync(ct)).ToHashSet();
            int vmid = newVmId.Value;
            while (claimed.Contains(vmid) && (pool.VmidRangeEnd == null || vmid < pool.VmidRangeEnd)) vmid++;
            if (claimed.Contains(vmid))
                return new ProvisionResult(null, "No free VM id is available for this pool. Contact an administrator.");

            cloneName = Sanitize(Expand(pool.NamePattern, pool.Name, userName, vmid));

            // Create the tracking rows up front (state Provisioning) so a crash mid-clone leaves a record
            // the reconcile loop can resume or clean up rather than an orphaned VM with no row.
            resource = new RDPResource
            {
                Source = ResourceSource.VdiClone,
                ProxmoxBackendId = backend.Id,
                ProxmoxNode = targetNode ?? template.Node,
                ProxmoxVmId = vmid,
                Name = cloneName,
                Port = pool.Port,
                OsType = pool.OsType,
                PowerState = ResourcePowerState.Stopped,
                DefaultConnectionDefaults = pool.ConnectionDefaults,
            };
            db.RDPResources.Add(resource);

            instance = new VdiInstance
            {
                PoolId = pool.Id,
                RDPResourceId = resource.Id,
                OwnerUserId = userId,
                ProxmoxNode = resource.ProxmoxNode,
                ProxmoxVmId = vmid,
                State = VdiInstanceState.Provisioning,
                UpdatedUtc = DateTime.UtcNow,
            };
            resource.VdiInstanceId = instance.Id;
            db.VdiInstances.Add(instance);
            await db.SaveChangesAsync(ct);

            // Clone the template (async Proxmox task). Once the POST is accepted the VMID is registered
            // cluster-wide, so the gate can be released while we wait for the copy.
            upid = await _proxmox.CloneAsync(backend, template.Node, pool.TemplateVmId, vmid,
                cloneName, full: pool.CloneMode == CloneMode.Full, targetNode: targetNode,
                targetStorage: string.IsNullOrWhiteSpace(pool.TargetStorage) ? null : pool.TargetStorage, ct);
        }
        finally
        {
            _vmidGate.Release();
        }

        if (upid == null)
            return await FailAsync(db, instance, "Failed to start cloning the desktop.", ct);
        // Remember the task so a provision interrupted by a gateway restart can be resumed (or its VM
        // cleaned up) by the reconcile loop instead of leaving the row stuck in Provisioning forever.
        instance.CloneUpid = upid;
        instance.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var cloneTimeout = TimeSpan.FromSeconds(Math.Max(300, backend.StartTimeoutSeconds * 3));
        if (!await _proxmox.WaitForTaskAsync(backend, template.Node, upid, cloneTimeout, ct))
        {
            // The task may still be running (slow storage). The row keeps the UPID: the reconciler waits
            // for the task to end and then destroys the finished clone (verified by notes) — no leak.
            return await FailAsync(db, instance, "Cloning the desktop did not complete in time. Contact an administrator.", ct);
        }

        await FinishProvisioningAsync(db, backend, pool, resource, instance, userId, userName, ct);

        _logger.LogInformation("VDI: provisioned {Pool} clone {VmId} ({Name}) for {User} -> resource {Res}",
            pool.Name, instance.ProxmoxVmId, cloneName, userName, resource.Id);
        return new ProvisionResult(resource.Id, null);
    }

    /// <summary>
    /// The post-clone half of provisioning: stamp the gateway id into the VM notes (so the binding survives
    /// migration/re-discovery, ProxmoxSyncService leaves the clone alone, and a later destroy can VERIFY the
    /// VM is ours), apply the pool's identity customization while the clone is still stopped, and mark the
    /// instance Ready. Shared by the connect-time path and the reconciler (which resumes a clone that
    /// finished after the gateway lost track of it).
    /// </summary>
    private async Task FinishProvisioningAsync(
        ApplicationDbContext db, ProxmoxBackend backend, VdiPool pool, RDPResource resource, VdiInstance instance,
        string userId, string userName, CancellationToken ct)
    {
        var stamped = await _proxmox.SetNotesAsync(backend, resource.ProxmoxNode!, resource.ProxmoxVmId!.Value,
            ProxmoxNotes.WriteId(null, resource.Id), ct);
        instance.NotesStamped = stamped;
        if (!stamped)
        {
            // Not fatal — but until the stamp lands this VM cannot be destroyed safely, so the reconciler
            // keeps retrying (see ReconcileAsync) and the pool page shows the condition.
            instance.LastError = "Could not stamp the gateway id into the VM notes (check the API token's VM.Config.Options permission); retrying in the background.";
            _logger.LogWarning("VDI: notes stamp failed for clone {VmId} (resource {Res}); will retry", resource.ProxmoxVmId, resource.Id);
        }
        else
        {
            instance.LastError = null;
        }

        // Per-pool identity customization (cloud-init). For VdiIdentityMode.None the template
        // self-customizes on first boot. Applied while the clone is still stopped (before the resolver
        // powers it on), so cloud-init lands on first boot.
        await ApplyIdentityAsync(db, backend, pool, resource, userId, userName, ct);

        instance.State = VdiInstanceState.Ready;
        instance.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The outcome of a guarded VM destroy.</summary>
    public enum DestroyOutcome
    {
        /// <summary>The VM was verified as this instance's clone and destroyed.</summary>
        Destroyed,
        /// <summary>No VM with this instance's binding id was found (already gone) — nothing to do.</summary>
        NotFound,
        /// <summary>A VM exists at the recorded VMID but its notes bind it to a different (or no)
        /// resource — it is NOT our clone (VMID recycled/reassigned). Deliberately left untouched.</summary>
        IdentityMismatch,
        /// <summary>The backend was unreachable or the check/destroy failed; state is unknown.</summary>
        Error,
    }

    /// <summary>
    /// Destroys the Proxmox VM backing <paramref name="instance"/> — but ONLY after verifying the VM is
    /// really this clone. VMIDs are recycled after deletion and the recorded node can be stale after a
    /// migration, so destroying purely by (node, VMID) can obliterate an unrelated VM. We therefore read
    /// the candidate VM's notes and require its <c>ksol-rdpgw-id</c> to equal this instance's
    /// <see cref="VdiInstance.RDPResourceId"/>. Any mismatch, or no matching VM anywhere in the cluster,
    /// results in <see cref="DestroyOutcome.IdentityMismatch"/>/<see cref="DestroyOutcome.NotFound"/> and
    /// <b>no destroy</b>.
    /// </summary>
    public async Task<DestroyOutcome> SafeDestroyInstanceVmAsync(
        ProxmoxBackend backend, VdiInstance instance, CancellationToken ct = default)
    {
        // No binding id means we can't prove which VM is ours — never destroy blindly.
        if (string.IsNullOrWhiteSpace(instance.RDPResourceId))
        {
            _logger.LogWarning("VDI: instance {Instance} (vmid {VmId}) has no RDPResourceId; refusing to destroy any VM.",
                instance.Id, instance.ProxmoxVmId);
            return DestroyOutcome.IdentityMismatch;
        }
        var expectedId = instance.RDPResourceId;

        try
        {
            // Prefer the recorded node, but confirm the VM actually there is ours before touching it.
            if (!string.IsNullOrWhiteSpace(instance.ProxmoxNode))
            {
                var notes = await _proxmox.GetNotesAsync(backend, instance.ProxmoxNode!, instance.ProxmoxVmId, ct);
                var boundId = ProxmoxNotes.ReadId(notes);
                if (boundId == expectedId)
                {
                    await _proxmox.DestroyVmAsync(backend, instance.ProxmoxNode!, instance.ProxmoxVmId, ct);
                    _logger.LogInformation("VDI: destroyed verified clone vmid {VmId} on {Node} (resource {Res}).",
                        instance.ProxmoxVmId, instance.ProxmoxNode, expectedId);
                    return DestroyOutcome.Destroyed;
                }
                if (boundId != null || notes != null)
                {
                    // A VM exists at this VMID/node but is bound to something else (or nothing) — not ours.
                    _logger.LogWarning("VDI: vmid {VmId} on {Node} is bound to '{Bound}', not '{Expected}'. " +
                        "VMID was likely recycled; NOT destroying.",
                        instance.ProxmoxVmId, instance.ProxmoxNode, boundId ?? "(none)", expectedId);
                    // Fall through to a cluster-wide search in case our VM migrated and this VMID got reused.
                }
            }

            // The VM wasn't confirmed on its recorded node (deleted, migrated, or VMID reused). Search the
            // whole cluster for the VM whose notes carry our binding id, and destroy that one if found.
            var vms = await _proxmox.ListVmsAsync(backend, ct);
            foreach (var vm in vms)
            {
                var notes = await _proxmox.GetNotesAsync(backend, vm.Node, vm.VmId, ct);
                if (ProxmoxNotes.ReadId(notes) == expectedId)
                {
                    await _proxmox.DestroyVmAsync(backend, vm.Node, vm.VmId, ct);
                    _logger.LogInformation("VDI: destroyed verified clone vmid {VmId} located on {Node} after node/VMID drift (resource {Res}).",
                        vm.VmId, vm.Node, expectedId);
                    return DestroyOutcome.Destroyed;
                }
            }

            _logger.LogInformation("VDI: no VM bound to resource {Res} found (recorded vmid {VmId}); nothing to destroy.",
                expectedId, instance.ProxmoxVmId);
            return DestroyOutcome.NotFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VDI: error while safely destroying VM for instance {Instance} (vmid {VmId}); left untouched.",
                instance.Id, instance.ProxmoxVmId);
            return DestroyOutcome.Error;
        }
    }

    /// <summary>
    /// Applies the pool's per-clone identity customization. A no-op for <see cref="VdiIdentityMode.None"/>.
    /// Best-effort: logged, never fatal to provisioning.
    ///
    /// <see cref="VdiIdentityMode.CloudInit"/> injects ciuser/cipassword/sshkeys via Proxmox cloud-init,
    /// which cloudbase-init applies on a Windows clone's first boot (and cloud-init on Linux). When the
    /// pool has <see cref="VdiPool.GenerateCredentials"/> set, a unique random username/password is derived
    /// from the owner's account data instead of the static pool credentials, and the same pair is stored as
    /// the owner's per-resource SSO so the gateway logs them straight in.
    /// </summary>
    private async Task ApplyIdentityAsync(
        ApplicationDbContext db, ProxmoxBackend backend, VdiPool pool, RDPResource resource,
        string userId, string userName, CancellationToken ct)
    {
        if (pool.IdentityMode != VdiIdentityMode.CloudInit)
        {
            if (pool.IdentityMode == VdiIdentityMode.GuestAgent)
                _logger.LogDebug("VDI: guest-agent identity for {Pool} not yet applied", pool.Name);
            return;
        }

        string? ciUser;
        string? ciPassword;
        if (pool.GenerateCredentials)
        {
            var gen = VdiCredentialGenerator.Create(userName, userId);
            ciUser = gen.Username;
            ciPassword = gen.Password;

            // Persist the generated pair as the owner's SSO credentials so the console/relay can inject
            // them at connect time (same envelope format as manually stored VM credentials).
            await StoreSsoCredentialsAsync(db, resource.Id, userId, ciUser, ciPassword, ct);
        }
        else
        {
            ciUser = pool.CiUser;
            ciPassword = _credentials.Unprotect(pool.ProtectedCiPassword);
        }

        var ok = await _proxmox.SetCloudInitAsync(
            backend, resource.ProxmoxNode!, resource.ProxmoxVmId!.Value,
            ciUser, ciPassword, pool.CiSshKeys, ct);
        if (!ok)
            _logger.LogWarning("VDI: cloud-init customization failed for {Pool} clone {VmId}; clone may boot uncustomized",
                pool.Name, resource.ProxmoxVmId);
    }

    /// <summary>Stores (or replaces) the owner's encrypted SSO credentials for a freshly provisioned clone.</summary>
    private async Task StoreSsoCredentialsAsync(
        ApplicationDbContext db, string resourceId, string userId, string username, string password, CancellationToken ct)
    {
        var auth = await db.RDPResourceUserAuthorizations
            .FirstOrDefaultAsync(a => a.RDPResourceId == resourceId && a.UserId == userId, ct);
        if (auth == null)
        {
            auth = new RDPResourceUserAuthorization { RDPResourceId = resourceId, UserId = userId };
            db.RDPResourceUserAuthorizations.Add(auth);
        }
        auth.ProtectedUsername = _credentials.Protect(username);
        auth.ProtectedPassword = _credentials.Protect(password);
        auth.ProtectedDomain = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// True if the instance's clone still exists on its pool's backend. A clone can vanish out-of-band
    /// (manually deleted in Proxmox, node lost) leaving a Ready instance that can never start; detecting
    /// that here lets the caller reclone instead of failing on a ghost VM. Treats an unreachable/missing
    /// backend as "exists" so a transient backend outage doesn't trigger a needless reclone.
    /// </summary>
    private async Task<bool> VmStillExistsAsync(ApplicationDbContext db, VdiInstance instance, CancellationToken ct)
    {
        var backendId = await db.VdiPools.Where(p => p.Id == instance.PoolId)
            .Select(p => (int?)p.ProxmoxBackendId).FirstOrDefaultAsync(ct);
        if (backendId == null) return true;

        var backend = await _backends.GetAsync(backendId.Value, ct);
        if (backend == null || !backend.IsConfigured) return true;

        try
        {
            var vms = await _proxmox.ListVmsAsync(backend, ct);
            return vms.Any(v => v.VmId == instance.ProxmoxVmId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VDI: could not verify VM {VmId} existence; assuming it still exists",
                instance.ProxmoxVmId);
            return true;
        }
    }

    /// <summary>
    /// Removes any leftover dedicated instance for <paramref name="poolId"/>/<paramref name="userId"/> that the
    /// connect-time lookups treat as absent (Failed/Deprovisioning). Such a row would otherwise trip the unique
    /// (PoolId, OwnerUserId) index on the next provision. Its VM is destroyed first when it can be VERIFIED as
    /// ours (notes stamp) — a clone that never got stamped is left for the reconciler, which stamps it once its
    /// clone task has finished and then destroys it. The orphaned <see cref="RDPResource"/> row goes too.
    /// </summary>
    private async Task ClearStaleInstanceAsync(ApplicationDbContext db, ProxmoxBackend backend, string poolId, string userId, CancellationToken ct)
    {
        var stale = await db.VdiInstances
            .FirstOrDefaultAsync(i => i.PoolId == poolId && i.OwnerUserId == userId
                && (i.State == VdiInstanceState.Failed || i.State == VdiInstanceState.Deprovisioning), ct);
        if (stale == null) return;

        // A clone task that is still running cannot be destroyed yet and must not be forgotten: leave the
        // row for the reconciler and let this connect fail with a clear message rather than leak a VM.
        if (stale.CloneUpid != null && !stale.NotesStamped)
        {
            var task = await _proxmox.GetTaskStatusAsync(backend, stale.CloneUpid, ct);
            if (task is { Stopped: false })
                throw new InvalidOperationException("A previous desktop for you is still being cloned; please try again in a few minutes.");
            if (task is { Stopped: true, Ok: true } && stale.RDPResourceId != null && stale.ProxmoxNode != null)
                stale.NotesStamped = await _proxmox.SetNotesAsync(backend, stale.ProxmoxNode, stale.ProxmoxVmId,
                    ProxmoxNotes.WriteId(null, stale.RDPResourceId), ct);
        }
        var outcome = await SafeDestroyInstanceVmAsync(backend, stale, ct);
        if (outcome == DestroyOutcome.Error)
            throw new InvalidOperationException("The backend could not be reached to clean up a previous desktop; please try again.");

        await RemoveInstanceRowsAsync(db, stale, ct);
        _logger.LogInformation("VDI: cleared stale {State} instance {Instance} for pool {Pool} / user {User} before reprovision (VM: {Outcome})",
            stale.State, stale.Id, poolId, userId, outcome);
    }

    /// <summary>Drops an instance's tracking rows: its clone resource (+ authorizations) and the instance itself.</summary>
    private static async Task RemoveInstanceRowsAsync(ApplicationDbContext db, VdiInstance instance, CancellationToken ct)
    {
        if (instance.RDPResourceId is { } resId)
        {
            var auths = await db.RDPResourceUserAuthorizations.Where(a => a.RDPResourceId == resId).ToListAsync(ct);
            db.RDPResourceUserAuthorizations.RemoveRange(auths);
            var resource = await db.RDPResources.FirstOrDefaultAsync(r => r.Id == resId, ct);
            if (resource != null) db.RDPResources.Remove(resource);
        }
        db.VdiInstances.Remove(instance);
        await db.SaveChangesAsync(ct);
    }

    private async Task<ProvisionResult> FailAsync(ApplicationDbContext db, VdiInstance instance, string error, CancellationToken ct)
    {
        instance.State = VdiInstanceState.Failed;
        instance.LastError = error;
        instance.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _logger.LogWarning("VDI: provisioning failed for instance {Instance}: {Error}", instance.Id, error);
        return new ProvisionResult(null, error);
    }

    // ==============================================================================================
    // Reconcile loop (driven by VdiReconcileService). Everything the connect-time path cannot do on
    // its own because the process may have died in the middle, or the backend was unreachable:
    //   - Provisioning rows nobody is working on: resume a clone that finished (stamp notes, apply
    //     identity, Ready) or mark it Failed so its VM gets cleaned up — previously such a row was stuck
    //     forever and the unique (pool, owner) index blocked every later connect for that user.
    //   - Failed / Deprovisioning rows: destroy the VM through the notes-verified path, then drop rows.
    //   - Ready rows whose VM vanished out of band: mark Failed (the user gets a fresh clone next time).
    //   - Ready rows whose notes stamp failed: retry the stamp so the VM can be destroyed safely later.
    // Every action is audited as VdiInstanceReconciled.
    // ==============================================================================================

    private static readonly TimeSpan ProvisioningHardCap = TimeSpan.FromHours(2);

    /// <summary>One reconcile pass over every instance that is not simply Ready-and-stamped.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        var instances = await db.VdiInstances
            .Include(i => i.Pool).Include(i => i.RDPResource)
            .ToListAsync(ct);
        if (instances.Count == 0) return;

        var backends = new Dictionary<int, ProxmoxBackend?>();
        var inventories = new Dictionary<int, HashSet<int>?>(); // backendId → live VMIDs (null = unknown/empty)

        foreach (var inst in instances)
        {
            ct.ThrowIfCancellationRequested();
            if (inst.Pool == null) continue;
            var backendId = inst.Pool.ProxmoxBackendId;
            if (!backends.TryGetValue(backendId, out var backend))
                backends[backendId] = backend = await _backends.GetAsync(backendId, ct);
            if (backend == null || !backend.IsConfigured) continue;

            try
            {
                switch (inst.State)
                {
                    case VdiInstanceState.Provisioning:
                        await ReconcileProvisioningAsync(db, audit, backend, inst, ct);
                        break;
                    case VdiInstanceState.Failed:
                    case VdiInstanceState.Deprovisioning:
                    case VdiInstanceState.Returning:
                        await ReconcileDeadAsync(db, audit, backend, inst, ct);
                        break;
                    case VdiInstanceState.Ready:
                    case VdiInstanceState.Leased:
                        if (!inventories.TryGetValue(backendId, out var live))
                        {
                            var vms = await _proxmox.ListVmsAsync(backend, ct);
                            // An empty inventory is far more likely an API blip than a truly empty cluster;
                            // never treat it as "every clone vanished" (same guard as ProxmoxSyncService).
                            inventories[backendId] = live = vms.Count == 0 ? null : vms.Select(v => v.VmId).ToHashSet();
                        }
                        await ReconcileLiveAsync(db, audit, backend, inst, live, ct);
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VDI reconcile: instance {Instance} (vmid {VmId}) skipped this pass", inst.Id, inst.ProxmoxVmId);
            }
        }
    }

    private async Task ReconcileProvisioningAsync(ApplicationDbContext db, IAuditLogger audit, ProxmoxBackend backend, VdiInstance inst, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // The connect-time path may still be working on it: it holds the (pool, user) gate and its clone
        // wait is bounded by max(5 min, 3×StartTimeout). Only look at rows older than that.
        var stuckAfter = TimeSpan.FromSeconds(Math.Max(1200, backend.StartTimeoutSeconds * 3 + 300));
        if (now - inst.UpdatedUtc < stuckAfter) return;
        var gate = _locks.GetOrAdd(Key(inst.PoolId, inst.OwnerUserId ?? ""), _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return; // someone is provisioning this (pool, user) right now
        try
        {
            var task = inst.CloneUpid != null ? await _proxmox.GetTaskStatusAsync(backend, inst.CloneUpid, ct) : null;
            if (task is { Stopped: false } && now - inst.UpdatedUtc < ProvisioningHardCap) return; // clone still running

            if (task is { Stopped: true, Ok: true } && inst.RDPResource != null && inst.OwnerUserId != null)
            {
                // The clone finished after the gateway lost track of it (restart mid-provision). Nothing has
                // booted yet, so the normal post-clone steps can simply be completed now.
                var userName = await db.Users.Where(u => u.Id == inst.OwnerUserId).Select(u => u.UserName).FirstOrDefaultAsync(ct) ?? inst.OwnerUserId;
                await FinishProvisioningAsync(db, backend, inst.Pool!, inst.RDPResource, inst, inst.OwnerUserId, userName, ct);
                _logger.LogInformation("VDI reconcile: resumed interrupted provisioning of vmid {VmId} (pool {Pool}) → Ready", inst.ProxmoxVmId, inst.Pool!.Name);
                await audit.LogAsync(AuditCategory.Resource, "VdiInstanceReconciled",
                    targetType: nameof(VdiPool), targetId: inst.PoolId, targetName: inst.Pool!.Name,
                    detail: new { action = "resumed-provisioning", inst.ProxmoxVmId, inst.OwnerUserId });
                return;
            }

            // Task failed, is unknown (Proxmox forgets old tasks), or ran past the hard cap.
            inst.State = VdiInstanceState.Failed;
            inst.LastError = task == null
                ? "Provisioning was interrupted (gateway restarted before the clone task was recorded or the task is no longer known); the desktop will be recreated on the next connect."
                : task.Ok ? "Provisioning was interrupted before the clone could be finalised."
                          : $"The clone task failed on Proxmox (exit status '{task.ExitStatus}').";
            inst.UpdatedUtc = now;
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("VDI reconcile: stuck Provisioning instance {Instance} (vmid {VmId}) marked Failed: {Error}", inst.Id, inst.ProxmoxVmId, inst.LastError);
            await audit.LogAsync(AuditCategory.Resource, "VdiInstanceReconciled", success: false,
                targetType: nameof(VdiPool), targetId: inst.PoolId, targetName: inst.Pool?.Name,
                detail: new { action = "marked-failed", inst.ProxmoxVmId, inst.OwnerUserId, error = inst.LastError });
        }
        finally { gate.Release(); }
    }

    private async Task ReconcileDeadAsync(ApplicationDbContext db, IAuditLogger audit, ProxmoxBackend backend, VdiInstance inst, CancellationToken ct)
    {
        // Give the connect-time cleanup a moment (it handles its own stale row under the gate).
        if (DateTime.UtcNow - inst.UpdatedUtc < TimeSpan.FromMinutes(2)) return;
        var gate = _locks.GetOrAdd(Key(inst.PoolId, inst.OwnerUserId ?? ""), _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            // A clone whose task is still running cannot be destroyed; once it ends, stamp the notes so the
            // destroy below can VERIFY the VM is the one this row created (the whole point of SafeDestroy).
            if (inst.CloneUpid != null && !inst.NotesStamped)
            {
                var task = await _proxmox.GetTaskStatusAsync(backend, inst.CloneUpid, ct);
                if (task is { Stopped: false } && DateTime.UtcNow - inst.UpdatedUtc < ProvisioningHardCap) return;
                if (task is { Stopped: true, Ok: true } && inst.RDPResourceId != null && inst.ProxmoxNode != null)
                    inst.NotesStamped = await _proxmox.SetNotesAsync(backend, inst.ProxmoxNode, inst.ProxmoxVmId,
                        ProxmoxNotes.WriteId(null, inst.RDPResourceId), ct);
            }

            var outcome = await SafeDestroyInstanceVmAsync(backend, inst, ct);
            if (outcome == DestroyOutcome.Error) return; // backend trouble — retry next pass, keep the rows

            await RemoveInstanceRowsAsync(db, inst, ct);
            _logger.LogInformation("VDI reconcile: cleaned up {State} instance {Instance} (vmid {VmId}, VM {Outcome})",
                inst.State, inst.Id, inst.ProxmoxVmId, outcome);
            await audit.LogAsync(AuditCategory.Resource, "VdiInstanceReconciled",
                success: outcome != DestroyOutcome.IdentityMismatch,
                targetType: nameof(VdiPool), targetId: inst.PoolId, targetName: inst.Pool?.Name,
                detail: new { action = "removed", priorState = inst.State.ToString(), inst.ProxmoxVmId, inst.OwnerUserId, vm = outcome.ToString(), inst.LastError });
        }
        finally { gate.Release(); }
    }

    private async Task ReconcileLiveAsync(ApplicationDbContext db, IAuditLogger audit, ProxmoxBackend backend, VdiInstance inst, HashSet<int>? liveVmIds, CancellationToken ct)
    {
        // VM deleted out of band (Proxmox UI, node lost): the user would hit "VM no longer exists" at
        // connect and be recloned then, but until that happens the dashboard advertises a ghost desktop.
        if (liveVmIds != null && !liveVmIds.Contains(inst.ProxmoxVmId))
        {
            inst.State = VdiInstanceState.Failed;
            inst.LastError = "The VM no longer exists on the backend (deleted outside ZeroVDI); a fresh desktop will be created on the next connect.";
            inst.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("VDI reconcile: vmid {VmId} of instance {Instance} is gone from the inventory; marked Failed", inst.ProxmoxVmId, inst.Id);
            await audit.LogAsync(AuditCategory.Resource, "VdiInstanceReconciled", success: false,
                targetType: nameof(VdiPool), targetId: inst.PoolId, targetName: inst.Pool?.Name,
                detail: new { action = "vm-missing", inst.ProxmoxVmId, inst.OwnerUserId });
            return;
        }

        if (!inst.NotesStamped && inst.RDPResourceId != null && inst.ProxmoxNode != null)
        {
            // Retry the binding stamp (token permissions may have been fixed since). Without it the VM can
            // never be destroyed through the verified path.
            if (await _proxmox.SetNotesAsync(backend, inst.ProxmoxNode, inst.ProxmoxVmId, ProxmoxNotes.WriteId(null, inst.RDPResourceId), ct))
            {
                inst.NotesStamped = true;
                inst.LastError = null;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("VDI reconcile: notes stamp succeeded on retry for vmid {VmId}", inst.ProxmoxVmId);
            }
        }
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
