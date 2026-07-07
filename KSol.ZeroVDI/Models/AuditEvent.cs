namespace KSol.ZeroVDI.Models;

/// <summary>
/// Broad classification of an audit event, used for filtering in the admin viewer.
/// </summary>
public enum AuditCategory
{
    Authentication, // login success/failure, lockout, 2FA, logout
    Session,        // RDP console connect / disconnect
    Credential,     // stored SSO credential set / cleared
    Resource,       // resource create / edit / delete
    Authorization,  // (user,resource) grant / revoke, role changes
    User,           // user account create / edit / delete
    Backend,        // Proxmox backend create / edit / delete
    Recording,      // recording viewed / deleted / retention purge
    Admin,          // other administrative actions
}

/// <summary>
/// A single immutable audit-trail entry: who did what to which target, when, and from where. Written
/// append-only by <see cref="RDP.AuditLogger"/>; there is no update/delete path in the application, so
/// the table is a tamper-evident-by-convention compliance record (SOC2 / ISO 27001). Sensitive values
/// (passwords, secrets) must never be placed in <see cref="Detail"/>.
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }

    /// <summary>When the event occurred (UTC).</summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public AuditCategory Category { get; set; }

    /// <summary>Short verb describing the action, e.g. "LoginSucceeded", "SessionConnected".</summary>
    public string Action { get; set; } = "";

    /// <summary>Identity user id of the actor, or null for anonymous / pre-auth events.</summary>
    public string? ActorUserId { get; set; }

    /// <summary>Display name of the actor (email/username) captured at event time so it survives user deletion.</summary>
    public string? ActorName { get; set; }

    /// <summary>Type of the object acted upon, e.g. "RDPResource", "User". Null for actor-only events.</summary>
    public string? TargetType { get; set; }

    /// <summary>Identifier of the target object.</summary>
    public string? TargetId { get; set; }

    /// <summary>Human-readable name of the target captured at event time.</summary>
    public string? TargetName { get; set; }

    /// <summary>Client IP the action originated from (honors forwarded headers).</summary>
    public string? IpAddress { get; set; }

    /// <summary>Whether the action succeeded. Failed auth attempts are recorded with false.</summary>
    public bool Success { get; set; } = true;

    /// <summary>Optional JSON blob of additional, non-sensitive context.</summary>
    public string? Detail { get; set; }
}
