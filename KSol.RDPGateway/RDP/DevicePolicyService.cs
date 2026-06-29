using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Loads, caches and applies the tenant-wide <see cref="DevicePolicy"/>. The policy is read on every
/// console load and every relay connect, so it is cached in memory and only re-read from the DB after a
/// save (via <see cref="Invalidate"/>). Registered as a singleton; it opens its own short-lived scope to
/// read the (scoped) DbContext rather than capturing one.
/// </summary>
public sealed class DevicePolicyService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _gate = new();
    private DevicePolicy? _cached;

    public DevicePolicyService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>The current policy, loading (and creating the default singleton row) on first use.</summary>
    public DevicePolicy Get()
    {
        var cached = _cached;
        if (cached != null) return cached;

        lock (_gate)
        {
            if (_cached != null) return _cached;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var policy = db.DevicePolicies.AsNoTracking()
                .FirstOrDefault(p => p.Id == DevicePolicy.SingletonId);
            // No row yet → use defaults (all UserControlled). The row is created lazily on first save.
            _cached = policy ?? new DevicePolicy();
            return _cached;
        }
    }

    /// <summary>Drop the cache so the next <see cref="Get"/> re-reads from the DB.</summary>
    public void Invalidate()
    {
        lock (_gate) _cached = null;
    }

    /// <summary>
    /// Return a copy of <paramref name="requested"/> with every policy-constrained feature clamped to the
    /// admin-enforced value. The input is not mutated.
    /// </summary>
    public ConnectionDefaults Apply(ConnectionDefaults requested)
    {
        var p = Get();
        return new ConnectionDefaults
        {
            Audio = DevicePolicy.Clamp(p.Audio, requested.Audio),
            Clipboard = DevicePolicy.Clamp(p.Clipboard, requested.Clipboard),
            Microphone = DevicePolicy.Clamp(p.Microphone, requested.Microphone),
            Camera = DevicePolicy.Clamp(p.Camera, requested.Camera),
            // Display/performance are not policy-controlled; pass through unchanged.
            GfxMode = requested.GfxMode,
            PerformanceFlags = requested.PerformanceFlags,
        };
    }
}
