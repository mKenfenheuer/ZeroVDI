// PointerTracker — server-side mouse-cursor state for session recordings.
//
// The desktop video stream never contains the mouse cursor: RDP sends the pointer SHAPE out of band
// (fastpath PTR_NEW / PTR_COLOR / PTR_CACHED updates, [MS-RDPBCGR] 2.2.9.1.1.4) and the client draws
// it locally. A recording built only from the video stream therefore shows no cursor at all. This
// tracker mirrors what a client does: it decodes the pointer shapes into BGRA+alpha bitmaps, keeps
// the cache, follows the current position (from the client's own fastpath MOUSE input events, plus
// server PTR_POSITION updates), and composites the active cursor onto each raw BGRA frame the
// GfxProgressiveCompositor emits — BEFORE the frame is H.264-encoded at mux, so no re-encode pass is
// needed.
//
// The mask decode mirrors FreeRDP freerdp_image_copy_from_pointer_data() (and the web client's
// pointer.js): scan lines are WORD-padded; color masks are bottom-up, 1-bpp masks top-down; 32-bpp
// XOR pixels carry their own alpha; 24/16-bpp use the AND mask (black->transparent, white->"inverted",
// which we draw as solid black since a static bitmap cannot XOR the pixels beneath it).

using System;

namespace KSol.RDPGateway.RDP;

internal sealed class PointerTracker
{
    internal sealed class Shape
    {
        public int Width, Height, HotX, HotY;
        public byte[] Bgra = Array.Empty<byte>(); // top-down, premux alpha in [3]
    }

    private readonly System.Collections.Generic.Dictionary<int, Shape> _cache = new();
    private Shape? _current;          // null => no custom shape (hidden or OS default)
    private bool _hidden;             // PTR_NULL
    private bool _havePosition;
    private int _x, _y;

    private static readonly Shape DefaultArrow = BuildDefaultArrow();

    // ---- state updates (called from RdpFastPath) ----------------------------------------------

    public void OnPtrNull() { _hidden = true; }
    public void OnPtrDefault() { _hidden = false; _current = null; }

    public void OnPtrCached(int cacheIndex)
    {
        _hidden = false;
        if (_cache.TryGetValue(cacheIndex, out var s)) _current = s;
        // Unknown index: keep the current shape (matches the web client's behavior).
    }

    public void OnPtrPosition(int x, int y) { _havePosition = true; _x = x; _y = y; }

    /// <summary>Client mouse input ([MS-RDPBCGR] 2.2.8.1.1.3.1.1.3). Wheel events carry rotation, not
    /// position, in some client implementations — only trust x/y on non-wheel events.</summary>
    public void OnMouseInput(int pointerFlags, int x, int y)
    {
        if ((pointerFlags & 0x0600) != 0) return; // WHEEL / HWHEEL
        _havePosition = true; _x = x; _y = y;
    }

    /// <summary>Decode PTR_NEW (TS_POINTERATTRIBUTE: xorBpp prefix) or PTR_COLOR (implicit 24-bpp).</summary>
    public void OnPtrShape(ReadOnlySpan<byte> data, bool hasXorBpp)
    {
        int headerLen = hasXorBpp ? 16 : 14;
        if (data.Length < headerLen) return;
        int o = 0;
        int xorBpp = 24;
        if (hasXorBpp) { xorBpp = data[o] | (data[o + 1] << 8); o += 2; }
        int cacheIndex = data[o] | (data[o + 1] << 8); o += 2;
        int hotX = data[o] | (data[o + 1] << 8); o += 2;
        int hotY = data[o] | (data[o + 1] << 8); o += 2;
        int width = data[o] | (data[o + 1] << 8); o += 2;
        int height = data[o] | (data[o + 1] << 8); o += 2;
        int lenAnd = data[o] | (data[o + 1] << 8); o += 2;
        int lenXor = data[o] | (data[o + 1] << 8); o += 2;
        if (width <= 0 || height <= 0 || width > 384 || height > 384) return;
        if ((long)o + lenXor + lenAnd > data.Length) return;
        var xor = data.Slice(o, lenXor);
        var and = data.Slice(o + lenXor, lenAnd);

        var shape = DecodeShape(xorBpp, width, height, hotX, hotY, xor, and);
        if (shape == null) return;
        _cache[cacheIndex] = shape;
        _current = shape;
        _hidden = false;
    }

    // ---- compositing ---------------------------------------------------------------------------

    /// <summary>Alpha-blend the active cursor into a top-down BGRA frame, in place.</summary>
    public void CompositeOnto(byte[] bgra, int width, int height)
    {
        if (_hidden || !_havePosition) return;
        var s = _current ?? DefaultArrow;
        int left = _x - s.HotX, top = _y - s.HotY;
        for (int sy = 0; sy < s.Height; sy++)
        {
            int dy = top + sy;
            if (dy < 0 || dy >= height) continue;
            for (int sx = 0; sx < s.Width; sx++)
            {
                int dx = left + sx;
                if (dx < 0 || dx >= width) continue;
                int si = (sy * s.Width + sx) * 4;
                int a = s.Bgra[si + 3];
                if (a == 0) continue;
                int di = (dy * width + dx) * 4;
                if (a == 255)
                {
                    bgra[di] = s.Bgra[si]; bgra[di + 1] = s.Bgra[si + 1]; bgra[di + 2] = s.Bgra[si + 2];
                }
                else
                {
                    int ia = 255 - a;
                    bgra[di] = (byte)((s.Bgra[si] * a + bgra[di] * ia + 127) / 255);
                    bgra[di + 1] = (byte)((s.Bgra[si + 1] * a + bgra[di + 1] * ia + 127) / 255);
                    bgra[di + 2] = (byte)((s.Bgra[si + 2] * a + bgra[di + 2] * ia + 127) / 255);
                }
            }
        }
    }

