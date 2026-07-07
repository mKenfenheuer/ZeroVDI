using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Chooses the fastest path to a host at connect time. For a given host:port it probes the <b>direct</b>
/// route and <b>every enabled, in-scope, online connector</b> in parallel (TCP connect / RTT), then picks
/// the lowest round-trip time and returns the matching <see cref="IHostTransport"/>. The winner is cached
/// per (host, port) for a short TTL so repeated connects don't re-probe every time.
///
/// This is the single entry point both the RDP connect path and the readiness/reachability checks use, so
/// a host reachable only through a connector is transparently reached and reported reachable.
/// </summary>
public sealed class ConnectorPathSelector
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly ConnectorHub _hub;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ConnectorPathSelector> _logger;
    private readonly ConcurrentDictionary<(string Host, int Port), CacheEntry> _cache = new();

    public ConnectorPathSelector(ConnectorHub hub, IServiceScopeFactory scopes, ILogger<ConnectorPathSelector> logger)
    {
        _hub = hub;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>The outcome of a probe race: which path won and how to build a live transport for it.</summary>
    public sealed record Path(string? ConnectorId, TimeSpan Rtt)
    {
        public bool IsDirect => ConnectorId == null;
    }

    private sealed record CacheEntry(Path? Path, DateTime ExpiryUtc);

    /// <summary>
    /// Returns the fastest reachable path to host:port (cached), or null if no path is reachable.
    /// </summary>
    public async Task<Path?> SelectPathAsync(string host, int port, CancellationToken ct = default)
    {
        var key = (host, port);
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiryUtc > DateTime.UtcNow)
            return cached.Path;

        var path = await RaceAsync(host, port, ct);
        _cache[key] = new CacheEntry(path, DateTime.UtcNow.Add(CacheTtl));
        return path;
    }

    /// <summary>True if host:port is reachable by any path (direct or connector).</summary>
    public async Task<bool> IsReachableAsync(string host, int port, CancellationToken ct = default)
        => await SelectPathAsync(host, port, ct) != null;

    /// <summary>True if host:port is reachable through the given connector (used when a resource pins one).</summary>
    public async Task<bool> IsReachableViaConnectorAsync(string connectorId, string host, int port, CancellationToken ct = default)
        => _hub.IsOnline(connectorId) && await _hub.ProbeAsync(connectorId, host, port, ct) != null;

    /// <summary>
    /// Builds a transport for the chosen path. When <paramref name="forcedConnectorId"/> is set the resource
    /// pins its connection to that connector and no RTT race is run. Otherwise returns the fastest path,
    /// falling back to a <see cref="DirectTcpTransport"/> when nothing wins so callers always get a usable
    /// transport.
    /// </summary>
    public async Task<IHostTransport> ResolveTransportAsync(string host, int port,
        string? forcedConnectorId = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(forcedConnectorId))
            return new ConnectorTcpTransport(_hub, forcedConnectorId);

        var path = await SelectPathAsync(host, port, ct);
        if (path is { IsDirect: false })
            return new ConnectorTcpTransport(_hub, path.ConnectorId!);
        return new DirectTcpTransport(_logger);
    }

    private async Task<Path?> RaceAsync(string host, int port, CancellationToken ct)
    {
        var candidates = new List<Task<Path?>> { ProbeDirectAsync(host, port, ct) };

        // Enumerate enabled, enrolled, online connectors whose scope admits this host.
        List<Connector> connectors;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            connectors = await db.Connectors.AsNoTracking()
                .Where(c => c.Enabled && c.AuthTokenHash != null)
                .ToListAsync(ct);
        }
        foreach (var c in connectors)
        {
            if (!_hub.IsOnline(c.Id)) continue;
            if (!ScopeAdmits(c.AllowScope, host)) continue;
            candidates.Add(ProbeConnectorAsync(c.Id, host, port, ct));
        }

        var results = await Task.WhenAll(candidates);
        var best = results.Where(r => r != null).OrderBy(r => r!.Rtt).FirstOrDefault();
        if (best != null)
            _logger.LogDebug("Path to {Host}:{Port} → {Path} ({Rtt}ms)", host, port,
                best.IsDirect ? "direct" : $"connector {best.ConnectorId}", best.Rtt.TotalMilliseconds);
        else
            _logger.LogInformation("No path to {Host}:{Port} (direct + {N} connectors all unreachable)",
                host, port, candidates.Count - 1);
        return best;
    }

    private static async Task<Path?> ProbeDirectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return client.Connected ? new Path(null, sw.Elapsed) : null;
        }
        catch { return null; }
    }

    private async Task<Path?> ProbeConnectorAsync(string connectorId, string host, int port, CancellationToken ct)
    {
        var rtt = await _hub.ProbeAsync(connectorId, host, port, ct);
        return rtt is { } r ? new Path(connectorId, r) : null;
    }

    /// <summary>
    /// True if an allow-scope admits the given host. Empty scope ⇒ admits everything. Each line is either a
    /// literal host (case-insensitive) or a CIDR that the host's IP must fall within.
    /// </summary>
    internal static bool ScopeAdmits(string? scope, string host)
    {
        if (string.IsNullOrWhiteSpace(scope)) return true;
        var patterns = scope.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IPAddress? hostIp = IPAddress.TryParse(host, out var ip) ? ip : null;
        foreach (var p in patterns)
        {
            if (string.Equals(p, host, StringComparison.OrdinalIgnoreCase)) return true;
            if (hostIp != null && p.Contains('/') && CidrContains(p, hostIp)) return true;
        }
        return false;
    }

    private static bool CidrContains(string cidr, IPAddress ip)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix))
            return false;
        if (network.AddressFamily != ip.AddressFamily) return false;
        var netBytes = network.GetAddressBytes();
        var ipBytes = ip.GetAddressBytes();
        if (netBytes.Length != ipBytes.Length) return false;
        int fullBytes = prefix / 8, rem = prefix % 8;
        for (int i = 0; i < fullBytes; i++)
            if (netBytes[i] != ipBytes[i]) return false;
        if (rem == 0) return true;
        int mask = (byte)(0xFF << (8 - rem));
        return (netBytes[fullBytes] & mask) == (ipBytes[fullBytes] & mask);
    }

    /// <summary>Drops any cached path for a host (e.g. after a connector goes offline).</summary>
    public void Invalidate(string host, int port) => _cache.TryRemove((host, port), out _);
}
