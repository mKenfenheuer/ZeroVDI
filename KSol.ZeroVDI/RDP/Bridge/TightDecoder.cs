using System.IO.Compression;

namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// Decoder for the RFB <b>Tight</b> encoding (encoding id 7, [RFC 6143] §7.7.6) — the fast VNC path we
/// negotiate in preference to Raw. Tight is the big bandwidth win on slow links: it combines four
/// persistent zlib streams (with filters: copy / palette / gradient) for "basic" rectangles and JPEG for
/// photographic ones, plus a compact solid-fill mode.
///
/// This decoder produces top-down 32bpp BGRX pixels (the same format <see cref="RfbClient"/> emits for Raw),
/// so the encoder side is unchanged. It assumes the connection's pixel format is the 32bpp true-colour
/// BGRX format <see cref="RfbClient.SetPixelFormatBgrx32"/> negotiates, which lets Tight use the 3-byte
/// TPIXEL representation (the "last significant byte" of a 4-byte pixel is dropped on the wire).
///
/// The four zlib streams keep dictionary state for the whole session, so they must be fed the exact
/// compressed byte runs in order. We hold a persistent <see cref="ZlibChannel"/> per stream that inflates
/// incrementally.
/// </summary>
internal sealed class TightDecoder
{
    // Reads exactly n bytes from the RFB socket (shared with RfbClient's reader).
    private readonly Func<int, CancellationToken, Task<byte[]>> _readExact;
    // Decodes a JPEG blob to top-down BGRX. Supplied by the host so we don't take an image dependency here.
    private readonly Func<byte[], (int w, int h, byte[] bgrx)> _decodeJpeg;

    private readonly ZlibChannel[] _zlib = new ZlibChannel[4];

    public TightDecoder(Func<int, CancellationToken, Task<byte[]>> readExact,
        Func<byte[], (int, int, byte[])> decodeJpeg)
    {
        _readExact = readExact;
        _decodeJpeg = decodeJpeg;
        for (int i = 0; i < 4; i++) _zlib[i] = new ZlibChannel();
    }

    /// <summary>
    /// Decodes one Tight rectangle of size w×h and returns its top-down 32bpp BGRX pixels. The rectangle
    /// header (x/y/w/h/encoding) has already been consumed by the caller.
    /// </summary>
    public async Task<byte[]> DecodeRectAsync(int w, int h, CancellationToken ct)
    {
        var dst = new byte[w * h * 4];
        if (w == 0 || h == 0) return dst;

        byte ctl = (await _readExact(1, ct))[0];

        // Top nibble bits 0-3: reset zlib stream i if its reset bit is set.
        for (int i = 0; i < 4; i++)
            if ((ctl & (1 << i)) != 0) _zlib[i].Reset();

        int method = ctl >> 4;

        // Fill compression (0x08 → method nibble 0x8): a single TPIXEL, whole rect that colour.
        if (method == 0x08)
        {
            var tpix = await _readExact(3, ct);
            FillSolid(dst, tpix[0], tpix[1], tpix[2]);
            return dst;
        }

        // JPEG compression (0x09 → method nibble 0x9): compact-length + JPEG blob.
        if (method == 0x09)
        {
            int len = await ReadCompactLenAsync(ct);
            var jpeg = await _readExact(len, ct);
            var (jw, jh, bgrx) = _decodeJpeg(jpeg);
            BlitClamped(bgrx, jw, jh, dst, w, h);
            return dst;
        }

        // Basic compression: bit 6 (0x40) present → a filter-id byte follows; else the Copy filter.
        int filterId = 0; // 0=Copy, 1=Palette, 2=Gradient
        if ((method & 0x4) != 0) filterId = (await _readExact(1, ct))[0];
        int streamId = method & 0x3;

        switch (filterId)
        {
            case 1: await DecodePaletteAsync(streamId, w, h, dst, ct); break;
            case 2: await DecodeGradientAsync(streamId, w, h, dst, ct); break;
            default: await DecodeCopyAsync(streamId, w, h, dst, ct); break;
        }
        return dst;
    }

    // ── filters ───────────────────────────────────────────────────────────────────────────────────

    // Copy filter: w*h TPIXELs (3 bytes each), row-major top-down.
    private async Task DecodeCopyAsync(int streamId, int w, int h, byte[] dst, CancellationToken ct)
    {
        int count = w * h * 3;
        var data = await ReadFilteredAsync(streamId, count, ct);
        for (int i = 0, p = 0; i < w * h; i++, p += 3)
        {
            int o = i * 4;
            dst[o] = data[p]; dst[o + 1] = data[p + 1]; dst[o + 2] = data[p + 2]; dst[o + 3] = 0xFF;
        }
    }

