using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// Every reachability and RTT probe against an RDP port goes through <see cref="RdpPortProbe"/>. GNOME
/// Remote Desktop 50.0 (Ubuntu 26.04) leaks a throttler slot for each connection that closes before
/// sending anything, and after five it refuses the gateway's address until the daemon restarts. These
/// tests pin that the probe always speaks first, and that "port open" still means what it meant.
/// </summary>
public class RdpPortProbeTests
{
    /// <summary>
    /// If the probe hangs up without sending a well-formed Connection Request, grd 50.0 hosts lock the
    /// gateway out after five status sweeps — every connect to them then fails with a reset.
    /// </summary>
    [Fact]
    public async Task Probe_SendsX224ConnectionRequest_BeforeHangingUp()
    {
        using var listener = StartListener(out var port);
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var stream = peer.GetStream();
            var request = new byte[19];
            var read = 0;
            while (read < request.Length)
            {
                var n = await stream.ReadAsync(request.AsMemory(read));
                if (n == 0) break;
                read += n;
            }
            // The Connection Confirm grd sends, selecting HYBRID.
            await stream.WriteAsync(Convert.FromHexString("030000130ED00000000000020B080002000000"));
            return request[..read];
        });

        var rtt = await RdpPortProbe.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));
        var sent = await server;

        Assert.NotNull(rtt);
        Assert.Equal(19, sent.Length);
        Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x13 }, sent[..4]); // TPKT v3, 19 bytes
        Assert.Equal(0x0E, sent[4]);                                    // X.224 length indicator
        Assert.Equal(0xE0, sent[5]);                                    // Connection Request
        Assert.Equal(0x01, sent[11]);                                   // TYPE_RDP_NEG_REQ
        Assert.Equal(3u, BitConverter.ToUInt32(sent, 15));              // PROTOCOL_SSL | PROTOCOL_HYBRID
    }

    /// <summary>
    /// A host that accepts but never answers (a stalled RDP stack, a non-RDP listener) must still read
    /// as reachable, and the probe must give up at its timeout — the status sweep and the connect path
    /// wait on it.
    /// </summary>
    [Fact]
    public async Task Probe_HostThatNeverAnswers_ReportsOpen_WithinTimeout()
    {
        using var listener = StartListener(out var port);
        var accept = listener.AcceptTcpClientAsync();

        var sw = Stopwatch.StartNew();
        var rtt = await RdpPortProbe.ProbeAsync("127.0.0.1", port, TimeSpan.FromMilliseconds(500));
        sw.Stop();

        Assert.NotNull(rtt);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"probe took {sw.Elapsed}");
        (await accept).Dispose();
    }

    /// <summary>A closed port must read as unreachable, or a stopped VM would show as Running.</summary>
    [Fact]
    public async Task Probe_ClosedPort_ReturnsNull()
    {
        int port;
        using (var listener = StartListener(out port)) listener.Stop();

        Assert.Null(await RdpPortProbe.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(2)));
    }

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }
}
