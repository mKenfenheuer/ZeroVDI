using System.Text;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;
using Microsoft.Extensions.Logging.Abstractions;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// The guard is the only enforcement point a modified browser client cannot route around: the console
/// UI and the save endpoint both clamp preferences, but only the relay sees what the client actually
/// asks the host for. It must refuse a blocked channel — and must never break an ordinary session by
/// mis-parsing the stream.
/// </summary>
public class ChannelPolicyGuardTests
{
    private static ChannelPolicyGuard? Guard(DevicePolicyMode clipboard = DevicePolicyMode.UserControlled,
        DevicePolicyMode audio = DevicePolicyMode.UserControlled)
        => ChannelPolicyGuard.ForPolicy(
            new DevicePolicy { Clipboard = clipboard, Audio = audio },
            NullLogger.Instance);

    [Fact]
    public void NothingDisabled_BuildsNoGuardAtAll()
    {
        // Inspection costs CPU on the relay's hot path; it must not run when the policy allows everything.
        Assert.Null(Guard());
        Assert.Null(ChannelPolicyGuard.ForPolicy(
            new DevicePolicy { Clipboard = DevicePolicyMode.Forced }, NullLogger.Instance));
    }

    [Fact]
    public void DisablingAFeature_BuildsAGuardThatNamesItsChannels()
    {
        var guard = Guard(clipboard: DevicePolicyMode.Disabled);

        Assert.NotNull(guard);
        Assert.Contains("cliprdr", guard!.BlockedChannels);
        Assert.DoesNotContain("rdpsnd", guard.BlockedChannels);
    }

    [Fact]
    public void DisablingAudio_BlocksBothTheStaticChannelAndItsDynamicVariants()
    {
        var guard = Guard(audio: DevicePolicyMode.Disabled);

        Assert.NotNull(guard);
        Assert.Contains("rdpsnd", guard!.BlockedChannels);
        Assert.Contains("AUDIO_PLAYBACK_DVC", guard.BlockedChannels);
        Assert.Contains("AUDIO_PLAYBACK_LOSSY_DVC", guard.BlockedChannels);
    }

    [Fact]
    public void ClientRequestingABlockedStaticChannel_IsRefused()
    {
        var guard = Guard(clipboard: DevicePolicyMode.Disabled)!;

        var violation = guard.Feed(RdpDir.ClientToServer, ConnectInitialWithChannels("cliprdr", "rdpdr"));

        Assert.NotNull(violation);
        Assert.Equal("Clipboard", violation!.Feature);
        Assert.Equal("cliprdr", violation.Channel);
        Assert.Contains("disabled", violation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientRequestingOnlyAllowedChannels_PassesThrough()
    {
        var guard = Guard(clipboard: DevicePolicyMode.Disabled)!;

        Assert.Null(guard.Feed(RdpDir.ClientToServer, ConnectInitialWithChannels("rdpdr", "drdynvc")));
    }

    [Fact]
    public void UnparseableTraffic_FailsOpenInsteadOfKillingTheSession()
    {
        // A parse failure must never take a working desktop down: the guard gives up on the stream
        // rather than guessing.
        var guard = Guard(clipboard: DevicePolicyMode.Disabled)!;

        var junk = new byte[4096];
        Random.Shared.NextBytes(junk);

        Assert.Null(guard.Feed(RdpDir.ClientToServer, junk));
    }

    /// <summary>
    /// Minimal MCS Connect Initial carrying a CS_NET (0xC003) block with the given channel names,
    /// wrapped in TPKT + X.224 Data, which is how it reaches the relay.
    /// </summary>
    private static byte[] ConnectInitialWithChannels(params string[] names)
    {
        var csNet = new List<byte>();
        csNet.AddRange(BitConverter.GetBytes((ushort)0xC003));              // CS_NET
        var lengthAt = csNet.Count;
        csNet.AddRange(BitConverter.GetBytes((ushort)0));                   // block length, patched below
        csNet.AddRange(BitConverter.GetBytes((uint)names.Length));          // channelCount
        foreach (var name in names)
        {
            var raw = Encoding.ASCII.GetBytes(name.PadRight(8, '\0'));
            csNet.AddRange(raw.AsSpan(0, 8).ToArray());                     // name[8]
            csNet.AddRange(BitConverter.GetBytes(0u));                      // options
        }
        var csNetBytes = csNet.ToArray();
        BinaryPrimitives_WriteUInt16(csNetBytes, lengthAt, (ushort)csNetBytes.Length);

        // The guard locates the client's user data by the "Duca" H.221 key and reads a PER length
        // immediately after it; everything before that (the BER/PER Connect Initial envelope) is skipped.
        var mcs = new List<byte> { 0x7F, 0x65 };                            // Connect Initial tag
        mcs.AddRange(Encoding.ASCII.GetBytes("Duca"));
        mcs.AddRange(new byte[] { (byte)(0x80 | (csNetBytes.Length >> 8)), (byte)(csNetBytes.Length & 0xFF) });
        mcs.AddRange(csNetBytes);

        var pdu = new List<byte> { 0x03, 0x00, 0x00, 0x00, 0x02, 0xF0, 0x80 };
        pdu.AddRange(mcs);
        pdu[2] = (byte)(pdu.Count >> 8);
        pdu[3] = (byte)(pdu.Count & 0xFF);
        return pdu.ToArray();
    }

    private static void BinaryPrimitives_WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }
}
