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

    public async Task<RdpHostConnection.Connected> ConnectAsync(RdpResolveRequest r, CancellationToken ct)
    {
        try
        {
            return await new RdpHostConnection(r.Host, r.Port, r.Kerberos, r.Logger, r.Transport)
                .ConnectAsync(r.Creds, r.RequestedProtocols, ct, r.RoutingToken);
        }
        catch (RdpHostConnection.ConnectException ex) when (
            ex.Status == "RDP negotiation failed"
            && (r.RequestedProtocols & PROTOCOL_HYBRID) != 0
            && r.RequestedProtocols != PROTOCOL_SSL)
        {
            r.Logger.LogInformation(
                "RDP host: NLA negotiation to {Host}:{Port} failed ('{Status}'); retrying TLS-only (SSL). " +
                "Typical of xrdp/Linux hosts that reject NLA.", r.Host, r.Port, ex.Status);
            return await new RdpHostConnection(r.Host, r.Port, r.Kerberos, r.Logger, r.Transport)
                .ConnectAsync(r.Creds, PROTOCOL_SSL, ct, r.RoutingToken);
        }
    }
}
