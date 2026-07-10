using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// An <see cref="IHostTransport"/> that reaches a VM's SPICE server through Proxmox's <c>spiceproxy</c>
/// daemon. Each channel the SPICE client opens is tunnelled identically: dial the spiceproxy HTTP CONNECT
/// endpoint, <c>CONNECT &lt;routing-host&gt;:&lt;tls-port&gt;</c> (the opaque routing token from the
/// <c>spiceproxy</c> API), then start TLS — the SPICE bytes flow inside that TLS tunnel. The host/port the
/// SPICE client passes are placeholders (the bridge always routes via the proxy), so they are ignored.
///
/// The single-use ticket password lives in the <see cref="SpiceConnection"/> and is injected as the SPICE
/// auth password by the resolver; this transport only builds the encrypted tunnel each channel rides.
/// </summary>
public sealed class SpiceProxyTransport : IHostTransport
{
    private readonly IHostTransport _inner;   // opens the raw socket to the proxy (direct or via connector)
    private readonly Func<CancellationToken, Task<SpiceConnection?>> _ticketFactory;
    private readonly bool _verifyTls;
    private readonly ILogger _logger;

    // The SPICE ticket password of the MOST RECENT successful connect. SpiceClient reads this after the
    // first (main) channel connects so every channel authenticates with the ticket that matches ITS tunnel.
    public string? LastTicketPassword { get; private set; }

    /// <summary>
    /// <paramref name="ticketFactory"/> fetches a FRESH spiceproxy ticket (routing host + single-use
    /// password) per call — Proxmox's spiceproxy ticket is one-shot/short-lived, so each SPICE channel
    /// (main/display/inputs) needs its own, fetched right before its CONNECT.
    /// </summary>
    public SpiceProxyTransport(IHostTransport inner, Func<CancellationToken, Task<SpiceConnection?>> ticketFactory,
        bool verifyTls, ILogger logger)
    {
        _inner = inner; _ticketFactory = ticketFactory; _verifyTls = verifyTls; _logger = logger;
    }

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        // Fetch a fresh ticket for THIS channel's tunnel (host/port args are ignored — always via proxy).
        var spice = await _ticketFactory(ct)
            ?? throw new RdpHostConnection.ConnectException("SPICE: could not obtain a spiceproxy ticket");
        LastTicketPassword = spice.Password;

        _logger.LogInformation("SPICE proxy: dialing {Host}:{Port}", spice.ProxyHost, spice.ProxyPort);
        var raw = await _inner.ConnectAsync(spice.ProxyHost, spice.ProxyPort, ct);
        try
        {
            await ProxyConnectAsync(raw, spice, ct);
            var ssl = new SslStream(raw, leaveInnerStreamOpen: false, ValidateServerCert);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = CnFromSubject(spice.HostSubject) ?? spice.RoutingHost,
            };
            await ssl.AuthenticateAsClientAsync(options, ct);
            _logger.LogInformation("SPICE proxy: TLS tunnel to {Route} established", spice.RoutingHost);
            return ssl;
        }
        catch
        {
            raw.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Performs the HTTP <c>CONNECT</c> handshake spiceproxy expects. The CONNECT target must be the FULL
    /// routing token with the tls-port appended (<c>&lt;host&gt;:&lt;tls-port&gt;</c>); omitting the
    /// trailing port makes PVE::Ticket's parse/verify fail with "401 invalid ticket".
    /// </summary>
    private async Task ProxyConnectAsync(Stream stream, SpiceConnection spice, CancellationToken ct)
    {
        var target = $"{spice.RoutingHost}:{spice.TlsPort}";
        var request = $"CONNECT {target} HTTP/1.0\r\nHost: {target}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        await stream.FlushAsync(ct);

        // Read the response headers byte-by-byte to the CRLFCRLF terminator, so we don't over-read into
        // the TLS bytes that follow on the same stream.
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int n = await stream.ReadAsync(one, ct);
            if (n == 0) throw new IOException("spiceproxy closed during CONNECT");
            sb.Append((char)one[0]);
            if (sb.Length > 8192) throw new IOException("spiceproxy CONNECT response too large");
        }

        var statusLine = sb.ToString().Split("\r\n", 2)[0];
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var code) || code is < 200 or >= 300)
            throw new IOException($"spiceproxy CONNECT failed: {statusLine}");
    }

    private bool ValidateServerCert(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
        => !_verifyTls || errors == SslPolicyErrors.None;

    private static string? CnFromSubject(string? subject)
    {
        if (string.IsNullOrEmpty(subject)) return null;
        foreach (var part in subject.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                var cn = p[3..].Trim();
                return string.IsNullOrEmpty(cn) ? null : cn;
            }
        }
        return null;
    }

    // The SPICE bridge doesn't sample gateway→host RTT the way the RDP relay does; defer to the inner
    // transport against whatever host it was asked to probe.
    public Task<TimeSpan?> ProbeRttAsync(string host, int port, CancellationToken ct)
        => _inner.ProbeRttAsync(host, port, ct);
}
