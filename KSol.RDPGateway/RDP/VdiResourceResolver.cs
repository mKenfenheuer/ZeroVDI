using System.Net.NetworkInformation;
using System.Net.Sockets;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Resolves a resource id (the .rdp full address, a GUID) to the backing machine's current host and
/// port, starting a Proxmox VM on demand and waiting until it is reachable before the tunnel is
/// opened. While this waits, the browser console shows its "starting remote computer" UI. Also records
/// active sessions and stamps last activity so <see cref="IdleReaperService"/> can pause idle VMs.
/// </summary>
public class VdiResourceResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;
    private readonly IpmiClient _ipmi;
    private readonly CredentialProtector _credentials;
    private readonly VdiProvisioningService _provisioning;
    private readonly ILogger<VdiResourceResolver> _logger;

    public VdiResourceResolver(
        IServiceScopeFactory scopeFactory,
        ProxmoxClient proxmox,
        ProxmoxBackendProvider backends,
        IpmiClient ipmi,
        CredentialProtector credentials,
        VdiProvisioningService provisioning,
        ILogger<VdiResourceResolver> logger)
    {
        _scopeFactory = scopeFactory;
        _proxmox = proxmox;
        _backends = backends;
        _ipmi = ipmi;
        _credentials = credentials;
        _provisioning = provisioning;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a resource to a reachable host/port, starting a Proxmox VM on demand. Thin wrapper over
    /// <see cref="RunReadinessAsync"/> used by the WebSocket relay (which has no progress UI of its own).
    /// </summary>
    public async Task<(string Host, ushort Port)?> ResolveAsync(string userId, string resource, ushort requestedPort)
    {
        var final = await RunReadinessAsync(userId, resource, requestedPort, progress: null, CancellationToken.None);
        return final is { Phase: ReadinessPhase.Ready, Host: { } host } ? (host, final.Port) : null;
    }

    /// <summary>
    /// Runs the full connect-readiness sequence, reporting each phase through <paramref name="progress"/>
    /// (for the browser preflight). Returns the terminal <see cref="ReadinessProgress"/> (Ready or Error).
    /// The actual power-on/poll logic is identical to what the relay needs, so both paths share it.
    /// </summary>
    public async Task<ReadinessProgress> RunReadinessAsync(
        string userId, string resource, ushort requestedPort,
        IProgress<ReadinessProgress>? progress, CancellationToken ct)
    {
        void Report(ReadinessProgress p) => progress?.Report(p);

        var checking = new ReadinessProgress(ReadinessPhase.Checking, "Checking resource…");
        Report(checking);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // A pool entry point: the supplied id is a VdiPool, not a concrete resource. Provision (or
        // reuse) the user's clone and continue with the concrete resource id. Authorization for the
        // pool is checked by the caller (HomeController/ConnectController via ResourceAccessService).
        if (await db.VdiPools.AnyAsync(p => p.Id == resource, ct))
        {
            var provisioned = await _provisioning.EnsureResourceForUserAsync(resource, userId, Report, ct);
            if (!provisioned.Ok)
                return Fail(provisioned.Error ?? "Could not prepare a desktop for you.");
            resource = provisioned.ResourceId!;
        }

        var res = await db.RDPResources.FirstOrDefaultAsync(r => r.Id == resource, ct);
        if (res == null)
        {
            _logger.LogWarning("Resolve: unknown resource {Resource}", resource);
            return Fail("This resource no longer exists.");
        }

        var port = (ushort)(res.Port > 0 ? res.Port : requestedPort);

        // Manual resources (or Proxmox/VdiClone resources missing their backend linkage):
        // Start → wait for ping → wait for RDP → connect. VDI clones are Proxmox-backed and take the
        // Proxmox start path below whenever their backend/node/VMID linkage is present.
        var proxmoxBacked = res.Source is ResourceSource.Proxmox or ResourceSource.VdiClone;
        if (!proxmoxBacked || res.ProxmoxBackendId == null
            || res.ProxmoxNode == null || res.ProxmoxVmId == null)
        {
            if (string.IsNullOrEmpty(res.IpAddress))
                return Fail("This resource has no address configured. Contact an administrator.");

            // 1) Check if the host is already reachable (ping + RDP). Fast path when already on.
            var hostUp = await PingAsync(res.IpAddress!);
            if (hostUp && await IsPortOpenAsync(res.IpAddress!, port))
            {
                res.PowerState = ResourcePowerState.Running;
                await db.SaveChangesAsync(ct);
                return Ready(res.IpAddress!, port);
            }

            // 2) Host is not ready — attempt to wake it if a wake method is configured.
            if (res.WakeMethod != WakeMethod.None)
            {
                Report(new ReadinessProgress(ReadinessPhase.Starting, "Starting resource…"));
                var wakeOk = false;
                if (res.WakeMethod == WakeMethod.WakeOnLan && !string.IsNullOrEmpty(res.WolMacAddress))
                {
                    try { await WakeOnLan.SendMagicPacketAsync(res.WolMacAddress); wakeOk = true; }
                    catch (Exception ex) { _logger.LogWarning(ex, "WOL failed for {Resource}", resource); }
                }
                else if (res.WakeMethod == WakeMethod.Ipmi && !string.IsNullOrEmpty(res.IpmiHost))
                {
                    var pass = _credentials.Unprotect(res.ProtectedIpmiPassword);
                    wakeOk = await _ipmi.PowerOnAsync(res.IpmiHost, res.IpmiUser ?? "", pass ?? "");
                }

                if (wakeOk)
                {
                    res.PowerState = ResourcePowerState.Starting;
                    await db.SaveChangesAsync(ct);
                }
            }

            // 3) Wait for ping — the host's network stack is booting up.
            var wakeDeadline = DateTime.UtcNow.AddSeconds(120);
            if (!hostUp)
            {
                Report(new ReadinessProgress(ReadinessPhase.WaitingIp, "Waiting for host to come online…"));
                while (DateTime.UtcNow < wakeDeadline && !ct.IsCancellationRequested)
                {
                    if (await PingAsync(res.IpAddress!))
                    {
                        hostUp = true;
                        break;
                    }
                    await Task.Delay(3000, ct);
                }
                if (!hostUp)
                    return Fail($"The host {res.IpAddress} did not respond within the timeout.");
            }

            // 4) Wait for RDP port — the OS is booting services.
            Report(new ReadinessProgress(ReadinessPhase.RdpProbe, "Waiting for remote desktop…"));
            while (DateTime.UtcNow < wakeDeadline && !ct.IsCancellationRequested)
            {
                if (await IsPortOpenAsync(res.IpAddress!, port))
                {
                    res.PowerState = ResourcePowerState.Running;
                    await db.SaveChangesAsync(ct);
                    return Ready(res.IpAddress!, port);
                }
                await Task.Delay(3000, ct);
            }

            return Fail($"The remote desktop service at {res.IpAddress}:{port} is not responding.");
        }

        var backend = await _backends.GetAsync(res.ProxmoxBackendId.Value);
        if (backend == null || !backend.IsConfigured)
        {
            _logger.LogWarning("Resolve: backend {Backend} for resource {Resource} is unavailable",
                res.ProxmoxBackendId, resource);
            return Fail("The backend for this resource is unavailable (backend unreachable). Contact an administrator.");
        }

        var vmid = res.ProxmoxVmId!.Value;

        // Resolve the VM's CURRENT node from the live cluster inventory: it may have live-migrated
        // since the stored node was last refreshed. Fall back to the stored node if the lookup fails.
        var node = res.ProxmoxNode!;
        var vms = await _proxmox.ListVmsAsync(backend, ct);
        var current = vms.FirstOrDefault(v => v.VmId == vmid);
        if (current != null && !string.IsNullOrEmpty(current.Node))
        {
            node = current.Node;
            if (res.ProxmoxNode != node) res.ProxmoxNode = node; // self-heal after a migration
        }

        // Power the VM on (start or resume).
        var status = await _proxmox.GetStatusAsync(backend, node, vmid, ct);
        var wasOff = !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
        if (wasOff)
        {
            Report(new ReadinessProgress(ReadinessPhase.Starting,
                "Starting resource… This may take a few minutes."));
            await SetPowerStateAsync(db, res, ResourcePowerState.Starting);
            if (!await _proxmox.EnsureRunningAsync(backend, node, vmid, ct))
            {
                await SetPowerStateAsync(db, res, ResourcePowerState.Stopped);
                return Fail("Failed to start the VM. Contact an administrator if this persists.");
            }
        }

        // Wait for the guest agent to report an hostname
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, backend.StartTimeoutSeconds));
        Report(new ReadinessProgress(ReadinessPhase.GuestAgent, "Waiting for guest agent…"));
        bool? running = null;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            running = await _proxmox.GetGuestAgentStatusAsync(backend, node, vmid, ct);
            if (running == null)
            {
                Report(new ReadinessProgress(ReadinessPhase.GuestAgent, "Waiting for guest agent…"));
                await Task.Delay(2000, ct);
                continue;
            }

            if (running == true)
            {
                Report(new ReadinessProgress(ReadinessPhase.WaitingIp, "Waiting for IP address…"));
                break;
            }

            await Task.Delay(2000, ct);
            running = null;
        }



        // Wait for the guest agent to report an IP, then for the RDP port to accept connections,
        // bounded by the backend's configured start timeout.
        deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, backend.StartTimeoutSeconds));
        Report(new ReadinessProgress(ReadinessPhase.WaitingIp, "Waiting for IP address…"));
        string? ip = null;
        var probedRdp = false;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            ip = await _proxmox.GetGuestIpAsync(backend, node, vmid, ct);
            if (ip == null)
            {
                Report(new ReadinessProgress(ReadinessPhase.WaitingIp, "Waiting for IP address…"));
                await Task.Delay(2000, ct);
                continue;
            }

            Report(new ReadinessProgress(ReadinessPhase.RdpProbe, "Waiting for remote desktop…"));
            probedRdp = true;
            if (await IsPortOpenAsync(ip, port))
                break;

            await Task.Delay(2000, ct);
            ip = null;
        }

        if (ip == null)
        {
            _logger.LogWarning("Resolve: VM {Node}/{VmId} not reachable within timeout", node, vmid);
            await SetPowerStateAsync(db, res, ResourcePowerState.Stopped);
            return Fail(probedRdp
                ? "Timed out waiting for the remote desktop service (RDP service unavailable)."
                : "Timed out waiting for the VM to report an IP address (no IP address reported).");
        }

        Report(new ReadinessProgress(ReadinessPhase.Finalizing, "Finalizing connection…"));
        res.IpAddress = ip;
        res.PowerState = ResourcePowerState.Running;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Resolve: {Resource} -> {Ip}:{Port}", resource, ip, port);
        return Ready(ip, port);

        ReadinessProgress Ready(string host, ushort p) =>
            new(ReadinessPhase.Ready, "Connecting…", host, p);
        ReadinessProgress Fail(string message) =>
            new(ReadinessPhase.Error, message, Error: message);
    }

    // Live-session registration moved to RdpWebSocketController (which has the user name, client IP and
    // resolved host needed for the admin sessions view). These now only stamp the resource's activity
    // timestamp, which the IdleReaper and UI use.
    public Task OnConnectedAsync(string userId, string resource) => StampActivityAsync(resource);

    public Task OnDisconnectedAsync(string userId, string resource) => StampActivityAsync(resource);

    private async Task StampActivityAsync(string resource)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var res = await db.RDPResources.FirstOrDefaultAsync(r => r.Id == resource);
        if (res != null)
        {
            res.LastActivityUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    private static async Task SetPowerStateAsync(ApplicationDbContext db, RDPResource res, ResourcePowerState state)
    {
        res.PowerState = state;
        await db.SaveChangesAsync();
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

    private static async Task<bool> IsPortOpenAsync(string host, ushort port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
