using System.Collections.Concurrent;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Short-lived store for RDP Server Redirection state, keyed by (user, resource). When the host (e.g.
/// GNOME Remote Desktop's "Remote Login" broker) redirects a session, the relay stashes the routing token
/// — and any one-time session credentials the broker handed back — here, then asks the browser to
/// reconnect; the next WebSocket connect for the same (user, resource) consumes them and uses them for the
/// reconnect (routing token in the X.224 Connection Request, credentials for NLA) so it lands on and
/// authenticates to the redirected session.
///
/// Entries are single-use and expire quickly — a redirect is followed immediately by a reconnect.
/// </summary>
public sealed class RedirectionTokenCache(ILogger<RedirectionTokenCache> logger)
{
    /// <summary>
    /// Routing token plus the optional broker-supplied session credentials for the reconnect, and the
    /// recording-continuation state so all legs of a redirect/handover chain (GNOME "Remote Login":
    /// initial → greeter → logged-in session) record into ONE recording instead of three. Each leg writes
    /// to <c>{BaseDir}/leg{Leg}</c>; the muxer concatenates them in order.
    /// </summary>
    public sealed record Pending(byte[] Token, string? Username, string? Domain, string? Password,
        string? RecordingId = null, string? BaseDir = null, int Leg = 0);

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (Pending Pending, DateTime Expires)> _entries = new();

    private static string Key(string userId, string resourceId) => userId + "|" + resourceId;

    public void Store(string userId, string resourceId, Pending pending)
    {
        _entries[Key(userId, resourceId)] = (pending, DateTime.UtcNow + Ttl);
        logger.LogInformation("RedirectionTokenCache: stored {Len}B token (creds={HasCreds}) for '{Key}'",
            pending.Token.Length, pending.Username != null, Key(userId, resourceId));
    }

    /// <summary>Removes and returns the pending redirection state for (user, resource), or null if none/expired.</summary>
    public Pending? Consume(string userId, string resourceId)
    {
        var hit = _entries.TryRemove(Key(userId, resourceId), out var e) && e.Expires > DateTime.UtcNow;
        logger.LogInformation("RedirectionTokenCache: consume '{Key}' -> {Result}",
            Key(userId, resourceId), hit ? $"{e.Pending.Token.Length}B" : "MISS");
        return hit ? e.Pending : null;
    }
}
