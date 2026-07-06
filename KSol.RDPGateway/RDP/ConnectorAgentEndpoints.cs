using System.Net.WebSockets;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// The gateway's machine-facing surface for connector agents (the first non-cookie endpoints in the app).
/// All under <c>/agent/*</c>, authenticated by connector tokens rather than the Identity cookie:
///  - <c>POST /agent/register</c> — redeem a one-time registration token for a long-lived auth token.
///  - <c>GET  /agent/control</c>  — the agent's outbound control WebSocket (bearer auth).
///  - <c>GET  /agent/data/{chanId}</c> — a per-request data WebSocket the agent opens for an open-tcp.
/// </summary>
public static class ConnectorAgentEndpoints
{
    public sealed record RegisterRequest(string RegistrationToken);
    public sealed record RegisterResponse(string ConnectorId, string AuthToken);

    public static void MapConnectorAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/agent/register", async (RegisterRequest body, ApplicationDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(body.RegistrationToken))
                return Results.BadRequest(new { error = "missing registration token" });

            // Registration tokens are encrypted at rest (value converter), so we can't index/query by them;
            // scan the not-yet-enrolled connectors and match the decrypted plaintext. There are few.
            var candidates = await db.Connectors
                .Where(c => c.RegistrationTokenUsedUtc == null && c.Enabled)
                .ToListAsync();
            var connector = candidates.FirstOrDefault(c =>
                string.Equals(c.RegistrationToken, body.RegistrationToken, StringComparison.Ordinal));
            if (connector == null)
                return Results.Json(new { error = "invalid or already-used registration token" }, statusCode: 401);

            var authToken = ConnectorTokens.NewToken();
            connector.AuthTokenHash = ConnectorTokens.Hash(authToken);
            connector.RegistrationTokenUsedUtc = DateTime.UtcNow;
            connector.RegistrationToken = null; // single-use; drop the plaintext secret
            await db.SaveChangesAsync();

            return Results.Ok(new RegisterResponse(connector.Id, authToken));
        });

        app.MapGet("/agent/control", async (HttpContext ctx, ConnectorHub hub, ApplicationDbContext db,
            IServiceScopeFactory scopes) =>
        {
            var connector = await AuthenticateAsync(ctx, db);
            if (connector == null) { ctx.Response.StatusCode = 401; return; }
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

            connector.LastSeenUtc = DateTime.UtcNow;
            connector.LastRemoteAddress = ctx.Connection.RemoteIpAddress?.ToString();
            await db.SaveChangesAsync();

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.RunControlChannelAsync(connector.Id, socket, ctx.RequestAborted);

            // Stamp offline time on a fresh scope (the request DbContext may be disposed by now).
            using var scope = scopes.CreateScope();
            var db2 = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db2.Connectors.FindAsync(connector.Id);
            if (row != null) { row.LastSeenUtc = DateTime.UtcNow; await db2.SaveChangesAsync(); }
        });

        app.MapGet("/agent/data/{chanId}", async (string chanId, HttpContext ctx, ConnectorHub hub,
            ApplicationDbContext db) =>
        {
            var connector = await AuthenticateAsync(ctx, db);
            if (connector == null) { ctx.Response.StatusCode = 401; return; }
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

            var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var completion = hub.TryAttachDataChannel(connector.Id, chanId, socket);
            if (completion == null)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "no pending channel", ctx.RequestAborted);
                return;
            }
            // The hub now owns the socket (wrapped in a WebSocketStream). Keep the request alive until that
            // stream is disposed so ASP.NET does not tear the connection down underneath the relay.
            try { await completion.WaitAsync(ctx.RequestAborted); }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>
    /// Resolves the connector from the <c>Authorization: Bearer</c> auth token, falling back to a
    /// <c>?token=</c> query parameter. The query fallback matters for the WebSocket endpoints: some reverse
    /// proxies strip the <c>Authorization</c> header on the HTTP upgrade, so agents pass the token in the
    /// URL for the control/data channels. Returns null when no valid token is present.
    /// </summary>
    private static async Task<Connector?> AuthenticateAsync(HttpContext ctx, ApplicationDbContext db)
    {
        string? token = null;
        var header = ctx.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            token = header[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
            token = ctx.Request.Query["token"].ToString();
        if (string.IsNullOrEmpty(token)) return null;

        var hash = ConnectorTokens.Hash(token);
        var connector = await db.Connectors.FirstOrDefaultAsync(c => c.AuthTokenHash == hash && c.Enabled);
        // Hash match is exact; the constant-time Verify guards the plaintext path but the indexed lookup is
        // on the already-hashed value so equality here is safe.
        return connector;
    }
}
