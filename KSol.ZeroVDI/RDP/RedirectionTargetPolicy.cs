using System.Net;
using System.Net.Sockets;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Decides whether the gateway may follow a Server Redirection PDU to a <em>different</em> host
/// ([MS-RDPBCGR] 2.2.13.1 <c>TargetNetAddress</c> / <c>TargetFQDN</c>). A session broker — a Windows
/// RD Connection Broker farm, or any load balancer in front of a desktop pool — answers the first
/// connection by naming the session host that should actually serve the user, and a client that
/// ignores it reconnects to the broker forever.
///
/// The target comes off the wire from the host, so it is followed under rules rather than blindly:
/// a compromised or misbehaving desktop could otherwise point the gateway — which connects from
/// inside the datacentre, with the user's credentials — at a machine of its choosing. Loopback, the
/// link-local range (including the cloud metadata address) and other non-routable targets are always
/// refused, and an optional allow-list narrows it further.
///
/// Configuration (<c>Redirection</c> section):
///   <c>FollowTargetHost</c> (bool, default true) — follow a redirect to a different host at all.
///   <c>AllowedTargetHosts</c> (string[]) — when non-empty, the target's name or resolved address
///   must appear here; entries may be host names, addresses, or a trailing-dot domain suffix
///   (<c>.rds.example.com</c>).
/// </summary>
public sealed class RedirectionTargetPolicy
{
    private readonly bool _follow;
    private readonly string[] _allowed;
    private readonly ILogger<RedirectionTargetPolicy> _logger;

    public RedirectionTargetPolicy(IConfiguration config, ILogger<RedirectionTargetPolicy> logger)
    {
        _logger = logger;
        var section = config.GetSection("Redirection");
        _follow = section.GetValue("FollowTargetHost", true);
        _allowed = section.GetSection("AllowedTargetHosts").Get<string[]>() ?? Array.Empty<string>();
    }

    /// <summary>The decision for one redirect.</summary>
    /// <param name="Host">Host to connect to — the target when allowed, otherwise the original.</param>
    /// <param name="Redirected">True when the connection moved to a different host.</param>
    /// <param name="Refusal">Why the target was not followed, for the audit trail; null when it was.</param>
    public sealed record Decision(string Host, bool Redirected, string? Refusal);

    /// <summary>
    /// Resolve where a redirected leg should connect. <paramref name="target"/> is the host named by the
    /// redirect (may be null — brokers that hand a session off on the same machine, like GNOME Remote
    /// Desktop's "Remote Login", omit it); <paramref name="original"/> is the resource's own host.
    /// </summary>
    public Decision Resolve(string? target, string original)
    {
        if (string.IsNullOrWhiteSpace(target)) return new Decision(original, false, null);

        target = target.Trim();
        // Brokers often name the same machine they are; nothing to decide.
        if (string.Equals(target, original, StringComparison.OrdinalIgnoreCase))
            return new Decision(original, false, null);

        if (!_follow)
            return new Decision(original, false, "Redirection:FollowTargetHost is disabled.");

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(target, out var literal)
                ? new[] { literal }
                : Dns.GetHostAddresses(target);
        }
        catch (Exception ex)
        {
            return new Decision(original, false, $"the redirect target '{target}' does not resolve ({ex.Message}).");
        }
        if (addresses.Length == 0)
            return new Decision(original, false, $"the redirect target '{target}' does not resolve.");

        foreach (var address in addresses)
        {
            if (IsForbidden(address))
                return new Decision(original, false,
                    $"the redirect target '{target}' resolves to the non-routable address {address}.");
        }

        if (_allowed.Length > 0 && !IsAllowListed(target, addresses))
            return new Decision(original, false,
                $"the redirect target '{target}' is not in Redirection:AllowedTargetHosts.");

        _logger.LogInformation("Server Redirection: following redirect from {Original} to {Target}", original, target);
        return new Decision(target, true, null);
    }

    /// <summary>
    /// Addresses a desktop must never be able to aim the gateway at: its own loopback (every service
    /// bound to localhost, including the gateway itself), the link-local range — which carries the
    /// 169.254.169.254 cloud metadata endpoint — and the unspecified/multicast ranges.
    /// </summary>
    private static bool IsForbidden(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast) return true;
            if (address.IsIPv4MappedToIPv6) return IsForbidden(address.MapToIPv4());
            return false;
        }
        var b = address.GetAddressBytes();
        if (b[0] == 169 && b[1] == 254) return true;   // link-local incl. cloud metadata
        if (b[0] >= 224) return true;                  // multicast / reserved
        return false;
    }

    private bool IsAllowListed(string target, IPAddress[] addresses)
    {
        foreach (var entry in _allowed)
        {
            var e = entry?.Trim();
            if (string.IsNullOrEmpty(e)) continue;
            if (e.StartsWith('.'))
            {
                if (target.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (string.Equals(e, target, StringComparison.OrdinalIgnoreCase)) return true;
            if (addresses.Any(a => a.ToString().Equals(e, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }
}
