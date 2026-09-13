using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Enforces recording retention: periodically deletes recordings that exceed the configured age, and
/// (optionally) trims the oldest recordings when the total on-disk size exceeds a cap. Deletions are
/// audited. Both limits are off by default (RetentionDays = 0, MaxTotalGB = 0) so the existing behaviour
/// — keep everything — is unchanged until an admin opts in.
///
///   Recording:RetentionDays  — delete completed recordings older than N days (0 = keep forever).
///   Recording:MaxTotalGB     — cap total recordings size; oldest are purged first (0 = no cap).
///   Recording:RetentionSweepHours — sweep cadence (default 6h).
///
/// Auditor/Admin visibility of purges is via the AuditEvent log (RecordingPurged). The hash chain is NOT
/// rewritten on purge — a gap is expected and legitimate when retention removes old rows; chain
/// verification covers tampering with retained rows, not policy-driven expiry.
/// </summary>
public sealed class RecordingRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    /// <summary>Name this worker reports liveness under on the operations page.</summary>
    private const string HeartbeatName = "Recording retention";
    private readonly ServiceHeartbeats _heartbeats;
    private readonly ILogger<RecordingRetentionService> _logger;

    public RecordingRetentionService(IServiceScopeFactory scopes, IConfiguration config,
        ServiceHeartbeats heartbeats, ILogger<RecordingRetentionService> logger)
    {
        _scopes = scopes;
        _config = config;
        _heartbeats = heartbeats;
        _logger = logger;
        _heartbeats.Register(HeartbeatName,
            TimeSpan.FromHours(Math.Max(1, config.GetValue("Recording:RetentionSweepHours", 6))));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var sweepHours = Math.Max(1, _config.GetValue("Recording:RetentionSweepHours", 6));
        // Small initial delay so startup migrations/seeding settle first.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var interval = TimeSpan.FromHours(sweepHours);
            try
            {
                await SweepAsync(ct);
                _heartbeats.Success(HeartbeatName, interval);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RecordingRetentionService: sweep failed");
                _heartbeats.Failure(HeartbeatName, interval, ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromHours(sweepHours), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var retentionDays = _config.GetValue("Recording:RetentionDays", 0);
        var maxTotalGb = _config.GetValue("Recording:MaxTotalGB", 0d);
        if (retentionDays <= 0 && maxTotalGb <= 0) return; // nothing to enforce

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        // 1) Age-based expiry.
        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var expired = await db.Recordings
                .Where(r => r.StartedUtc < cutoff)
                .OrderBy(r => r.StartedUtc)
                .ToListAsync(ct);
            foreach (var rec in expired)
                await PurgeAsync(db, audit, rec, $"age>{retentionDays}d", ct);
            if (expired.Count > 0) await db.SaveChangesAsync(ct);
        }

        // 2) Size cap: purge oldest until under the cap.
        if (maxTotalGb > 0)
        {
            var capBytes = (long)(maxTotalGb * 1024 * 1024 * 1024);
            var all = await db.Recordings.OrderBy(r => r.StartedUtc).ToListAsync(ct);
            var sized = all.Select(r => (rec: r, size: DirSize(r))).ToList();
            long total = sized.Sum(x => x.size);
            int i = 0;
            while (total > capBytes && i < sized.Count)
            {
                var (rec, size) = sized[i++];
                await PurgeAsync(db, audit, rec, $"sizeCap>{maxTotalGb}GB", ct);
                total -= size;
            }
            if (i > 0) await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Delete a recording's files/dir and remove the row. Caller batches SaveChanges.</summary>
    private async Task PurgeAsync(ApplicationDbContext db, IAuditLogger audit, Recording rec, string reason,
        CancellationToken ct)
    {
        try
        {
            var path = rec.DesktopFilePath;
            if (!string.IsNullOrEmpty(path))
            {
                var baseDir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                    Directory.Delete(baseDir, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RecordingRetentionService: deleting files for {Id} failed; dropping row anyway", rec.Id);
        }

        db.Recordings.Remove(rec);
        await audit.LogAsync(AuditCategory.Recording, "RecordingPurged",
            targetType: nameof(Recording), targetId: rec.Id,
            detail: new { reason, rec.StartedUtc, ageDays = (int)(DateTime.UtcNow - rec.StartedUtc).TotalDays });
        _logger.LogInformation("RecordingRetentionService: purged recording {Id} ({Reason})", rec.Id, reason);
    }

    /// <summary>Best-effort on-disk size of a recording's base directory (or its file).</summary>
    private static long DirSize(Recording rec)
    {
        try
        {
            var path = rec.DesktopFilePath;
            if (string.IsNullOrEmpty(path)) return 0;
            var baseDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                return new DirectoryInfo(baseDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch { return 0; }
    }
}
