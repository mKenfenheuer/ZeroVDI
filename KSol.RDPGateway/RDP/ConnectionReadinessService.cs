using System.Collections.Concurrent;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Drives and tracks the connect-readiness sequence per (user, resource) so the browser preflight can
/// kick it off once and then poll for progress. A single job runs per key; concurrent "begin" calls
/// attach to the running job rather than starting a second power-on. Completed/failed jobs linger
/// briefly so a slightly-late poll still sees the terminal state, then are reaped.
/// </summary>
public sealed class ConnectionReadinessService
{
    private readonly VdiResourceResolver _resolver;
    private readonly ILogger<ConnectionReadinessService> _logger;
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public ConnectionReadinessService(VdiResourceResolver resolver, ILogger<ConnectionReadinessService> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    private sealed class Job
    {
        public volatile ReadinessProgress Progress =
            new(ReadinessPhase.Checking, "Checking resource…");
        public DateTime? FinishedUtc;
        public int Started; // 0/1, set via Interlocked so the run kicks off exactly once
    }

    private static string Key(string userId, string resource) => $"{userId}|{resource}";

    /// <summary>Starts readiness for (user, resource) if not already running; returns current progress.</summary>
    public ReadinessProgress Begin(string userId, string resource, ushort requestedPort)
    {
        ReapStale();
        var key = Key(userId, resource);

        // A lingering terminal job from a previous attempt is replaced so "Connect" can be retried.
        var job = _jobs.AddOrUpdate(key,
            _ => new Job(),
            (_, existing) => existing.FinishedUtc != null ? new Job() : existing);

        // Start the background run exactly once per Job instance.
        if (Interlocked.Exchange(ref job.Started, 1) == 0)
        {
            var progress = new Progress<ReadinessProgress>(p => job.Progress = p);
            _ = Task.Run(async () =>
            {
                try
                {
                    job.Progress = await _resolver.RunReadinessAsync(userId, resource, requestedPort, progress, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Readiness run failed for {Key}", key);
                    job.Progress = new ReadinessProgress(ReadinessPhase.Error,
                        "An unexpected error occurred while preparing the connection.", Error: ex.Message);
                }
                finally
                {
                    job.FinishedUtc = DateTime.UtcNow;
                }
            });
        }

        return job.Progress;
    }

    /// <summary>Current progress for (user, resource), or null if nothing has been started.</summary>
    public ReadinessProgress? Status(string userId, string resource)
        => _jobs.TryGetValue(Key(userId, resource), out var job) ? job.Progress : null;

    private void ReapStale()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-2);
        foreach (var kv in _jobs)
        {
            if (kv.Value.FinishedUtc is { } f && f < cutoff)
                _jobs.TryRemove(kv.Key, out _);
        }
    }
}
