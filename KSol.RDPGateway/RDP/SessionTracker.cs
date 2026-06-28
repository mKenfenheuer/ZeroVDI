using System.Collections.Concurrent;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Tracks the number of live gateway tunnels per resource id. Shared (singleton) between the
/// connection path (which increments/decrements as sessions come and go) and
/// <see cref="IdleReaperService"/> (which only pauses resources with zero active sessions).
/// </summary>
public class SessionTracker
{
    private readonly ConcurrentDictionary<string, int> _active = new();

    public void Increment(string resourceId) =>
        _active.AddOrUpdate(resourceId, 1, (_, n) => n + 1);

    public void Decrement(string resourceId) =>
        _active.AddOrUpdate(resourceId, 0, (_, n) => Math.Max(0, n - 1));

    /// <summary>True if at least one tunnel is currently open to the resource.</summary>
    public bool HasActiveSessions(string resourceId) =>
        _active.TryGetValue(resourceId, out var n) && n > 0;
}
