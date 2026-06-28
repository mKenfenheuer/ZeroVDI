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
    private readonly SessionTracker _sessions;
    private readonly ILogger<VdiResourceResolver> _logger;

    public VdiResourceResolver(
        IServiceScopeFactory scopeFactory,
        ProxmoxClient proxmox,
        ProxmoxBackendProvider backends,
        SessionTracker sessions,
        ILogger<VdiResourceResolver> logger)
    {
        _scopeFactory = scopeFactory;
        _proxmox = proxmox;
        _backends = backends;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<(string Host, ushort Port)?> ResolveAsync(string userId, string resource, ushort requestedPort)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var res = await db.RDPResources.FirstOrDefaultAsync(r => r.Id == resource);
        if (res == null)
        {
            _logger.LogWarning("Resolve: unknown resource {Resource}", resource);
            return null; // fall back to connecting to the literal id (will fail) — safe default.
        }

        var port = (ushort)(res.Port > 0 ? res.Port : requestedPort);

        // Manual resources (or Proxmox resources missing their backend linkage) connect to the
        // stored address directly.
        if (res.Source != ResourceSource.Proxmox || res.ProxmoxBackendId == null
            || res.ProxmoxNode == null || res.ProxmoxVmId == null)
        {
            return string.IsNullOrEmpty(res.IpAddress) ? null : (res.IpAddress!, port);
        }

        var backend = await _backends.GetAsync(res.ProxmoxBackendId.Value);
        if (backend == null || !backend.IsConfigured)
        {
            _logger.LogWarning("Resolve: backend {Backend} for resource {Resource} is unavailable",
                res.ProxmoxBackendId, resource);
            return string.IsNullOrEmpty(res.IpAddress) ? null : (res.IpAddress!, port);
        }

        var vmid = res.ProxmoxVmId!.Value;

        // Resolve the VM's CURRENT node from the live cluster inventory: it may have live-migrated
        // since the stored node was last refreshed. Fall back to the stored node if the lookup fails.
        var node = res.ProxmoxNode!;
        var vms = await _proxmox.ListVmsAsync(backend);
        var current = vms.FirstOrDefault(v => v.VmId == vmid);
        if (current != null && !string.IsNullOrEmpty(current.Node))
        {
            node = current.Node;
            if (res.ProxmoxNode != node)
            {
                res.ProxmoxNode = node; // self-heal the stored node after a migration
            }
        }

        // Power the VM on (start or resume) and wait until the guest agent reports an IP and the RDP
        // port accepts a TCP connection, bounded by the backend's configured start timeout.
        await SetPowerStateAsync(db, res, ResourcePowerState.Starting);
        await _proxmox.EnsureRunningAsync(backend, node, vmid);

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, backend.StartTimeoutSeconds));
        string? ip = null;
        while (DateTime.UtcNow < deadline)
        {
            ip = await _proxmox.GetGuestIpAsync(backend, node, vmid);
            if (ip != null && await IsPortOpenAsync(ip, port))
            {
                break;
            }
            await Task.Delay(2000);
            ip = null;
        }

        if (ip == null)
        {
            _logger.LogWarning("Resolve: VM {Node}/{VmId} not reachable within timeout", node, vmid);
            await SetPowerStateAsync(db, res, ResourcePowerState.Unknown);
            return null;
        }

        res.IpAddress = ip;
        res.PowerState = ResourcePowerState.Running;
        await db.SaveChangesAsync();
        _logger.LogInformation("Resolve: {Resource} -> {Ip}:{Port}", resource, ip, port);
        return (ip, port);
    }

    public async Task OnConnectedAsync(string userId, string resource)
    {
        _sessions.Increment(resource);
        await StampActivityAsync(resource);
    }

    public async Task OnDisconnectedAsync(string userId, string resource)
    {
        _sessions.Decrement(resource);
        await StampActivityAsync(resource);
    }

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
