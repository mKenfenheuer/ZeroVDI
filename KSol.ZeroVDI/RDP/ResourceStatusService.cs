using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Periodically refreshes every resource's <see cref="ResourcePowerState"/> from live liveness
/// probes (ICMP ping + RDP port) combined, for Proxmox resources, with the VM's reported qmpstatus.
///
/// State is derived as follows (first match wins):
/// <list type="bullet">
///   <item>Proxmox VM suspended/paused/hibernated → <see cref="ResourcePowerState.Suspended"/>.</item>
///   <item>A live gateway session, or the RDP port reachable (directly or via a connector, regardless of
///         ping) → <see cref="ResourcePowerState.Running"/>. A Running resource is only downgraded
///         after two consecutive failed probes.</item>
///   <item>Host pings, or the Proxmox VM reports "running" (booting, RDP not up yet)
///         → <see cref="ResourcePowerState.Starting"/>.</item>
///   <item>Otherwise → <see cref="ResourcePowerState.Stopped"/>.</item>
/// </list>
/// There is no "Unknown" state: absence of any alive signal is treated as Stopped.
/// </summary>
public class ResourceStatusService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);
    // Hold off the first sweep so the network probes don't compete with application startup.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;
    /// <summary>Name this worker reports liveness under on the operations page.</summary>
    private const string HeartbeatName = "Resource status probe";
    private readonly ServiceHeartbeats _heartbeats;
    private readonly ConnectorPathSelector _paths;
    private readonly SessionTracker _sessions;
    private readonly ILogger<ResourceStatusService> _logger;

    // Consecutive failed RDP probes per resource that is currently Running (see ResolveStateAsync).
    private const int MissesBeforeDowngrade = 2;
    private readonly ConcurrentDictionary<string, int> _rdpMisses = new();

    public ResourceStatusService(
        IServiceScopeFactory scopeFactory,
        ProxmoxClient proxmox,
        ProxmoxBackendProvider backends,
        ServiceHeartbeats heartbeats,
        ConnectorPathSelector paths,
        SessionTracker sessions,
        ILogger<ResourceStatusService> logger)
    {
        _scopeFactory = scopeFactory;
        _proxmox = proxmox;
        _backends = backends;
        _heartbeats = heartbeats;
        _paths = paths;
        _sessions = sessions;
        _logger = logger;
        _heartbeats.Register(HeartbeatName, ScanInterval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defer the first sweep so startup never competes with it. The scan pings + TCP-probes every
        // resource; unreachable hosts each cost ~2s of ICMP timeout, and firing that whole sweep during
        // boot starves the (initially tiny) thread pool and delays Kestrel from binding by tens of
        // seconds. Waiting ~30s lets the app come up first, then the fleet state fills in.
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
                _logger.LogError(ex, "Resource status scan failed");
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

        var resources = await db.RDPResources.ToListAsync(ct);

        // Probe all resources concurrently. Each probe is network-bound (up to ~2s ping + ~2s TCP
        // connect + a Proxmox HTTP call) and touches no DbContext, so running them serially made a
        // whole-fleet scan take resources×~4s — long enough during the very first scan to starve the
        // thread pool and delay Kestrel from binding at startup. Resolving in parallel keeps a scan at
        // roughly single-probe latency regardless of fleet size; the DB writes are applied afterward on
        // the single (non-thread-safe) DbContext.
        //
        // Proxmox power states come from ONE cluster/resources call per backend per scan rather than a
        // status call per VM (100 VMs used to mean 100 API requests every 15 s).
        var backendIds = resources.Where(IsProxmoxBacked).Select(r => r.ProxmoxBackendId!.Value).Distinct().ToList();
        var stateLists = await Task.WhenAll(backendIds.Select(async id =>
        {
            var backend = await _backends.GetAsync(id, ct);
            return (id, states: backend is { IsConfigured: true } ? await _proxmox.GetVmStatesAsync(backend, ct) : null);
        }));
        var vmStates = stateLists.ToDictionary(x => x.id, x => x.states);

        var resolved = await Task.WhenAll(resources.Select(async res => (res, state: await ResolveStateAsync(res, vmStates, ct))));

        var changed = false;
        foreach (var (res, newState) in resolved)
        {
            if (res.PowerState != newState)
            {
                res.PowerState = newState;
                changed = true;
            }
        }
        if (changed)
            await db.SaveChangesAsync(ct);
    }

    private static bool IsProxmoxBacked(RDPResource res) =>
        (res.Source == ResourceSource.Proxmox || res.Source == ResourceSource.VdiClone)
        && res.ProxmoxBackendId != null && res.ProxmoxNode != null && res.ProxmoxVmId != null;

    private async Task<ResourcePowerState> ResolveStateAsync(RDPResource res,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, ProxmoxVmState>?> vmStates, CancellationToken ct)
    {
        // Proxmox-reported status: distinguishes suspended/hibernated, and lets a booting VM that
        // hasn't opened RDP yet show as Starting even before its IP pings.
        string? vmStatus = null;
        var fromBatch = false;
        if (IsProxmoxBacked(res))
        {
            if (vmStates.TryGetValue(res.ProxmoxBackendId!.Value, out var states) && states != null)
            {
                fromBatch = true;
                if (states.TryGetValue(res.ProxmoxVmId!.Value, out var s))
                    vmStatus = s.Status == "stopped" && s.Lock == "suspended" ? "hibernated" : s.Status;
            }
            else
            {
                // Batch read failed for this backend: fall back to the per-VM call.
                vmStatus = await GetQmpStatusAsync(res, ct);
            }
        }

        // A suspended/hibernated VM has no live network presence; report it as such directly.
        if (vmStatus is "suspended" or "paused" or "hibernated")
            return ResourcePowerState.Suspended;

        var vmRunning = string.Equals(vmStatus, "running", StringComparison.OrdinalIgnoreCase);

        // A live tunnel is proof the desktop is up, whatever a probe says this instant.
        if (_sessions.HasActiveSessions(res.Id))
        {
            _rdpMisses.TryRemove(res.Id, out _);
            return ResourcePowerState.Running;
        }

        // Proxmox says the VM is off: no probe can contradict that (and the path selector's positive
        // cache could otherwise report a just-stopped VM as reachable for up to a minute).
        if (string.Equals(vmStatus, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            _rdpMisses.TryRemove(res.Id, out _);
            return ResourcePowerState.Stopped;
        }

        // Liveness probes (only meaningful when we have an address to probe). The RDP port is probed
        // independently of ping: Windows blocks ICMP echo by default, so gating the port check on a
        // successful ping left every running Windows VM stuck at "Starting". The port probe goes through
        // the path selector so hosts reachable only via a connector are seen too (direct TCP from the
        // gateway can't reach them). Its direct probe is RdpPortProbe, never a bare connect-and-close —
        // this runs every 15 s against every host, and a bare connect locks a GNOME Remote Desktop 50.0
        // host out of the gateway.
        var pingOk = false;
        var rdpOk = false;
        if (!string.IsNullOrEmpty(res.IpAddress))
        {
            var host = res.IpAddress!;
            var port = res.Port > 0 ? res.Port : 3389;
            var ping = PingAsync(host);
            var rdp = !string.IsNullOrEmpty(res.ForcedConnectorId)
                ? _paths.IsReachableViaConnectorAsync(res.ForcedConnectorId!, host, port, ct)
                : _paths.IsReachableAsync(host, port, ct);
            pingOk = await ping;
            try { rdpOk = await rdp; } catch (Exception ex) when (ex is not OperationCanceledException) { rdpOk = false; }
        }

        if (rdpOk)
        {
            _rdpMisses.TryRemove(res.Id, out _);
            return ResourcePowerState.Running;
        }

        // One missed probe (a 2 s timeout under load, a connector reconnecting) shouldn't flip a Running
        // desktop back to "Starting…" on the dashboard. Hold Running until the port has been unreachable
        // for MissesBeforeDowngrade consecutive scans — unless Proxmox reports the VM is no longer running.
        if (res.PowerState == ResourcePowerState.Running && (vmRunning || pingOk || vmStatus == null))
        {
            var misses = _rdpMisses.AddOrUpdate(res.Id, 1, (_, n) => n + 1);
            if (misses < MissesBeforeDowngrade) return ResourcePowerState.Running;
        }
        _rdpMisses.TryRemove(res.Id, out _);

        // "running" without RDP may also be a VM paused in RAM, which cluster/resources can't tell apart.
        // Only this (rare) case needs the per-VM qmpstatus call.
        if (vmRunning && fromBatch && await GetQmpStatusAsync(res, ct) is "paused" or "suspended")
            return ResourcePowerState.Suspended;

        if (pingOk || vmRunning) return ResourcePowerState.Starting; // booting / network up, RDP not yet
        return ResourcePowerState.Stopped;                  // no alive signal at all
    }

    private async Task<string?> GetQmpStatusAsync(RDPResource res, CancellationToken ct)
    {
        var backend = await _backends.GetAsync(res.ProxmoxBackendId!.Value, ct);
        if (backend == null || !backend.IsConfigured) return null;
        return await _proxmox.GetStatusAsync(backend, res.ProxmoxNode!, res.ProxmoxVmId!.Value, ct);
    }

    private static async Task<bool> PingAsync(string host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 2000);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }
}
