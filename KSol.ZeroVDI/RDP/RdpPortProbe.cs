using System.Diagnostics;
using System.Net.Sockets;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// The one way the gateway (and the connector, which links this file) checks that an RDP port is up or
/// measures the round trip to it: connect, send an X.224 Connection Request, read the reply, hang up.
///
/// A bare connect-and-close is not safe. GNOME Remote Desktop peeks the first bytes of every connection
/// for a routing token before handing it to a session, and 50.0 — the version Ubuntu 26.04 ships — never
/// releases the throttler slot of a connection that closes before the peek sees data ("Failed to peek
/// routing token: Cancelled" in the host journal; upstream issue #332, fixed in 50.2). After five of
/// them from one address, every later connection from that address is queued and then refused, with
/// nothing logged, until the daemon restarts. The 15 s status sweep and the in-session RTT sampler used to
/// get the gateway locked out of such a host within about a minute. A connection that speaks first takes
/// grd's normal session path and gives its slot back when it closes.
/// </summary>
public static class RdpPortProbe
{
    /// <summary>
    /// TPKT + X.224 Connection Request carrying an RDP_NEG_REQ for SSL|HYBRID ([MS-RDPBCGR] 2.2.1.1) — the
    /// same protocols the real connect asks for, so the host answers the way it will for a session.
    /// </summary>
    internal static readonly byte[] ConnectionRequest =
    {
        0x03, 0x00, 0x00, 0x13,                         // TPKT v3, 19 bytes
        0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00,       // X.224 LI=14, CR, dst-ref, src-ref, class 0
        0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00, // RDP_NEG_REQ, flags 0, length 8, SSL|HYBRID
    };

    /// <summary>
    /// Returns the TCP connect time to host:port, or null when the port could not be reached within
    /// <paramref name="timeout"/>. Reachability still means "the connect succeeded": a host that accepts
    /// but never answers the request counts as open, exactly as it did before the request was added.
    /// </summary>
    public static async Task<TimeSpan?> ProbeAsync(string host, int port, TimeSpan timeout,
        CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var tcp = new TcpClient();
            var sw = Stopwatch.StartNew();
            await tcp.ConnectAsync(host, port, cts.Token);
            var rtt = sw.Elapsed;
            if (!tcp.Connected) return null;

            // Waiting for the Connection Confirm, rather than closing straight after the write, keeps
            // the close from racing grd's peek. What it says does not matter.
            try
            {
                var stream = tcp.GetStream();
                await stream.WriteAsync(ConnectionRequest, cts.Token);
                _ = await stream.ReadAsync(new byte[64], cts.Token);
            }
            catch { /* the port answered the connect; that is all the caller asked */ }
            return rtt;
        }
        catch { return null; }
    }
}
