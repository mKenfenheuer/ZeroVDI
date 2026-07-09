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
    /// The destination rectangle a <paramref name="srcW"/>×<paramref name="srcH"/> source maps into within a
    /// <paramref name="dstW"/>×<paramref name="dstH"/> destination, preserving aspect ratio, centered, with
    /// even-aligned offsets/size (so chroma-subsampled encoders stay aligned). Mirrors macRDP's letterbox.
    /// </summary>
    public static (int x, int y, int w, int h) Letterbox(int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) return (0, 0, dstW, dstH);
        double scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
        int fitW = Math.Max(2, Math.Min((int)Math.Round(srcW * scale) & ~1, dstW));
        int fitH = Math.Max(2, Math.Min((int)Math.Round(srcH * scale) & ~1, dstH));
        int x = ((dstW - fitW) / 2) & ~1;
        int y = ((dstH - fitH) / 2) & ~1;
        return (x, y, fitW, fitH);
    }

    /// <summary>
    /// Nearest-neighbour scales a top-down 32bpp BGRX source rectangle into a destination rectangle of the
    /// given size, writing into <paramref name="dst"/> (a top-down 32bpp BGRX session framebuffer of
    /// <paramref name="dstStrideW"/> pixels per row) at (dstX, dstY). Nearest-neighbour keeps this cheap on
    /// the relay hot path; the bitmap fallback is a low-fidelity path anyway (H.264/GFX is the quality path).
    /// </summary>
    public static void ScaleBlitBgrx(ReadOnlySpan<byte> src, int srcW, int srcH,
        byte[] dst, int dstStrideW, int dstX, int dstY, int dstW, int dstH)
    {
        for (int dy = 0; dy < dstH; dy++)
        {
            int sy = srcH == dstH ? dy : (int)((long)dy * srcH / dstH);
            int srcRow = sy * srcW * 4;
            int dstRow = ((dstY + dy) * dstStrideW + dstX) * 4;
            for (int dx = 0; dx < dstW; dx++)
            {
                int sx = srcW == dstW ? dx : (int)((long)dx * srcW / dstW);
                int s = srcRow + sx * 4;
                int d = dstRow + dx * 4;
                dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2]; dst[d + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Converts a sub-rectangle of a top-down 32bpp BGRX session framebuffer into a <see cref="BitmapRect"/>
    /// (16bpp RGB565, bottom-up rows). IMPORTANT: this browser client's uncompressed bitmap renderer
    /// (<c>color.js</c>) uses a stride of exactly <c>width*2</c> with NO row padding and runs <c>flipV</c>,
    /// so we emit tightly-packed rows — 4-byte row padding (which mstsc expects) shears the image here.
    /// </summary>
    public static BitmapRect FramebufferRegionToRect(byte[] fb, int fbW, int x, int y, int w, int h)
    {
        int rowBytes = w * 2;
        var data = new byte[rowBytes * h];
        for (int row = 0; row < h; row++)
        {
            int srcRow = ((y + row) * fbW + x) * 4;           // top-down BGRX
            int dstRow = (h - 1 - row) * rowBytes;            // bottom-up 565
            for (int col = 0; col < w; col++)
            {
                int s = srcRow + col * 4;                       // B,G,R,x
                ushort pel = Rgb565(fb[s + 2], fb[s + 1], fb[s]);
                int d = dstRow + col * 2;
                data[d] = (byte)(pel & 0xff);
                data[d + 1] = (byte)(pel >> 8);
            }
        }
        return new BitmapRect(x, y, x + w - 1, y + h - 1, w, h, data);
    }

    /// <summary>
    /// Converts a top-down 32bpp BGRX rectangle (as RFB delivers with our SetPixelFormat) into a
    /// <see cref="BitmapRect"/>: 16bpp RGB565, <b>bottom-up</b> rows, each row padded to a 4-byte
    /// multiple ([MS-RDPBCGR] 2.2.9.1.1.3.1.2.2). The browser client's <c>flipV</c> restores top-down.
    /// </summary>
    public static BitmapRect Bgrx32ToRect(int destLeft, int destTop, int width, int height, ReadOnlySpan<byte> bgrx)
    {
        int rowBytes = width * 2;
        int pad = (4 - (rowBytes % 4)) % 4;
        int stride = rowBytes + pad;
        var data = new byte[stride * height];
        for (int row = 0; row < height; row++)
        {
            int srcRow = row * width * 4;                  // top-down source row
            int dstRow = (height - 1 - row) * stride;      // bottom-up destination row
            for (int x = 0; x < width; x++)
            {
                int s = srcRow + x * 4;                     // B,G,R,x
                ushort pel = Rgb565(bgrx[s + 2], bgrx[s + 1], bgrx[s]);
                int d = dstRow + x * 2;
                data[d] = (byte)(pel & 0xff);
                data[d + 1] = (byte)(pel >> 8);
            }
            // padding bytes already zero
        }
        // destRight/destBottom inclusive.
        return new BitmapRect(destLeft, destTop, destLeft + width - 1, destTop + height - 1, width, height, data);
    }

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