    // Palette filter: numColors(1)=(count-1), then count TPIXELs, then indices. 1-bit packed for a
    // 2-colour palette, otherwise 1 byte per pixel.
    private async Task DecodePaletteAsync(int streamId, int w, int h, byte[] dst, CancellationToken ct)
    {
        int colors = (await _readExact(1, ct))[0] + 1;
        var pal = await _readExact(colors * 3, ct);
        int rows = h, cols = w;
        if (colors <= 2)
        {
            int rowBytes = (cols + 7) / 8;
            var idx = await ReadFilteredAsync(streamId, rowBytes * rows, ct);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    int bit = (idx[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1;
                    PutPalette(dst, (y * cols + x) * 4, pal, bit);
                }
        }
        else
        {
            var idx = await ReadFilteredAsync(streamId, cols * rows, ct);
            for (int i = 0; i < cols * rows; i++) PutPalette(dst, i * 4, pal, idx[i]);
        }
    }

    // Gradient filter: per-pixel prediction P = left + up - upleft (clamped per channel), residuals sent
    // as w*h TPIXELs; pixel = residual + prediction (mod 256 per RFC).
    private async Task DecodeGradientAsync(int streamId, int w, int h, byte[] dst, CancellationToken ct)
    {
        var data = await ReadFilteredAsync(streamId, w * h * 3, ct);
        // Work on a 3-channel scratch so predictions use decoded (not BGRX-padded) neighbours.
        var prev = new int[(w + 1) * 3];   // previous row, 1-pixel left pad (index 0 = virtual col -1)
        var cur = new int[(w + 1) * 3];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            for (int c = 0; c < 3; c++) cur[c] = 0; // virtual left column = 0
            for (int x = 0; x < w; x++)
            {
                int ci = (x + 1) * 3;
                for (int c = 0; c < 3; c++)
                {
                    int left = cur[ci - 3 + c];
                    int up = prev[ci + c];
                    int upleft = prev[ci - 3 + c];
                    int pred = left + up - upleft;
                    if (pred < 0) pred = 0; else if (pred > 255) pred = 255;
                    int val = (data[p++] + pred) & 0xFF;
                    cur[ci + c] = val;
                }
                int o = (y * w + x) * 4;
                dst[o] = (byte)cur[ci]; dst[o + 1] = (byte)cur[ci + 1]; dst[o + 2] = (byte)cur[ci + 2]; dst[o + 3] = 0xFF;
            }
            (prev, cur) = (cur, prev);
        }
    }

    // A filtered basic run: if the uncompressed size is < 12 bytes it is sent raw (no zlib), else it is
    // zlib-compressed with a compact-length prefix on stream `streamId`.
    private async Task<byte[]> ReadFilteredAsync(int streamId, int uncompressed, CancellationToken ct)
    {
        if (uncompressed < 12) return await _readExact(uncompressed, ct);
        int clen = await ReadCompactLenAsync(ct);
        var compressed = await _readExact(clen, ct);
        return _zlib[streamId].Inflate(compressed, uncompressed);
    }

    // Tight compact length: 1-3 bytes, 7 bits each, low byte first, high bit = continuation.
    private async Task<int> ReadCompactLenAsync(CancellationToken ct)
    {
        int b0 = (await _readExact(1, ct))[0];
        int len = b0 & 0x7F;
        if ((b0 & 0x80) == 0) return len;
        int b1 = (await _readExact(1, ct))[0];
        len |= (b1 & 0x7F) << 7;
        if ((b1 & 0x80) == 0) return len;
        int b2 = (await _readExact(1, ct))[0];
        len |= (b2 & 0xFF) << 14;
        return len;
    }

    private static void PutPalette(byte[] dst, int o, byte[] pal, int idx)
    {
        int p = idx * 3;
        dst[o] = pal[p]; dst[o + 1] = pal[p + 1]; dst[o + 2] = pal[p + 2]; dst[o + 3] = 0xFF;
    }

    private static void FillSolid(byte[] dst, byte b, byte g, byte r)
    {
        for (int o = 0; o < dst.Length; o += 4) { dst[o] = b; dst[o + 1] = g; dst[o + 2] = r; dst[o + 3] = 0xFF; }
    }

    // Copy a JPEG-decoded BGRX buffer into the rect, clamping to the smaller dimensions (defensive; the
    // server should send a JPEG exactly w×h).
    private static void BlitClamped(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        int cw = Math.Min(sw, dw), ch = Math.Min(sh, dh);
        for (int y = 0; y < ch; y++)
            Buffer.BlockCopy(src, y * sw * 4, dst, y * dw * 4, cw * 4);
    }

    /// <summary>
    /// One of Tight's four persistent zlib streams. Tight requires the zlib dictionary/window to persist
    /// across rectangles, so we keep a single <see cref="ZLibStream"/> reading from an append-only
    /// <see cref="FeedStream"/>: each Inflate() appends the new compressed run to the queue and reads
    /// exactly the expected uncompressed length. The queue is never seeked (which would corrupt the
    /// inflater's internal read buffering), so consumed bytes simply advance forward.
    /// </summary>
    private sealed class ZlibChannel
    {
        private FeedStream _in = new();
        private ZLibStream? _z;

        public void Reset()
        {
            _z?.Dispose();
            _z = null;
            _in = new FeedStream();
        }

        public byte[] Inflate(byte[] compressed, int uncompressedLen)
        {
            _in.Append(compressed);
            _z ??= new ZLibStream(_in, CompressionMode.Decompress, leaveOpen: true);

            var outBuf = new byte[uncompressedLen];
            int got = 0;
            while (got < uncompressedLen)
            {
                int n = _z.Read(outBuf, got, uncompressedLen - got);
                if (n == 0) break; // needs more input than provided — shouldn't happen for well-formed runs
                got += n;
            }
            return outBuf;
        }

        // A forward-only stream that grows by Append() and is consumed by sequential Read(). Bytes already
        // read are dropped so it doesn't grow unbounded over a long session. Reads never block: they return
        // only what is buffered (the caller feeds exactly the compressed run before each expected read).
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
}
