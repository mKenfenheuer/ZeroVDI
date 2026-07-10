namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// The capability sets the VNC bridge's Demand Active advertises. The browser client
/// (<c>_onDemandActiveControl</c>) does not strictly validate individual server capsets — it reads the
/// shareId and replies — so a conservative, well-formed set is enough for M1's legacy fastpath bitmap
/// path. Bodies mirror the layouts in <c>protocol.js</c> (client capsets), sized to what the client's
/// renderer needs: FASTPATH_OUTPUT enabled, 16bpp bitmaps, no drawing orders (we only send bitmap
/// updates).
/// </summary>
internal static class RdpServerCapsets
{
    /// <summary>Number of capsets <see cref="ServerDemand"/> emits (must match the Demand Active header).</summary>
    public const int ServerCapCount = 4;

    private static byte[] CapSet(int type, ReadOnlySpan<byte> data)
    {
        var w = new W();
        w.U16le(type);
        w.U16le(4 + data.Length);
        w.Bytes(data);
        return w.ToArray();
    }

    private static byte[] General()
    {
        var d = new W();
        d.U16le(0x0001); // osMajorType WINDOWS
        d.U16le(0x0003); // osMinorType
        d.U16le(0x0200); // protocolVersion
        d.U16le(0x0000); // padding
        d.U16le(0x0000); // generalCompressionTypes
        // extraFlags: FASTPATH_OUTPUT(0x0001) | NO_BITMAP_COMPRESSION_HDR(0x0400) | LONG_CREDENTIALS(0x0004)
        d.U16le(0x0001 | 0x0004 | 0x0400);
        d.U16le(0x0000); // updateCapabilityFlag
        d.U16le(0x0000); // remoteUnshareFlag
        d.U16le(0x0000); // generalCompressionLevel
        d.U8(0x00);      // refreshRectSupport
        d.U8(0x00);      // suppressOutputSupport
        return CapSet(0x0001, d.ToArray());
    }

    private static byte[] Bitmap(int width, int height)
    {
        var d = new W();
        d.U16le(0x0010); // preferredBitsPerPixel = HIGH_COLOR_16BPP
        d.U16le(0x0001); d.U16le(0x0001); d.U16le(0x0001); // receive1/4/8BitsPerPixel
        d.U16le(width); d.U16le(height);
        d.U16le(0x0000); // pad
        d.U16le(0x0001); // desktopResizeFlag
        d.U16le(0x0001); // bitmapCompressionFlag
        d.U8(0x00);      // highColorFlags
        d.U8(0x00);      // drawingFlags
        d.U16le(0x0001); // multipleRectangleSupport
        d.U16le(0x0000); // pad
        return CapSet(0x0002, d.ToArray());
    }

    private static byte[] Order()
    {
        // ORDER capset with all drawing orders DISABLED — we only send bitmap updates, no primary/
        // secondary orders. orderSupport[32] left zero.
        var d = new W();
        d.Zeros(16);     // terminalDescriptor
        d.U32le(0);      // pad
        d.U16le(1);      // desktopSaveXGranularity
        d.U16le(20);     // desktopSaveYGranularity
        d.U16le(0);      // pad
        d.U16le(1);      // maximumOrderLevel
        d.U16le(0);      // numberFonts
        d.U16le(0x0002); // orderFlags = NEGOTIATEORDERSUPPORT
        d.Zeros(32);     // orderSupport[32] (all zero = no orders)
        d.U16le(0);      // textFlags
        d.U16le(0);      // orderSupportExFlags
        d.U32le(0);      // pad
        d.U32le(230400); // desktopSaveSize (480*480)
        d.U16le(0);      // pad
        d.U16le(0);      // pad
        d.U16le(0);      // textANSICodePage
        d.U16le(0);      // pad
        return CapSet(0x0003, d.ToArray());
    }

    private static byte[] Pointer()
    {
        var d = new W();
        d.U16le(1);   // colorPointerFlag
        d.U16le(20);  // colorPointerCacheSize
        d.U16le(20);  // pointerCacheSize
        return CapSet(0x0008, d.ToArray());
    }

    /// <summary>General + Bitmap + Order + Pointer capsets concatenated, for the Demand Active PDU.</summary>
    public static byte[] ServerDemand(int width, int height)
    {
        var w = new W();
        w.Bytes(General());
        w.Bytes(Bitmap(width, height));
        w.Bytes(Order());
        w.Bytes(Pointer());
        return w.ToArray();
    }
}
