using System.Net;
using System.Net.Sockets;

namespace KSol.RDPGateway.RDP;

public static class WakeOnLan
{
    public static async Task SendMagicPacketAsync(string macAddress)
    {
        var mac = macAddress.Replace(":", "").Replace("-", "");
        var macBytes = Enumerable.Range(0, 6)
            .Select(i => Convert.ToByte(mac.Substring(i * 2, 2), 16))
            .ToArray();

        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
    }
}
