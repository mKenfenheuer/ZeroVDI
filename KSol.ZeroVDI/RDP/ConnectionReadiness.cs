namespace KSol.ZeroVDI.RDP;

/// <summary>
/// The stages a connection goes through before the in-browser console is launched. Surfaced to the
/// browser by the connect preflight so the user sees transparent progress instead of a blank wait.
/// </summary>
public enum ReadinessPhase
{
    Checking,      // inspecting the resource / current power state
    Provisioning,  // cloning a VDI desktop from its pool template (first connect / floating lease)
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
    string? Error = null,
    // The concrete resource id the request resolved to. Differs from the requested id when the request
    // targeted a VDI pool entry point: the readiness pre-step provisions/reuses the user's clone and
    // stamps its resource id here so credential/SSO lookups bind to the clone, not the pool (whose id
    // has no per-user SSO row). Null until a Ready snapshot for a resolved request.
    string? ResourceId = null)
{
    public bool Done => Phase == ReadinessPhase.Ready;
    public bool Failed => Phase == ReadinessPhase.Error;
}
