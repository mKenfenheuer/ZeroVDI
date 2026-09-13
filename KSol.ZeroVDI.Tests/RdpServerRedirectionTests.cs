using System.Buffers.Binary;
using System.Text;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// The redirect scanner runs over every byte the host sends. It used to match on the 0x0400 marker
/// alone, which a session streaming RemoteFX tiles eventually satisfies by chance — and a bogus
/// "routing token" tore down healthy sessions. These tests pin both halves: a real redirect parses,
/// and payload that merely contains the marker does not.
/// </summary>
public class RdpServerRedirectionTests
{
    private const uint LB_TARGET_NET_ADDRESS = 0x00000001;
    private const uint LB_LOAD_BALANCE_INFO = 0x00000002;

    /// <summary>Builds a TPKT + X.224 + MCS Send-Data-Indication carrying a redirection packet.</summary>
    private static byte[] RedirectPdu(uint redirFlags, params byte[][] fields)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)0x0400));   // Flags = SEC_REDIRECTION_PKT
        var lengthAt = body.Count;
        body.AddRange(BitConverter.GetBytes((ushort)0));        // Length, filled in below
        body.AddRange(BitConverter.GetBytes(1u));               // SessionID
        body.AddRange(BitConverter.GetBytes(redirFlags));
        foreach (var field in fields)
        {
            body.AddRange(BitConverter.GetBytes((uint)field.Length));
            body.AddRange(field);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(body).Slice(lengthAt, 2), (ushort)body.Count);

        // MCS Send-Data-Indication: initiator(2) channelId(2) flags(1) length(1, short form).
        var mcs = new List<byte> { 0x68, 0x00, 0x01, 0x03, 0xEB, 0x70, (byte)body.Count };
        mcs.AddRange(body);

        var pdu = new List<byte> { 0x03, 0x00, 0x00, 0x00, 0x02, 0xF0, 0x80 };
        pdu.AddRange(mcs);
        var total = pdu.Count;
        pdu[2] = (byte)(total >> 8);
        pdu[3] = (byte)(total & 0xFF);
        return pdu.ToArray();
    }

    [Fact]
    public void ParsesARoutingTokenAndTargetHost()
    {
        var target = Encoding.Unicode.GetBytes("rds03.example.com\0");
        var token = Encoding.ASCII.GetBytes("Cookie: msts=1234.5678.0000\r\n");

        var redirect = RdpServerRedirection.TryParse(
            RedirectPdu(LB_TARGET_NET_ADDRESS | LB_LOAD_BALANCE_INFO, target, token));

        Assert.NotNull(redirect);
        Assert.Equal("rds03.example.com", redirect!.TargetHost);
        Assert.Equal(token, redirect.LoadBalanceInfo);
    }

    [Fact]
    public void GraphicsPayloadContainingTheMarker_IsNotMistakenForARedirect()
    {
        // 0x0400 little-endian appears constantly in compressed tile data; without the TPKT/X.224/MCS
        // anchor this was enough to trigger a "redirect" and kill the session.
        var noise = new byte[4096];
        for (int i = 0; i + 1 < noise.Length; i += 2)
        {
            noise[i] = 0x00;
            noise[i + 1] = 0x04;
        }

        Assert.Null(RdpServerRedirection.TryParse(noise));
    }

    [Fact]
    public void ARedirectWithNeitherTokenNorTarget_IsNotActionable()
    {
        Assert.Null(RdpServerRedirection.TryParse(RedirectPdu(0x00000020 /* LB_DONTSTOREUSERNAME */)));
    }

    [Fact]
    public void FromToken_CarriesTheHostForwardThroughAHandover()
    {
        var redirect = RdpServerRedirection.FromToken(
            Encoding.ASCII.GetBytes("Cookie: msts=1\r\n"), "user", "dom", "pw", "rds03.example.com");

        Assert.Equal("rds03.example.com", redirect.TargetHost);
        Assert.Equal("user", redirect.Username);
    }
}
