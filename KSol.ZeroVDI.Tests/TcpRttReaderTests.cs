using System.Net;
using System.Net.Sockets;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// The relay reads the gateway→host RTT from the kernel instead of opening probe connections. The
/// TCP_INFO / SIO_TCP_INFO layouts are read by byte offset, so check them against a real socket.
/// </summary>
public class TcpRttReaderTests
{
    [Fact]
    public async Task ReadsAPlausibleRttFromAConnectedLoopbackSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();

        // Exchange some data so the kernel has RTT samples.
        var buf = new byte[1024];
        for (int i = 0; i < 20; i++)
        {
            await client.GetStream().WriteAsync(buf);
            await server.GetStream().ReadExactlyAsync(buf);
            await server.GetStream().WriteAsync(buf);
            await client.GetStream().ReadExactlyAsync(buf);
        }

        var rtt = TcpRttReader.Read(client.Client);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            Assert.Null(rtt);
            return;
        }
        // Loopback: present (or null on a kernel that doesn't report it), and never a garbage value read
        // from the wrong offset.
        if (rtt is { } r)
        {
            Assert.True(r > TimeSpan.Zero, $"rtt {r}");
            Assert.True(r < TimeSpan.FromMilliseconds(200), $"loopback rtt {r} is implausible — wrong struct offset?");
        }
    }

    [Fact]
    public void UnconnectedOrMissingSocketYieldsNull()
    {
        Assert.Null(TcpRttReader.Read(null));
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.Null(TcpRttReader.Read(s));
    }
}
