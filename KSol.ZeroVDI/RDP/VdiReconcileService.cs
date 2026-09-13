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
    private readonly ILogger<VdiReconcileService> _logger;

    public VdiReconcileService(VdiProvisioningService provisioning, ILogger<VdiReconcileService> logger)
    {
        _provisioning = provisioning;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _provisioning.ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VDI reconcile pass failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
