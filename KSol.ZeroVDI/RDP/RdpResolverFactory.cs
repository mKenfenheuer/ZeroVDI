using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP.Vnc;

namespace KSol.ZeroVDI.RDP;

/// <summary>Picks the <see cref="IRdpResolver"/> for a resource's <see cref="RdpProtocol"/>.</summary>
public interface IRdpResolverFactory
{
    IRdpResolver For(RdpProtocol protocol);
}

/// <inheritdoc/>
public sealed class RdpResolverFactory : IRdpResolverFactory
{
    private readonly NlaRdpResolver _nla;
    private readonly VncRdpResolver _vnc;

    public RdpResolverFactory(NlaRdpResolver nla, VncRdpResolver vnc)
    {
        _nla = nla;
        _vnc = vnc;
    }

    public IRdpResolver For(RdpProtocol protocol) => protocol switch
    {
        RdpProtocol.Vnc => _vnc,
        _ => _nla,
    };
}
