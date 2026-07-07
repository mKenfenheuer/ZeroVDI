using System.Net.NetworkInformation;
using System.Net.Sockets;
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
///   <item>RDP port reachable (ping + port open) → <see cref="ResourcePowerState.Running"/>.</item>
///   <item>Host pings, or the Proxmox VM reports "running" (booting, RDP not up yet)
///         → <see cref="ResourcePowerState.Starting"/>.</item>
///   <item>Otherwise → <see cref="ResourcePowerState.Stopped"/>.</item>
/// </list>
/// There is no "Unknown" state: absence of any alive signal is treated as Stopped.
/// </summary>
public class ResourceStatusService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxmoxClient _proxmox;
    private readonly ProxmoxBackendProvider _backends;
    private readonly ILogger<ResourceStatusService> _logger;

    public ResourceStatusService(
        IServiceScopeFactory scopeFactory,
        ProxmoxClient proxmox,
        ProxmoxBackendProvider backends,
        ILogger<ResourceStatusService> logger)
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
            try { await ScanOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Resource status scan failed");
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

        foreach (var res in resources)
        {
            var newState = await ResolveStateAsync(res, ct);
            if (res.PowerState != newState)
            {
                res.PowerState = newState;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task<ResourcePowerState> ResolveStateAsync(RDPResource res, CancellationToken ct)
    {
        // Proxmox-reported status: distinguishes suspended/hibernated, and lets a booting VM that
        // hasn't opened RDP yet show as Starting even before its IP pings.
        string? vmStatus = null;
        if (res.Source == ResourceSource.Proxmox && res.ProxmoxBackendId != null
            && res.ProxmoxNode != null && res.ProxmoxVmId != null)
        {
            var backend = await _backends.GetAsync(res.ProxmoxBackendId.Value);
            if (backend != null && backend.IsConfigured)
                vmStatus = await _proxmox.GetStatusAsync(backend, res.ProxmoxNode, res.ProxmoxVmId.Value, ct);
        }

        // A suspended/hibernated VM has no live network presence; report it as such directly.
        if (vmStatus is "suspended" or "paused" or "hibernated")
            return ResourcePowerState.Suspended;

        var vmRunning = string.Equals(vmStatus, "running", StringComparison.OrdinalIgnoreCase);

        // Liveness probes (only meaningful when we have an address to probe).
        var pingOk = false;
        var rdpOk = false;
        if (!string.IsNullOrEmpty(res.IpAddress))
        {
            var port = (ushort)(res.Port > 0 ? res.Port : 3389);
            pingOk = await PingAsync(res.IpAddress!);
            if (pingOk) rdpOk = await IsPortOpenAsync(res.IpAddress!, port);
        }

        if (rdpOk) return ResourcePowerState.Running;       // ping + RDP, or VM up + RDP
        if (pingOk || vmRunning) return ResourcePowerState.Starting; // booting / network up, RDP not yet
        return ResourcePowerState.Stopped;                  // no alive signal at all
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
        catch { return false; }
    }
}
