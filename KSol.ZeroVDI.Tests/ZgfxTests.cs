using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// ZGFX decompresses everything on the Graphics channel, driven entirely by values off the wire. The
/// recorder feeds it host output, so a hostile or corrupt stream reaches it directly.
/// </summary>
public class ZgfxTests
{
    /// <summary>Wraps a payload as a single uncompressed ZGFX segment (descriptor 0xE0, flags 0).</summary>
    private static byte[] SingleUncompressed(byte[] payload)
    {
        var buffer = new byte[2 + payload.Length];
        buffer[0] = 0xE0;   // ZGFX_SEGMENTED_SINGLE
        buffer[1] = 0x00;   // segment flags: not compressed
        payload.CopyTo(buffer, 2);
        return buffer;
    }

    [Fact]
    public void SingleUncompressedSegment_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        var result = new Zgfx().Decompress(SingleUncompressed(payload));

        Assert.NotNull(result);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void MultipartClaimingMoreThanItsSegmentsCanHold_IsRejected()
    {
        // uncompressedSize is a 32-bit field straight off the wire. One segment can produce at most
        // 65,536 bytes ([MS-RDPEGFX] 2.2.5.1), so a single-segment message claiming 4 GB is a hostile
        // (or corrupt) allocation request and must not be honoured.
        var message = new byte[] { 0xE1, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };

        Assert.Null(new Zgfx().Decompress(message));
    }

    [Fact]
    public void MultipartWithZeroSegments_IsRejected()
    {
        var message = new byte[] { 0xE1, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00 };

        Assert.Null(new Zgfx().Decompress(message));
    }

    [Fact]
    public void TruncatedAndUnknownMessages_ReturnNullRatherThanThrowing()
    {
        var zgfx = new Zgfx();

        Assert.Null(zgfx.Decompress(Array.Empty<byte>()));
        Assert.Null(zgfx.Decompress(new byte[] { 0x00 }));          // unknown descriptor
        Assert.Null(zgfx.Decompress(new byte[] { 0xE1, 0x01 }));    // multipart header cut short
        Assert.Null(zgfx.Decompress(new byte[] { 0xE0 }));          // single with no segment
    }
}
