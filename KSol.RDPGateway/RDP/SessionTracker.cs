using System.Collections.Concurrent;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// A single live gateway tunnel. Carries the metadata the admin "active sessions" view shows and the
/// <see cref="CancellationTokenSource"/> the force-disconnect action trips to abort the relay loop.
/// </summary>
public sealed class ActiveSession
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public string? UserName { get; init; }
    public required string ResourceId { get; init; }
    public string? ResourceName { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
    public string? ClientIp { get; init; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Tripped by an admin force-disconnect; the relay's run token is linked to it.</summary>
    public CancellationTokenSource Cancellation { get; } = new();
}

/// <summary>
/// Registry of all live gateway tunnels. Singleton, shared between the connection path (register on
/// connect, remove on disconnect), <see cref="IdleReaperService"/> (which only pauses resources with
/// zero active sessions), and the admin sessions view / force-disconnect.
///
/// NOTE: this is in-memory and therefore single-instance only. In a multi-node HA deployment it must be
/// backed by a distributed store; tracked as roadmap item #8.
/// </summary>
public class SessionTracker
{
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();

    /// <summary>
    /// Registers a new live session and returns its record (including the cancellation source the relay
    /// must link its run token to, so force-disconnect works).
    /// </summary>
    public ActiveSession Register(string userId, string? userName, string resourceId, string? resourceName,
        string? host, int port, string? clientIp)
    {
        var session = new ActiveSession
        {
            SessionId = Guid.NewGuid().ToString(),
            UserId = userId,
            UserName = userName,
            ResourceId = resourceId,
            ResourceName = resourceName,
            Host = host,
            Port = port,
            ClientIp = clientIp,
        };
        _sessions[session.SessionId] = session;
        return session;
    }

    /// <summary>Removes a session from the registry (on disconnect) and disposes its cancellation source.</summary>
    public void Remove(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var s))
            s.Cancellation.Dispose();
    }

    /// <summary>Snapshot of all live sessions, newest first.</summary>
    public IReadOnlyList<ActiveSession> All() =>
        _sessions.Values.OrderByDescending(s => s.StartedUtc).ToList();

    /// <summary>Number of live sessions for a user — used to enforce concurrent-session limits.</summary>
    public int CountForUser(string userId) =>
        _sessions.Values.Count(s => s.UserId == userId);

    /// <summary>
    /// Force-disconnects a session by id: trips its cancellation source (aborting the relay loop) and
    /// returns the session record for auditing, or null if it was not found.
    /// </summary>
    public ActiveSession? ForceDisconnect(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        try { s.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return s;
    }

    /// <summary>True if at least one tunnel is currently open to the resource.</summary>
    public bool HasActiveSessions(string resourceId) =>
        _sessions.Values.Any(s => s.ResourceId == resourceId);
}
