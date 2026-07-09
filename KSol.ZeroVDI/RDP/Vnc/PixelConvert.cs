namespace KSol.ZeroVDI.RDP.Vnc;

/// <summary>
/// Pixel-format conversion for the VNC→RDP bitmap path. The browser client's uncompressed bitmap path is
/// hard-wired to <b>16bpp RGB565, little-endian, bottom-up</b> rows (see <c>color.js</c>), so everything
/// here targets that. VNC framebuffers arrive as 32bpp BGRX (we request that pixel format from the
/// host); M2 adds the BGRX→565 swizzle. M1 only needs a solid-color fill to validate the handshake.
/// </summary>
internal static class PixelConvert
{
    /// <summary>Packs 8-bit R,G,B into little-endian RGB565 (2 bytes).</summary>
    public static ushort Rgb565(byte r, byte g, byte b)
        => (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));

    /// <summary>
    /// Builds a solid-color <paramref name="width"/>×<paramref name="height"/> rectangle as bottom-up
    /// 16bpp RGB565 bytes, ready for <see cref="BitmapRect"/>. (Solid color is orientation-independent,
    /// but we keep the bottom-up contract explicit for when real pixels arrive.)
    /// </summary>
    public static BitmapRect SolidRect(int width, int height, byte r, byte g, byte b)
    {
        ushort pel = Rgb565(r, g, b);
        var data = new byte[width * height * 2];
        for (int i = 0; i < data.Length; i += 2)
        {
            data[i] = (byte)(pel & 0xff);
            data[i + 1] = (byte)(pel >> 8);
        }
        // destRight/destBottom are INCLUSIVE per [MS-RDPBCGR] 2.2.9.1.1.3.1.2.2.
        return new BitmapRect(0, 0, width - 1, height - 1, width, height, data);
    }
}
