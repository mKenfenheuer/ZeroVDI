using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP.Bridge;
using KSol.ZeroVDI.RDP.Spice;

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
    private readonly SpiceRdpResolver _spice;

    public RdpResolverFactory(NlaRdpResolver nla, VncRdpResolver vnc, SpiceRdpResolver spice)
    {
        _nla = nla;
        _vnc = vnc;
        _spice = spice;
    }

    public IRdpResolver For(RdpProtocol protocol) => protocol switch
    {
        RdpProtocol.Vnc => _vnc,
        RdpProtocol.Spice => _spice,
        _ => _nla,
    };
}
