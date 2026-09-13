namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Drives <see cref="VdiProvisioningService.ReconcileAsync"/> on a timer. The connect-time provisioning
/// path is a single in-process operation; if the gateway restarts, the backend drops out, or a clone
/// task outlives its wait, the tracking rows are left in a state nobody would ever touch again
/// (Provisioning forever, Failed with a live VM behind it, Ready for a VM deleted in Proxmox). This loop
/// is the second half of the broker that makes those states converge: resume, clean up, or flag.
/// </summary>
public sealed class VdiReconcileService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly VdiProvisioningService _provisioning;
    /// <summary>Name this worker reports liveness under on the operations page.</summary>
    private const string HeartbeatName = "VDI reconciler";
    private readonly ServiceHeartbeats _heartbeats;
    private readonly ILogger<VdiReconcileService> _logger;

    public VdiReconcileService(VdiProvisioningService provisioning, ServiceHeartbeats heartbeats,
        ILogger<VdiReconcileService> logger)
    {
        _provisioning = provisioning;
        _heartbeats = heartbeats;
        _logger = logger;
        _heartbeats.Register(HeartbeatName, Interval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _provisioning.ReconcileAsync(stoppingToken);
                _heartbeats.Success(HeartbeatName, Interval);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VDI reconcile pass failed");
                _heartbeats.Failure(HeartbeatName, Interval, ex.Message);
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
