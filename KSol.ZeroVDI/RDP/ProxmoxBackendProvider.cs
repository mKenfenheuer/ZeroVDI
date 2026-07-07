using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Loads <see cref="ProxmoxBackend"/> rows. Resolves a scoped <see cref="ApplicationDbContext"/> per
/// call so it is safe to use from singletons and hosted services (which must not capture a scoped
/// DbContext).
/// </summary>
public class ProxmoxBackendProvider
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ProxmoxBackendProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>All configured backends.</summary>
    public async Task<IReadOnlyList<ProxmoxBackend>> GetAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ProxmoxBackends.AsNoTracking().ToListAsync(ct);
    }

    /// <summary>A single backend by id, or null if it no longer exists.</summary>
    public async Task<ProxmoxBackend?> GetAsync(int id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ProxmoxBackends.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
    }
}
