using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// A background worker that dies takes its feature with it silently. These are the rules the
/// operations page uses to notice.
/// </summary>
public class ServiceHeartbeatsTests
{
    [Fact]
    public void ARegisteredWorkerIsListedBeforeItsFirstPass()
    {
        var beats = new ServiceHeartbeats();
        beats.Register("Idle reaper", TimeSpan.FromMinutes(5));

        var beat = Assert.Single(beats.All());
        Assert.Null(beat.LastRunUtc);
        Assert.False(beat.IsStale);      // a worker still inside its startup delay is not a fault
    }

    [Fact]
    public void ASuccessfulPassClearsAPreviousFailure()
    {
        var beats = new ServiceHeartbeats();
        beats.Failure("Proxmox sync", TimeSpan.FromMinutes(10), "connection refused");
        Assert.False(beats.All().Single().Healthy);

        beats.Success("Proxmox sync", TimeSpan.FromMinutes(10));

        var beat = beats.All().Single();
        Assert.Null(beat.LastError);
        Assert.True(beat.Healthy);
    }

    [Fact]
    public void APassOlderThanThreeIntervalsIsOverdue()
    {
        var stale = new ServiceHeartbeat("VDI reconciler", TimeSpan.FromMinutes(2),
            DateTime.UtcNow.AddMinutes(-30), null, null);

        Assert.True(stale.IsStale);
        Assert.False(stale.Healthy);
    }

    [Fact]
    public void AFrequentWorkerGetsAFiveMinuteFloorBeforeItCountsAsOverdue()
    {
        // Three intervals of a 15-second probe is 45 seconds — far too tight to survive a slow sweep
        // without crying wolf.
        var recent = new ServiceHeartbeat("Resource status probe", TimeSpan.FromSeconds(15),
            DateTime.UtcNow.AddMinutes(-2), null, null);

        Assert.False(recent.IsStale);
    }
}