    // ---- shape decoding --------------------------------------------------------------------------

    private static int ScanlineStride(int width, int bpp) => ((width * bpp + 15) >> 4) << 1;

    private static Shape? DecodeShape(int xorBpp, int width, int height, int hotX, int hotY,
        ReadOnlySpan<byte> xor, ReadOnlySpan<byte> and)
    {
        var shape = new Shape { Width = width, Height = height, HotX = hotX, HotY = hotY, Bgra = new byte[width * height * 4] };
        return xorBpp == 1
            ? Decode1Bpp(shape, xor, and) : DecodeColor(shape, xorBpp, xor, and);
    }

    private static Shape? Decode1Bpp(Shape s, ReadOnlySpan<byte> xor, ReadOnlySpan<byte> and)
    {
        int step = ScanlineStride(s.Width, 1);
        if (step * s.Height > xor.Length || step * s.Height > and.Length) return null;
        for (int y = 0; y < s.Height; y++)
        {
            // 1-bpp masks are top-down (FreeRDP vFlip == false for xorBpp == 1).
            int rowOff = step * y;
            for (int x = 0; x < s.Width; x++)
            {
                int bit = 0x80 >> (x & 7), idx = rowOff + (x >> 3);
                bool xorPx = (xor[idx] & bit) != 0, andPx = (and[idx] & bit) != 0;
                // (and,xor): (0,0)=black (0,1)=white (1,0)=transparent (1,1)=inverted->black
                byte v = (byte)(!andPx && xorPx ? 0xFF : 0x00);
                byte a = (byte)(andPx && !xorPx ? 0x00 : 0xFF);
                int di = (y * s.Width + x) * 4;
                s.Bgra[di] = v; s.Bgra[di + 1] = v; s.Bgra[di + 2] = v; s.Bgra[di + 3] = a;
            }
        }
        return s;
    }

    private static Shape? DecodeColor(Shape s, int xorBpp, ReadOnlySpan<byte> xor, ReadOnlySpan<byte> and)
    {
        int bytesPerPx = xorBpp >> 3;
        if (bytesPerPx is not (2 or 3 or 4)) return null;
        int xorStep = ScanlineStride(s.Width, xorBpp);
        int andStep = ScanlineStride(s.Width, 1);
        if (xorStep * s.Height > xor.Length) return null;
        bool hasAnd = and.Length >= andStep * s.Height;

        for (int y = 0; y < s.Height; y++)
        {
            // Color masks are bottom-up.
            int xorRow = xorStep * (s.Height - y - 1);
            int andRow = andStep * (s.Height - y - 1);
            for (int x = 0; x < s.Width; x++)
            {
                bool andPx = hasAnd && (and[andRow + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                int si = xorRow + bytesPerPx * x;
                byte b, g, r, a;
                if (bytesPerPx == 4)
                {
                    b = xor[si]; g = xor[si + 1]; r = xor[si + 2]; a = xor[si + 3]; // real per-pixel alpha
                }
                else
                {
                    if (bytesPerPx == 3) { b = xor[si]; g = xor[si + 1]; r = xor[si + 2]; }
                    else // 16-bpp RGB555
                    {
                        int v = xor[si] | (xor[si + 1] << 8);
                        r = (byte)(((v >> 10) & 0x1F) * 255 / 31); g = (byte)(((v >> 5) & 0x1F) * 255 / 31); b = (byte)((v & 0x1F) * 255 / 31);
                    }
                    a = 0xFF;
                    if (andPx)
                    {
                        if (r == 0xFF && g == 0xFF && b == 0xFF) { r = g = b = 0x00; } // white -> inverted -> black
                        else a = 0x00;                                                 // else -> transparent
                    }
                }
                int di = (y * s.Width + x) * 4;
                s.Bgra[di] = b; s.Bgra[di + 1] = g; s.Bgra[di + 2] = r; s.Bgra[di + 3] = a;
            }
        }
        return s;
    }

    /// <summary>Classic 12x19 arrow (black fill, white outline) drawn when the host selects the
    /// system default pointer — a static stand-in for the OS cursor the viewer would otherwise see.</summary>
    private static Shape BuildDefaultArrow()
    {
        // 1 = white outline, 2 = black fill.
        string[] rows =
        {
            "1...........",
            "11..........",
            "121.........",
            "1221........",
            "12221.......",
            "122221......",
            "1222221.....",
            "12222221....",
            "122222221...",
            "1222222221..",
            "12222222221.",
            "122222111111",
            "1221221.....",
            "121.1221....",
            "11..1221....",
            "1....1221...",
            ".....1221...",
            "......11....",
            "............",
        };
        var s = new Shape { Width = 12, Height = 19, HotX = 0, HotY = 0, Bgra = new byte[12 * 19 * 4] };
        for (int y = 0; y < 19; y++)
            for (int x = 0; x < 12; x++)
            {
                char c = rows[y][x];
                if (c == '.') continue;
                int i = (y * 12 + x) * 4;
                byte v = c == '1' ? (byte)0xFF : (byte)0x00;
                s.Bgra[i] = v; s.Bgra[i + 1] = v; s.Bgra[i + 2] = v; s.Bgra[i + 3] = 0xFF;
            }
        return s;
    }
}
