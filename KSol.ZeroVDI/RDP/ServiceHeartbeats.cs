using System.Collections.Concurrent;

namespace KSol.ZeroVDI.RDP;

/// <summary>One background worker's last completed pass.</summary>
/// <param name="Name">Display name of the worker.</param>
/// <param name="Interval">How often the worker is supposed to run.</param>
/// <param name="LastRunUtc">When its last pass finished, or null if it has not completed one yet.</param>
/// <param name="LastError">Message from the last failed pass, cleared by the next success.</param>
/// <param name="Detail">Short human-readable summary of what the last pass did.</param>
public sealed record ServiceHeartbeat(string Name, TimeSpan Interval, DateTime? LastRunUtc,
    string? LastError, string? Detail)
{
    /// <summary>
    /// The worker has missed its schedule by a wide margin (three intervals, floor 5 minutes). A
    /// background service that dies takes its feature with it silently — an idle reaper that stopped
    /// leaves VMs burning money, a reconciler that stopped leaves desktops stuck in Provisioning.
    /// </summary>
    public bool IsStale
    {
        get
        {
            if (LastRunUtc is not { } last) return false;   // hasn't run yet; startup delay is normal
            var grace = Interval * 3;
            if (grace < TimeSpan.FromMinutes(5)) grace = TimeSpan.FromMinutes(5);
            return DateTime.UtcNow - last > grace;
        }
    }

    public bool Healthy => LastError == null && !IsStale;
}

/// <summary>
/// In-memory registry of background-worker liveness, surfaced on the operations page. Workers report
/// after every pass; nothing here is persisted, so the view is always "since this process started" —
/// which is exactly the question being asked ("is this instance working?").
/// </summary>
public sealed class ServiceHeartbeats
{
    private readonly ConcurrentDictionary<string, ServiceHeartbeat> _beats = new();

    /// <summary>Declare a worker before its first pass, so the page lists it as pending rather than
    /// omitting it entirely.</summary>
    public void Register(string name, TimeSpan interval) =>
        _beats.TryAdd(name, new ServiceHeartbeat(name, interval, null, null, null));

    /// <summary>Record a completed pass.</summary>
    public void Success(string name, TimeSpan interval, string? detail = null) =>
        _beats[name] = new ServiceHeartbeat(name, interval, DateTime.UtcNow, null, detail);

    /// <summary>Record a failed pass; the message stays visible until the next success.</summary>
    public void Failure(string name, TimeSpan interval, string error) =>
        _beats[name] = new ServiceHeartbeat(name, interval, DateTime.UtcNow, error, null);

    public IReadOnlyList<ServiceHeartbeat> All() =>
        _beats.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
