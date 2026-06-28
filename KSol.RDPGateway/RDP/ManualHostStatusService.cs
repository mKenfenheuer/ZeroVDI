using System.Net.NetworkInformation;
using System.Net.Sockets;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

public class ManualHostStatusService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ManualHostStatusService> _logger;

    public ManualHostStatusService(IServiceScopeFactory scopeFactory, ILogger<ManualHostStatusService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Manual host status scan failed");
            }

            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var hosts = await db.RDPResources
            .Where(r => r.Source == ResourceSource.Manual && !string.IsNullOrEmpty(r.IpAddress))
            .ToListAsync(ct);

        foreach (var host in hosts)
        {
            var reachable = await ProbeHostAsync(host.IpAddress!, (ushort)(host.Port > 0 ? host.Port : 3389));
            var newState = reachable ? ResourcePowerState.Running : ResourcePowerState.Stopped;
            if (host.PowerState != newState)
            {
                host.PowerState = newState;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    public static async Task<bool> ProbeHostAsync(string ip, ushort port)
    {
        var pingOk = await PingAsync(ip);
        if (!pingOk) return false;
        return await IsPortOpenAsync(ip, port);
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
