using System.IO.Compression;

namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// Decoder for the RFB <b>ZRLE</b> encoding (encoding id 16, [RFC 6143] §7.7.5) — a compact, zlib-based codec
/// that macOS's built-in Screen Sharing / Apple Remote Desktop server supports well and prefers over Raw. We
/// negotiate it alongside Tight so Apple's server has a good non-photographic path (its Tight implementation is
/// weak and often falls back to near-Raw rectangles; ZRLE keeps flat UI, palettes, and runs cheap on the wire).
///
/// ZRLE keeps a <b>single</b> persistent zlib stream for the whole session (unlike Tight's four). Each
/// rectangle on the wire is: length(4, big-endian) + that many zlib-compressed bytes. The inflated bytes are a
/// stream of 64×64 tiles, left-to-right then top-to-bottom, each prefixed by a subencoding byte:
///   0        → raw CPIXELs (w*h of them)
///   1        → solid: one CPIXEL fills the tile
///   2..16    → packed palette: palette of N CPIXELs, then bit-packed indices (1/2/4 bits per pixel)
///   128      → plain RLE: (CPIXEL, run-length) pairs
///   130..255 → palette RLE: palette of (subenc-128) CPIXELs, then indexed runs
/// (129 is unused.) CPIXEL is the 3-byte "compressed pixel": for our 32bpp true-colour BGRX format the least
/// significant byte is dropped, so each CPIXEL is B,G,R and we pad X=0xFF. Output is top-down 32bpp BGRX — the
/// same format <see cref="RfbClient"/> emits for Raw/Tight, so the encoder side is unchanged.
/// </summary>
internal sealed class ZrleDecoder
{
    private readonly Func<int, CancellationToken, Task<byte[]>> _readExact;

    // The single session-persistent zlib stream. ZRLE never resets it mid-session.
    private readonly FeedStream _in = new();
    private ZLibStream? _z;

    // Reusable inflate scratch for one rectangle's uncompressed tile stream.
    private byte[] _tileBuf = Array.Empty<byte>();

    public ZrleDecoder(Func<int, CancellationToken, Task<byte[]>> readExact) => _readExact = readExact;

    /// <summary>
    /// Decodes one ZRLE rectangle of size w×h and returns its top-down 32bpp BGRX pixels. The rectangle
    /// header (x/y/w/h/encoding) has already been consumed by the caller.
    /// </summary>
    public async Task<byte[]> DecodeRectAsync(int w, int h, CancellationToken ct)
    {
        var dst = new byte[w * h * 4];
        if (w == 0 || h == 0) return dst;

        // length(4, big-endian) + compressed payload for this rectangle.
        var lenB = await _readExact(4, ct);
        int clen = (lenB[0] << 24) | (lenB[1] << 16) | (lenB[2] << 8) | lenB[3];
        var compressed = await _readExact(clen, ct);

        // Inflate the whole rectangle's tile stream. The uncompressed size is not sent, so we grow-and-read
        // until the inflater has consumed all the input we just appended (it stops returning bytes when the
        // fed run is exhausted). A rectangle can never inflate to more than w*h raw CPIXELs + tile overhead.
        byte[] data = Inflate(compressed);
        var cur = new Cur(data);

        // Walk 64×64 tiles across then down.
        for (int ty = 0; ty < h; ty += 64)
        {
            int th = Math.Min(64, h - ty);
            for (int tx = 0; tx < w; tx += 64)
            {
                int tw = Math.Min(64, w - tx);
                DecodeTile(ref cur, dst, w, tx, ty, tw, th);
            }
        }
        return dst;
    }

    private static void DecodeTile(ref Cur c, byte[] dst, int fbw, int tx, int ty, int tw, int th)
    {
        int sub = c.Byte();
        if (sub == 0)
        {
            // Raw CPIXELs, row-major.
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                    PutCpixel(dst, (ty + y) * fbw + tx + x, ref c);
            return;
        }
        if (sub == 1)
        {
            // Solid tile: one CPIXEL for the whole tile.
            (byte b, byte g, byte r) = c.Cpixel();
            for (int y = 0; y < th; y++)
            {
                int o = ((ty + y) * fbw + tx) * 4;
                for (int x = 0; x < tw; x++, o += 4) { dst[o] = b; dst[o + 1] = g; dst[o + 2] = r; dst[o + 3] = 0xFF; }
            }
            return;
        }
        if (sub <= 16)
        {
            // Packed palette: N-colour palette, then bit-packed indices (1/2/4 bits per pixel, MSB-first,
            // each row byte-aligned).
            int n = sub;
            var pal = ReadPalette(ref c, n);
            int bpp = n <= 2 ? 1 : n <= 4 ? 2 : 4;
            int mask = (1 << bpp) - 1;
            int perByte = 8 / bpp;
            for (int y = 0; y < th; y++)
            {
                int rowBytes = (tw * bpp + 7) / 8;
                for (int x = 0; x < tw; )
                {
                    int packed = c.Byte();
                    for (int k = 0; k < perByte && x < tw; k++, x++)
                    {
                        int shift = 8 - bpp - k * bpp;
                        int idx = (packed >> shift) & mask;
                        PutPalette(dst, (ty + y) * fbw + tx + x, pal, idx);
                    }
                }
            }
            return;
        }
        if (sub == 128)
        {
            // Plain RLE: (CPIXEL, run-length) pairs filling the tile row-major.
            int total = tw * th, done = 0;
            while (done < total)
            {
                (byte b, byte g, byte r) = c.Cpixel();
                int run = ReadRunLength(ref c);
                for (int i = 0; i < run && done < total; i++, done++)
                    PutBgr(dst, TilePixelIndex(fbw, tx, ty, tw, done), b, g, r);
            }
            return;
        }
        // sub >= 130: palette RLE. Palette of (sub-128) colours, then indexed runs; an index with the high bit
        // set carries a run-length, otherwise it's a single pixel of that palette entry.
        {
            int n = sub - 128;
            var pal = ReadPalette(ref c, n);
            int total = tw * th, done = 0;
            while (done < total)
            {
                int idx = c.Byte();
                int run = 1;
                if ((idx & 0x80) != 0) { idx &= 0x7F; run = ReadRunLength(ref c); }
                for (int i = 0; i < run && done < total; i++, done++)
                    PutPaletteFlat(dst, TilePixelIndex(fbw, tx, ty, tw, done), pal, idx);
            }
        }
    }

