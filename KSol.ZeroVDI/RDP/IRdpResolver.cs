namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Resolves a host — reached over some protocol — into a <b>decrypted, pre-authenticated RDP byte
/// stream</b> that the browser console relay (<see cref="RdpRelaySession"/>) and the session recorder
/// consume opaquely. This is the pluggability seam of the pipeline:
///
///   Host → protocol → <see cref="IRdpResolver"/> → RDP stream → Session / Recording → web client
///
/// The implementation (<see cref="NlaRdpResolver"/>) speaks native RDP: TCP + X.224
/// <c>PROTOCOL_HYBRID</c> + TLS + CredSSP(NLA), delegating to <see cref="RdpHostConnection"/>.
/// </summary>
public interface IRdpResolver
{
    /// <summary>
    /// Connects to and pre-authenticates the host, returning the decrypted RDP stream positioned where
    /// the browser client takes over (the MCS Connect-Initial exchange). Throws
    /// <see cref="RdpHostConnection.ConnectException"/> with a caller-facing status on failure.
    /// </summary>
    Task<RdpHostConnection.Connected> ConnectAsync(RdpResolveRequest request, CancellationToken ct);
}

/// <summary>
/// Everything a resolver needs to open a host. <see cref="RequestedProtocols"/> and
/// <see cref="RoutingToken"/> are meaningful only to the native-RDP resolver (X.224 negotiation flags and
/// a Server-Redirection routing token); other resolvers ignore them, so the two call sites stay uniform.
/// </summary>
public sealed record RdpResolveRequest(
    string Host,
    int Port,
    RdpRelaySession.VmCredentials Creds,
    IHostTransport Transport,
    KerberosAuth? Kerberos,
    uint RequestedProtocols,
    byte[]? RoutingToken,
    ILogger Logger);
