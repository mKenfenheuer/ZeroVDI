using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RDPGW.AspNetCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Connection handler for the rdpgw.net native RDGW path. Instead of letting the library open a plain
/// TCP socket to the host (relaying the client's end-to-end NLA opaquely), this returns a
/// <see cref="MitmRdpStream"/> that terminates the client's TLS+NLA at the gateway and re-originates it
/// toward the host — bringing the native path to parity with the in-browser console:
/// <list type="bullet">
/// <item><b>SSO</b>: the client re-presents its <i>gateway-login</i> credentials (which it already
///       holds) for the inner NLA; the gateway verifies them against the user's stored NT hash and then
///       authenticates to the host with the <i>stored host credentials</i> — the host password never
///       reaches the client.</item>
/// <item><b>Recording</b>: because the gateway now sees the decrypted RDP stream, the recording policy
///       and <see cref="SessionRecorder"/> apply exactly as for the web console.</item>
/// </list>
/// When the gateway has no NT hash for the user (so it cannot terminate the client's NLA), it falls back
/// to a plain pass-through TCP stream — preserving the previous opaque-relay behaviour (no SSO, no
/// recording) rather than failing the connection.
/// </summary>
public sealed class GatewayConnectionHandler : IRDPGWConnectionHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxmoxBackendProvider _backends;
    private readonly CredentialProtector _credentials;
    private readonly IConfiguration _config;
    private readonly ILogger<GatewayConnectionHandler> _logger;
    // The gateway certificate presented to clients during the NLA MITM TLS handshake. Built once
    // (CredSSP pins its public key, so it must be stable for the process lifetime).
    private readonly Lazy<X509Certificate2> _serverCert;

    public GatewayConnectionHandler(
        IServiceScopeFactory scopeFactory,
        ProxmoxBackendProvider backends,
        CredentialProtector credentials,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<GatewayConnectionHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _backends = backends;
        _credentials = credentials;
        _config = config;
        _logger = logger;
        _serverCert = new Lazy<X509Certificate2>(() => CertificateProvider.GetRdpServerCertificate(config, env));
    }

    public async Task<Stream?> ConnectAsync(RDPGWConnectionContext context, CancellationToken ct)
    {
        // Without an authenticated user we cannot resolve stored creds, the NT hash, or recording
        // policy — relay opaquely as before.
        if (context.UserId == null)
            return await PassthroughAsync(context, ct);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.FindByIdAsync(context.UserId);
        // The client re-presents its gateway-login credentials for the inner NLA; we verify them against
        // this NT hash. No hash (e.g. password never set under the deriving hasher) => cannot MITM.
        if (user?.NtHash == null)
        {
            _logger.LogInformation("RDGW native: no NT hash for user {User}; relaying without MITM", context.UserId);
            return await PassthroughAsync(context, ct);
        }
        var clientExpectedNtHash = Convert.FromHexString(user.NtHash);

        var authorization = await db.RDPResourceUserAuthorizations
            .Include(a => a.RDPResource)
            .FirstOrDefaultAsync(a => a.UserId == context.UserId && a.RDPResourceId == context.Resource, ct);

        // SSO: stored host credentials for this (user, resource), if any.
        RdpRelaySession.VmCredentials? storedCreds = null;
        if (authorization?.HasStoredCredentials == true)
        {
            var u = _credentials.Unprotect(authorization.ProtectedUsername);
            var p = _credentials.Unprotect(authorization.ProtectedPassword);
            if (!string.IsNullOrEmpty(u) && p != null)
                storedCreds = new RdpRelaySession.VmCredentials(u, p,
                    _credentials.Unprotect(authorization.ProtectedDomain));
        }

        // Recording: evaluate the policy for this (user, resource, roles). RecordingPolicy is scoped,
        // so resolve it from this connection scope rather than capturing it in the singleton handler.
        bool record = false;
        try
        {
            var recordingPolicy = scope.ServiceProvider.GetRequiredService<RecordingPolicy>();
            var roles = await userManager.GetRolesAsync(user);
            record = (await recordingPolicy.EvaluateAsync(context.UserId, context.Resource, roles)).Record;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RDGW native: recording policy evaluation failed"); }

        // If we have neither a reason to swap credentials (SSO) nor to record, there is no benefit to
        // terminating the client's NLA — relay opaquely and let the client authenticate end-to-end.
        if (storedCreds == null && !record)
            return await PassthroughAsync(context, ct);

        var kerberos = await ResolveKerberosAsync(authorization?.RDPResource);

        // Recording sink + Recording row, finalized when the MITM stream completes.
        RecordingSession? recording = null;
        if (record)
            recording = await RecordingSession.StartAsync(_scopeFactory, _config, context, _logger);

        // Host credentials: stored creds for SSO; otherwise fall back to whatever the client delegates
        // (captured by the CredSSP server) so recording works even without stored SSO credentials.
        var mitm = new MitmRdpStream(
            context.Host, context.Port, kerberos,
            // When no stored creds, MitmRdpStream uses the client-delegated creds it captured.
            storedCreds, clientExpectedNtHash, _serverCert.Value,
            recording?.Recorder, _logger,
            onCompleted: () => recording?.FinalizeAsync());

        _logger.LogInformation("RDGW native: MITM for {User} -> {Host}:{Port} (sso={Sso}, record={Record})",
            context.UserId, context.Host, context.Port, storedCreds != null, record);
        return mitm;
    }

    /// <summary>Plain TCP pass-through: the previous opaque-relay behaviour (no SSO, no recording).</summary>
    private async Task<Stream?> PassthroughAsync(RDPGWConnectionContext context, CancellationToken ct)
    {
        try
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(context.Host, context.Port, ct);
            return new TcpForwardStream(tcp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RDGW native: pass-through connect to {Host}:{Port} failed", context.Host, context.Port);
            return null;
        }
    }

    private async Task<KerberosAuth?> ResolveKerberosAsync(RDPResource? resource)
    {
        if (resource == null || resource.Source != ResourceSource.Proxmox || resource.ProxmoxBackendId == null)
            return null;
        var backend = await _backends.GetAsync(resource.ProxmoxBackendId.Value);
        if (backend == null || string.IsNullOrWhiteSpace(backend.KerberosRealm)
            || string.IsNullOrWhiteSpace(backend.KdcHost))
            return null;
        return new KerberosAuth(backend.KerberosRealm!, backend.KdcHost!, _logger);
    }
}

/// <summary>A <see cref="Stream"/> over a <see cref="TcpClient"/> that disposes the client on dispose.</summary>
internal sealed class TcpForwardStream : Stream
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _net;
    public TcpForwardStream(TcpClient tcp) { _tcp = tcp; _net = tcp.GetStream(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _net.ReadAsync(buffer, ct);
    public override int Read(byte[] buffer, int offset, int count) => _net.Read(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _net.WriteAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) => _net.Write(buffer, offset, count);
    public override Task FlushAsync(CancellationToken ct) => _net.FlushAsync(ct);
    public override void Flush() => _net.Flush();
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _net.Dispose(); } catch { } try { _tcp.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