    // Run length: sum of bytes until one is < 255, plus 1 (the value is length-1).
    private static int ReadRunLength(ref Cur c)
    {
        int run = 1;
        int b;
        do { b = c.Byte(); run += b; } while (b == 255);
        return run;
    }

    // Reads n CPIXELs into a flat B,G,R palette (3 bytes each).
    private static byte[] ReadPalette(ref Cur c, int n)
    {
        var pal = new byte[n * 3];
        for (int i = 0; i < n; i++)
        {
            (byte b, byte g, byte r) = c.Cpixel();
            pal[i * 3] = b; pal[i * 3 + 1] = g; pal[i * 3 + 2] = r;
        }
        return pal;
    }

    // Maps the k-th pixel (row-major within a tile) to its absolute framebuffer pixel index.
    private static int TilePixelIndex(int fbw, int tx, int ty, int tw, int k)
    {
        int lx = k % tw, ly = k / tw;
        return (ty + ly) * fbw + tx + lx;
    }

    private static void PutCpixel(byte[] dst, int pixelIndex, ref Cur c)
    {
        (byte b, byte g, byte r) = c.Cpixel();
        PutBgr(dst, pixelIndex, b, g, r);
    }

    private static void PutBgr(byte[] dst, int pixelIndex, byte b, byte g, byte r)
    {
        int o = pixelIndex * 4;
        dst[o] = b; dst[o + 1] = g; dst[o + 2] = r; dst[o + 3] = 0xFF;
    }

    private static void PutPalette(byte[] dst, int pixelIndex, byte[] pal, int idx)
        => PutPaletteFlat(dst, pixelIndex, pal, idx);

    private static void PutPaletteFlat(byte[] dst, int pixelIndex, byte[] pal, int idx)
    {
        int p = idx * 3;
        if (p + 2 >= pal.Length) { PutBgr(dst, pixelIndex, 0, 0, 0); return; } // defensive: bad index
        PutBgr(dst, pixelIndex, pal[p], pal[p + 1], pal[p + 2]);
    }

    // Inflate the fed compressed run; returns a buffer holding exactly the produced bytes. Grows _tileBuf as
    // needed and reuses it across rectangles to avoid per-rect allocation churn.
    private byte[] Inflate(byte[] compressed)
    {
        _in.Append(compressed);
        _z ??= new ZLibStream(_in, CompressionMode.Decompress, leaveOpen: true);
        int got = 0;
        while (true)
        {
            if (got == _tileBuf.Length)
            {
                int next = _tileBuf.Length == 0 ? Math.Max(4096, compressed.Length * 4) : _tileBuf.Length * 2;
                Array.Resize(ref _tileBuf, next);
            }
            int n = _z.Read(_tileBuf, got, _tileBuf.Length - got);
            if (n == 0) break; // all of the input we appended has been consumed
            got += n;
        }
        // Return a right-sized view. Callers walk it via Cur, so a copy keeps bounds honest.
        var outb = new byte[got];
        Buffer.BlockCopy(_tileBuf, 0, outb, 0, got);
        return outb;
    }

    // A forward cursor over the inflated tile stream, with CPIXEL (3-byte B,G,R) reads for our BGRX format.
    private ref struct Cur
    {
        private readonly byte[] _d;
        private int _p;
        public Cur(byte[] d) { _d = d; _p = 0; }
        public byte Byte() => _p < _d.Length ? _d[_p++] : (byte)0;
        public (byte b, byte g, byte r) Cpixel()
        {
            byte b = Byte(), g = Byte(), r = Byte();
            return (b, g, r);
        }
    }

    // Same append-only, forward-only, non-blocking feed stream Tight uses for its zlib channels: each rectangle
    // appends its compressed run and we read exactly what inflates from it. Never seeked (that would corrupt the
    // inflater's buffering); consumed head chunks are dropped so it doesn't grow over a long session.
    private sealed class FeedStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private int _headOffset;

        public void Append(byte[] data) { if (data.Length > 0) _chunks.Enqueue(data); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (count > 0 && _chunks.Count > 0)
            {
                var head = _chunks.Peek();
                int avail = head.Length - _headOffset;
                int n = Math.Min(avail, count);
                Buffer.BlockCopy(head, _headOffset, buffer, offset, n);
                _headOffset += n; offset += n; count -= n; total += n;
                if (_headOffset >= head.Length) { _chunks.Dequeue(); _headOffset = 0; }
            }
            return total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
