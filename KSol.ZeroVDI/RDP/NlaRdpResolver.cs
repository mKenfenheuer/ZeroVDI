namespace KSol.ZeroVDI.RDP;

/// <summary>
/// The native-RDP resolver: TCP + X.224 <c>PROTOCOL_HYBRID</c> negotiation + TLS + CredSSP(NLA), by
/// delegating to <see cref="RdpHostConnection"/>. This is today's behaviour, now behind the
/// <see cref="IRdpResolver"/> seam so other host protocols can be plugged in alongside it.
/// </summary>
public sealed class NlaRdpResolver : IRdpResolver
{
    public Task<RdpHostConnection.Connected> ConnectAsync(RdpResolveRequest r, CancellationToken ct)
        => new RdpHostConnection(r.Host, r.Port, r.Kerberos, r.Logger, r.Transport)
            .ConnectAsync(r.Creds, r.RequestedProtocols, ct, r.RoutingToken);
}
