using System.Text.Json;
using Microsoft.AspNetCore.Http;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Writes <see cref="AuditEvent"/> records. Auditing must never break the request it is recording, so
/// every write is wrapped in a try/catch that downgrades failures to a log warning. The logger is
/// registered scoped and resolves its own short-lived DbContext from the request scope.
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// Records an audit event. <paramref name="detail"/> is serialized to JSON; never pass secrets.
    /// The actor and client IP are resolved from the current <see cref="HttpContext"/> when available.
    /// </summary>
    Task LogAsync(AuditCategory category, string action, bool success = true,
        string? targetType = null, string? targetId = null, string? targetName = null,
        object? detail = null,
        // Overrides for pre-auth events (e.g. failed login) where there is no authenticated principal.
        string? actorUserId = null, string? actorName = null);
}

public sealed class AuditLogger : IAuditLogger
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(ApplicationDbContext db, IHttpContextAccessor http, ILogger<AuditLogger> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public async Task LogAsync(AuditCategory category, string action, bool success = true,
        string? targetType = null, string? targetId = null, string? targetName = null,
        object? detail = null, string? actorUserId = null, string? actorName = null)
    {
        try
        {
            var ctx = _http.HttpContext;
            var user = ctx?.User;

            actorUserId ??= user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            actorName ??= user?.Identity?.IsAuthenticated == true ? user.Identity!.Name : null;

            var ev = new AuditEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Category = category,
                Action = action,
                Success = success,
                ActorUserId = actorUserId,
                ActorName = actorName,
                TargetType = targetType,
                TargetId = targetId,
                TargetName = targetName,
                // RemoteIpAddress already honors UseForwardedHeaders (configured in Program).
                IpAddress = ctx?.Connection.RemoteIpAddress?.ToString(),
                Detail = detail == null ? null : JsonSerializer.Serialize(detail),
            };

            _db.AuditEvents.Add(ev);
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never let an audit-write failure surface to the user or abort the audited operation.
            _logger.LogWarning(ex, "Failed to write audit event {Category}/{Action}", category, action);
        }
    }
}
