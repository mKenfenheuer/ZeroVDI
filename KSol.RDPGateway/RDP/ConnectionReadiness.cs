namespace KSol.RDPGateway.RDP;

/// <summary>
/// The stages a connection goes through before the in-browser console is launched. Surfaced to the
/// browser by the connect preflight so the user sees transparent progress instead of a blank wait.
/// </summary>
public enum ReadinessPhase
{
    Checking,      // inspecting the resource / current power state
    Starting,      // VM is being started or resumed
    GuestAgent,    // waiting for the QEMU guest agent to respond
    WaitingIp,     // waiting for the guest agent to report an IP
    RdpProbe,      // waiting for the RDP port to accept connections
    Finalizing,    // last checks before handing off
    Ready,         // host is reachable; console may launch
    Error,         // gave up — see Message for the actionable reason
}

/// <summary>A snapshot of a connection's readiness, polled by the preflight page.</summary>
public sealed record ReadinessProgress(
    ReadinessPhase Phase,
    string Message,
    string? Host = null,
    ushort Port = 0,
    string? Error = null)
{
    public bool Done => Phase == ReadinessPhase.Ready;
    public bool Failed => Phase == ReadinessPhase.Error;
}
