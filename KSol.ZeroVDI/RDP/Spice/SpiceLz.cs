namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// SPICE LZ-RGB image decompression (a C# port of spice-html5's lz.js, itself adapted from lz.c).
/// Handles the RGB32/RGBA/XXXA sub-types QEMU/SPICE emit for LZ-compressed rectangles. Output is a
/// tightly packed top-down BGRA buffer (width*height*4) so it can be blitted straight into the
/// display channel's BGRX framebuffer.
/// </summary>
internal static class SpiceLz
{
    /// <summary>
    /// Decompresses an LZ-RGB image. <paramref name="data"/> is the raw LZ stream (after the 28-byte
    /// big-endian header the display channel already parsed). Returns top-down BGRA of size w*h*4, or
    /// null for an unsupported sub-type.
    /// </summary>
    public static byte[]? Decode(byte[] data, int lzType, int width, int height, bool topDown)
    {
        // The decompressor writes RGBA (R,G,B,A order); we swap into BGRA at the end. spice-html5 works
        // in canvas RGBA space, so mirror that then convert once.
        byte[] rgba = new byte[width * height * 4];

        switch (lzType)
        {
            case SpiceConst.LZ_IMAGE_TYPE_RGB32:
            case SpiceConst.LZ_IMAGE_TYPE_RGBA:
            {
                int at = Rgb32Decompress(data, 0, rgba, SpiceConst.LZ_IMAGE_TYPE_RGB32,
                    defaultAlpha: lzType != SpiceConst.LZ_IMAGE_TYPE_RGBA);
                if (!topDown) FlipRows(rgba, width, height);
                if (lzType == SpiceConst.LZ_IMAGE_TYPE_RGBA)
                    Rgb32Decompress(data, at, rgba, SpiceConst.LZ_IMAGE_TYPE_RGBA, defaultAlpha: false);
                break;
            }
            case SpiceConst.LZ_IMAGE_TYPE_XXXA:
                Rgb32Decompress(data, 0, rgba, SpiceConst.LZ_IMAGE_TYPE_RGBA, defaultAlpha: false);
                break;
            default:
                return null;
        }

        // RGBA → BGRA (swap R/B) in place.
        for (int i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        return rgba;
    }

    // Direct port of lz_rgb32_decompress from lz.js. out_buf is RGBA (4 bytes/pixel).
    private static int Rgb32Decompress(byte[] inBuf, int at, byte[] outBuf, int type, bool defaultAlpha)
    {
        bool rgba = type == SpiceConst.LZ_IMAGE_TYPE_RGBA;
        int encoder = at;
        int op = 0;

        while (op * 4 < outBuf.Length)
        {
            int ctrl = inBuf[encoder++];
            int reff = op;
            int len = ctrl >> 5;
            int ofs = (ctrl & 31) << 8;

            if (ctrl >= 32)
            {
                int code;
                len--;
                if (len == 7 - 1)
                {
                    do { code = inBuf[encoder++]; len += code; } while (code == 255);
                }
                code = inBuf[encoder++];
                ofs += code;

                if (code == 255)
                {
                    if ((ofs - code) == (31 << 8))
                    {
                        ofs = inBuf[encoder++] << 8;
                        ofs += inBuf[encoder++];
                        ofs += 8191;
                    }
                }
                len += 1;
                if (rgba) len += 2;
                ofs += 1;

                reff -= ofs;
                if (reff == op - 1)
                {
                    int b = reff;
                    for (; len > 0; --len)
                    {
                        if (rgba) outBuf[op * 4 + 3] = outBuf[b * 4 + 3];
                        else for (int i = 0; i < 4; i++) outBuf[op * 4 + i] = outBuf[b * 4 + i];
                        op++;
                    }
                }
                else
                {
                    for (; len > 0; --len)
                    {
                        if (rgba) outBuf[op * 4 + 3] = outBuf[reff * 4 + 3];
                        else for (int i = 0; i < 4; i++) outBuf[op * 4 + i] = outBuf[reff * 4 + i];
                        op++; reff++;
                    }
                }
            }
            else
            {
                ctrl++;
                if (rgba)
                {
                    outBuf[op * 4 + 3] = inBuf[encoder++];
                }
                else
                {
                    outBuf[op * 4 + 0] = inBuf[encoder + 2];
                    outBuf[op * 4 + 1] = inBuf[encoder + 1];
                    outBuf[op * 4 + 2] = inBuf[encoder + 0];
                    if (defaultAlpha) outBuf[op * 4 + 3] = 255;
                    encoder += 3;
                }
                op++;

                for (--ctrl; ctrl > 0; ctrl--)
                {
                    if (rgba)
                    {
                        outBuf[op * 4 + 3] = inBuf[encoder++];
                    }
                    else
                    {
                        outBuf[op * 4 + 0] = inBuf[encoder + 2];
                        outBuf[op * 4 + 1] = inBuf[encoder + 1];
                        outBuf[op * 4 + 2] = inBuf[encoder + 0];
                        if (defaultAlpha) outBuf[op * 4 + 3] = 255;
                        encoder += 3;
                    }
                    op++;
                }
            }
        }
        return encoder;
    }

    private static void FlipRows(byte[] rgba, int width, int height)
    {
        int wb = width * 4;
        byte[] tmp = new byte[wb];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * wb, bot = (height - 1 - y) * wb;
            Array.Copy(rgba, top, tmp, 0, wb);
            Array.Copy(rgba, bot, rgba, top, wb);
            Array.Copy(tmp, 0, rgba, bot, wb);
        }
    }
}
