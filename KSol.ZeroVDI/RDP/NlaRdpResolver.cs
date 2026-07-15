namespace KSol.ZeroVDI.RDP;

/// <summary>
/// The native-RDP resolver: TCP + X.224 <c>PROTOCOL_HYBRID</c> negotiation + TLS + CredSSP(NLA), by
/// delegating to <see cref="RdpHostConnection"/>. This is today's behaviour, now behind the
/// <see cref="IRdpResolver"/> seam so other host protocols can be plugged in alongside it.
///
/// When the host requested HYBRID (NLA) and the X.224 negotiation fails, we retry once asking for plain
/// <c>PROTOCOL_SSL</c> (TLS-only, no CredSSP). Windows hosts return a clean NEG_FAILURE for an unsupported
/// protocol, but many non-Windows RDP servers — notably <c>xrdp</c> on Linux (Ubuntu VMs) — simply RST the
/// TCP connection the moment they see an NLA request. Those hosts serve the login screen in-band over a
/// TLS-only session instead. The retry lets the gateway reach them the same way mstsc/FreeRDP do.
/// </summary>
public sealed class NlaRdpResolver : IRdpResolver
{
    private const uint PROTOCOL_SSL = 0x00000001;
    private const uint PROTOCOL_HYBRID = 0x00000002;

    // Per-attempt connect/negotiation deadline. An xrdp host that rejects NLA sometimes stalls the socket
    // instead of promptly RSTing the HYBRID request; without a bound the first attempt can burn the whole
    // client-patience budget and the TLS-only retry never runs. A timeout here surfaces as a ConnectException
    // ("RDP negotiation failed") so the HYBRID attempt still falls through to the SSL retry below.
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    public async Task<RdpHostConnection.Connected> ConnectAsync(RdpResolveRequest r, CancellationToken ct)
    {
        try
        {
            return await AttemptAsync(r, r.RequestedProtocols, ct);
        }
        catch (RdpHostConnection.ConnectException ex) when (
            !ct.IsCancellationRequested
            && ex.Status == "RDP negotiation failed"
            && (r.RequestedProtocols & PROTOCOL_HYBRID) != 0
            && r.RequestedProtocols != PROTOCOL_SSL)
        {
            r.Logger.LogInformation(
                "RDP host: NLA negotiation to {Host}:{Port} failed ('{Status}'); retrying TLS-only (SSL). " +
                "Typical of xrdp/Linux hosts that reject NLA.", r.Host, r.Port, ex.Status);
            return await AttemptAsync(r, PROTOCOL_SSL, ct);
        }
    }

    /// <summary>
    /// One connect attempt bounded by <see cref="AttemptTimeout"/>. A timeout (the caller's <paramref name="ct"/>
    /// was NOT the trigger) is normalized to a "RDP negotiation failed" <see cref="RdpHostConnection.ConnectException"/>
    /// so the HYBRID→SSL fallback still fires; a real caller cancellation propagates as-is.
    /// </summary>
    private static async Task<RdpHostConnection.Connected> AttemptAsync(
        RdpResolveRequest r, uint protocols, CancellationToken ct)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(AttemptTimeout);
        try
        {
            return await new RdpHostConnection(r.Host, r.Port, r.Kerberos, r.Logger, r.Transport)
                .ConnectAsync(r.Creds, protocols, attemptCts.Token, r.RoutingToken);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            r.Logger.LogWarning("RDP host: connect attempt to {Host}:{Port} timed out after {Secs}s (protocol 0x{Proto:X})",
                r.Host, r.Port, (int)AttemptTimeout.TotalSeconds, protocols);
            throw new RdpHostConnection.ConnectException("RDP negotiation failed");
        }
    }
}
