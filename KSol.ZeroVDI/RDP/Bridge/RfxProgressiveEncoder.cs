using System.Runtime.CompilerServices;

namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// RemoteFX Progressive (RDPGFX_CODECID_CAPROGRESSIVE 0x0009) encoder for the RDPEGFX pipeline — the
/// fallback for clients whose GFX caps advertise no AVC decoder (they would otherwise drop to legacy
/// bitmaps). Emits the "progressive simple" profile FreeRDP's shadow server produces (and the rdpweb
/// <c>progressive.js</c> decoder consumes): per frame SYNC + CONTEXT + FRAME_BEGIN + REGION + FRAME_END,
/// shipped in WIRE_TO_SURFACE_2 PDUs.
///
/// Tiles are 64x64. Only tiles whose pixels changed since the previous frame are encoded (per-tile hash
/// compare against the last-sent state), so idle regions cost nothing. A changed tile is sent COARSE
/// (stage 0), then RE-SENT at successively finer quality on later idle frames (refine-when-idle),
/// converging to lossless. Faithful port of macRDP's <c>RfxProgressiveEncoder.swift</c> (itself FreeRDP's
/// encoder path: rfx_encode / prim_colors / rfx_dwt / rfx_quantization / rfx_differential / rfx_rlgr /
/// rfx wire framing). VideoToolbox is not involved — this is pure CPU and portable.
///
/// The encoder is driven from the session's tight top-down BGRA framebuffer (width*4 stride).
/// </summary>
internal sealed class RfxProgressiveEncoder
{
    // Progressive block types ([MS-RDPEGFX] 2.2.4.2, PROGRESSIVE_WBT_*).
    private const ushort WBT_SYNC = 0xCCC0;
    private const ushort WBT_FRAME_BEGIN = 0xCCC1;
    private const ushort WBT_FRAME_END = 0xCCC2;
    private const ushort WBT_CONTEXT = 0xCCC3;
    private const ushort WBT_REGION = 0xCCC4;
    private const ushort WBT_TILE_FIRST = 0xCCC6;

    /// <summary>Default quantization values in RDPRFX band order
    /// {LL3, LH3, HL3, HH3, LH2, HL2, HH2, LH1, HL1, HH1} (rfx.c).</summary>
    private static readonly int[] Quant = { 6, 6, 6, 6, 7, 7, 8, 8, 8, 9 };

    private readonly int _width, _height, _tilesX, _tilesY;
    private uint _frameIndex;

    /// <summary>Tight top-down BGRA copy the encode reads from (width*4 stride), refreshed per frame.</summary>
    private byte[] _curFrame;

    /// <summary>Per-tile hash of the pixels last encoded, indexed <c>ty*tilesX+tx</c>. A tile is re-encoded
    /// iff its current hash differs. <c>0</c> = never sent (forces encode).</summary>
    private readonly ulong[] _tileHashes;

    /// <summary>Per-tile progressive stage last SENT (index into <see cref="ProgStages"/>; last = lossless).
    /// <c>-1</c> = never sent / needs a full coarse send.</summary>
    private readonly int[] _tileStage;

    private bool _forceFullFrame = true;

    public RfxProgressiveEncoder(int width, int height)
    {
        _width = width; _height = height;
        _tilesX = (width + 63) / 64;
        _tilesY = (height + 63) / 64;
        _curFrame = new byte[width * height * 4];
        _tileHashes = new ulong[_tilesX * _tilesY];
        _tileStage = new int[_tilesX * _tilesY];
        Array.Fill(_tileStage, -1);
    }

    /// <summary>Force the next encode to send every tile (GFX re-negotiated: fresh client decoder state).</summary>
    public void Reset()
    {
        _forceFullFrame = true;
        Array.Clear(_tileHashes);
        Array.Fill(_tileStage, -1);
    }

    // Per-PDU payload budget. mstsc-family GFX decoders reject large WIRE_TO_SURFACE_2 CAPROGRESSIVE PDUs
    // (0x8007006f then disconnect); ground truth from a working GNOME-RD session never shipped a stream
    // larger than ~17 KB. 16384 keeps every stream under that proven-safe ceiling with margin.
    private const int MaxStreamBytes = 16_384;

    /// <summary>
    /// Encode one top-down BGRA session frame (length must be width*height*4) into zero or more complete
    /// RFX_PROGRESSIVE bitstreams — one WIRE_TO_SURFACE_2 payload each. Returns an empty list when no tile
    /// changed. Change detection is a per-tile pixel hash against the last-sent state (no reliance on dirty
    /// rects): a changed tile is reset to stage 0 (coarse), an unchanged-but-unrefined tile advances one
    /// stage, a lossless idle tile is skipped.
    /// </summary>
    public IReadOnlyList<byte[]> Encode(byte[] bgra)
    {
        if (bgra.Length != _curFrame.Length) return Array.Empty<byte[]>();
        Buffer.BlockCopy(bgra, 0, _curFrame, 0, bgra.Length);

        int lastStage = ProgStages.Length - 1;
        var changed = new List<(int tx, int ty, int stage)>();
        for (int ty = 0; ty < _tilesY; ty++)
        {
            for (int tx = 0; tx < _tilesX; tx++)
            {
                int idx = ty * _tilesX + tx;
                ulong h = HashTile(_curFrame, tx, ty, _width, _height);
                if (_forceFullFrame || h != _tileHashes[idx])
                {
                    _tileHashes[idx] = h;
                    _tileStage[idx] = 0;
                    changed.Add((tx, ty, 0));
                }
                else if (_tileStage[idx] >= 0 && _tileStage[idx] < lastStage)
                {
                    _tileStage[idx]++;
                    changed.Add((tx, ty, _tileStage[idx]));
                }
            }
        }
        _forceFullFrame = false;
        if (changed.Count == 0) return Array.Empty<byte[]>();

        // Encode each changed tile (fully independent) — fan out across cores for busy frames.
        var tiles = new Tile[changed.Count];
        if (changed.Count >= 4)
        {
            Parallel.For(0, changed.Count, () => new Scratch(), (i, _, s) =>
            {
                tiles[i] = EncodeTile(changed[i], s);
                return s;
            }, _ => { });
        }
        else
        {
            var s = new Scratch();
            for (int i = 0; i < changed.Count; i++) tiles[i] = EncodeTile(changed[i], s);
        }

        _frameIndex++;

        // Split into as few self-contained streams as fit the per-PDU budget. Interleave tile order first
        // so each PDU spans the whole surface (avoids the visible top-to-bottom "scanning" of raster bands).
        int totalBytes = StreamFixedOverhead;
        foreach (var t in tiles) totalBytes += 8 + 23 + t.Y.Length + t.Cb.Length + t.Cr.Length;
        int estStreams = Math.Max(1, (totalBytes + MaxStreamBytes - 1) / MaxStreamBytes);
        Tile[] ordered;
        if (estStreams > 1)
        {
            var list = new List<Tile>(tiles.Length);
            for (int i = 0; i < estStreams; i++)
                for (int j = i; j < tiles.Length; j += estStreams) list.Add(tiles[j]);
            ordered = list.ToArray();
        }
        else ordered = tiles;

        var streams = new List<byte[]>();
        var chunk = new List<Tile>();
        int chunkBytes = StreamFixedOverhead;
        foreach (var t in ordered)
        {
            int tileBytes = 8 + 23 + t.Y.Length + t.Cb.Length + t.Cr.Length;
            if (chunk.Count > 0 && chunkBytes + tileBytes > MaxStreamBytes)
            {
                streams.Add(WriteStream(chunk));
                chunk.Clear();
                chunkBytes = StreamFixedOverhead;
            }
            chunk.Add(t);
            chunkBytes += tileBytes;
        }
        if (chunk.Count > 0) streams.Add(WriteStream(chunk));
        return streams;
    }

    private Tile EncodeTile((int tx, int ty, int stage) t, Scratch s)
    {
        var pq = ProgStages[t.stage].Prog;
        ExtractTile(_curFrame, t.tx, t.ty, s);
        RgbToYCbCr(s);
        var y = EncodeComponent(s.PlaneY, s, pq);
        var cb = EncodeComponent(s.PlaneCb, s, pq);
        var cr = EncodeComponent(s.PlaneCr, s, pq);
        return new Tile(t.tx, t.ty, t.stage, y, cb, cr);
    }

    // ── Per-tile scratch ─────────────────────────────────────────────────────────────────────────
    private sealed class Scratch
    {
        public readonly short[] PlaneY = new short[4096];
        public readonly short[] PlaneCb = new short[4096];
        public readonly short[] PlaneCr = new short[4096];
        public readonly short[] PlaneR = new short[4096];
        public readonly short[] PlaneG = new short[4096];
        public readonly short[] PlaneB = new short[4096];
        public readonly short[] DwtTmp = new short[4096];
    }

    private readonly struct Tile
    {
        public readonly int Tx, Ty, Stage;
        public readonly byte[] Y, Cb, Cr;
        public Tile(int tx, int ty, int stage, byte[] y, byte[] cb, byte[] cr)
        { Tx = tx; Ty = ty; Stage = stage; Y = y; Cb = cb; Cr = cr; }
    }

    /// <summary>Fast content hash of tile (tx,ty) from the tight BGRA frame. FNV-1a over 8 bytes at a time;
    /// only the real (unpadded) extent of edge tiles is hashed.</summary>
    private static ulong HashTile(byte[] src, int tx, int ty, int width, int height)
    {
        int x0 = tx * 64, y0 = ty * 64;
        int tw = Math.Min(64, width - x0);
        int th = Math.Min(64, height - y0);
        ulong h = 0xcbf29ce484222325UL;
        int rowBytes = tw * 4;
        for (int row = 0; row < th; row++)
        {
            int p = ((y0 + row) * width + x0) * 4;
            int end = p + (rowBytes & ~7);
            while (p < end)
            {
                ulong word = BitConverter.ToUInt64(src, p);
                h = (h ^ word) * 0x100000001b3UL;
                p += 8;
            }
            int tail = p + (rowBytes & 7);
            while (p < tail) { h = (h ^ src[p]) * 0x100000001b3UL; p++; }
        }
        return h == 0 ? 1 : h;
    }

    // ── Tile extraction (rfx_encode_format_rgb, BGRX32 case) ─────────────────────────────────────
    private void ExtractTile(byte[] src, int tx, int ty, Scratch s)
    {
        int x0 = tx * 64, y0 = ty * 64;
        int tw = Math.Min(64, _width - x0);
        int th = Math.Min(64, _height - y0);
        var rp = s.PlaneR; var gp = s.PlaneG; var bp = s.PlaneB;
        for (int row = 0; row < th; row++)
        {
            int si = ((y0 + row) * _width + x0) * 4;
            int di = row * 64;
            for (int c = 0; c < tw; c++)
            {
                bp[di] = src[si];
                gp[di] = src[si + 1];
                rp[di] = src[si + 2];
                si += 4; di++;
            }
            if (tw < 64)
            {
                short r = rp[di - 1], g = gp[di - 1], b = bp[di - 1];
                for (int c = tw; c < 64; c++) { rp[di] = r; gp[di] = g; bp[di] = b; di++; }
            }
        }
        if (th < 64)
        {
            int lastRow = (th - 1) * 64;
            for (int row = th; row < 64; row++)
            {
                int d = row * 64;
                for (int i = 0; i < 64; i++) { rp[d + i] = rp[lastRow + i]; gp[d + i] = gp[lastRow + i]; bp[d + i] = bp[lastRow + i]; }
            }
        }
    }

    // ── Color transform (prim_colors.c general_RGBToYCbCr_16s16s_P3P3), output scaled <<5 ─────────
    private static void RgbToYCbCr(Scratch s)
    {
        var rp = s.PlaneR; var gp = s.PlaneG; var bp = s.PlaneB;
        var yp = s.PlaneY; var cbp = s.PlaneCb; var crp = s.PlaneCr;
        for (int i = 0; i < 4096; i++)
        {
            int r = rp[i], g = gp[i], b = bp[i];
            int cy = (r * 9798 + g * 19235 + b * 3735) >> 10;
            int cb = (r * -5535 + g * -10868 + b * 16403) >> 10;
            int cr = (r * 16377 + g * -13714 + b * -2663) >> 10;
            yp[i] = (short)Math.Clamp(cy - 4096, -4096, 4095);
            cbp[i] = (short)Math.Clamp(cb, -4096, 4095);
            crp[i] = (short)Math.Clamp(cr, -4096, 4095);
        }
    }

    // ── Per-component encode (rfx_encode_component) ──────────────────────────────────────────────
    private static byte[] EncodeComponent(short[] data, Scratch s, int[] progQuant)
    {
        Dwt2dEncode(data, s);
        QuantizationEncode(data, progQuant);
        DifferentialEncode(data);
        return Rlgr1Encode(data);
    }

    // ── Forward DWT (rfx_dwt_2d_encode) ──────────────────────────────────────────────────────────
    private static void Dwt2dEncode(short[] buffer, Scratch s)
    {
        DwtEncodeBlock(buffer, 0, 32, s);
        DwtEncodeBlock(buffer, 3072, 16, s);
        DwtEncodeBlock(buffer, 3840, 8, s);
    }

    private static void DwtEncodeBlock(short[] buf, int baseOff, int sw, Scratch s)
    {
        int total = sw << 1;
        var dwt = s.DwtTmp;
        // Vertical direction: 2 sub-bands (L, H) into the tmp buffer.
        for (int x = 0; x < total; x++)
        {
            for (int n = 0; n < sw; n++)
            {
                int y = n << 1;
                int l = n * total + x;
                int hh = l + sw * total;
                int src = baseOff + y * total + x;
                int nextEven = baseOff + (n < sw - 1 ? (y + 2) * total : y * total) + x;
                int hv = (buf[src + total] - ((buf[src] + buf[nextEven]) >> 1)) >> 1;
                dwt[hh] = unchecked((short)hv);
                int lv = buf[src] + (n == 0 ? dwt[hh] : (dwt[hh - total] + dwt[hh]) >> 1);
                dwt[l] = unchecked((short)lv);
            }
        }
        // Horizontal direction: 4 sub-bands HL(0), LH(1), HH(2), LL(3) back into buffer.
        int ll = baseOff + 3 * sw * sw;
        int hl = baseOff;
        int lSrc = 0;
        int lh = baseOff + sw * sw;
        int hhb = baseOff + 2 * sw * sw;
        int hSrc = 2 * sw * sw;
        for (int r = 0; r < sw; r++)
        {
            for (int n = 0; n < sw; n++)
            {
                int x = n << 1;
                int xn = n < sw - 1 ? x + 2 : x;
                int hlv = (dwt[lSrc + x + 1] - ((dwt[lSrc + x] + dwt[lSrc + xn]) >> 1)) >> 1;
                buf[hl + n] = unchecked((short)hlv);
                int llv = dwt[lSrc + x] + (n == 0 ? buf[hl + n] : (buf[hl + n - 1] + buf[hl + n]) >> 1);
                buf[ll + n] = unchecked((short)llv);
            }
            for (int n = 0; n < sw; n++)
            {
                int x = n << 1;
                int xn = n < sw - 1 ? x + 2 : x;
                int hhv = (dwt[hSrc + x + 1] - ((dwt[hSrc + x] + dwt[hSrc + xn]) >> 1)) >> 1;
                buf[hhb + n] = unchecked((short)hhv);
                int lhv = dwt[hSrc + x] + (n == 0 ? buf[hhb + n] : (buf[hhb + n - 1] + buf[hhb + n]) >> 1);
                buf[lh + n] = unchecked((short)lhv);
            }
            ll += sw; hl += sw; lSrc += total;
            lh += sw; hhb += sw; hSrc += total;
        }
    }

    // ── Quantization (rfx_quantization_encode) ───────────────────────────────────────────────────
    private static void QuantEncodeBlock(short[] buf, int baseOff, int count, int factor)
    {
        if (factor <= 0) return;
        int half = 1 << (factor - 1);
        for (int i = baseOff; i < baseOff + count; i++)
            buf[i] = unchecked((short)((buf[i] + half) >> factor));
    }

    /// <summary>Per-band (quant-6) rounding shift, then a global >>5 undoing the color-transform scale.
    /// <paramref name="progQuant"/> (RFX_COMPONENT_CODEC_QUANT band order HL1,LH1,HH1,HL2,LH2,HH2,HL3,LH3,
    /// HH3,LL3) adds an EXTRA per-band right shift — the progressive quality step, undone exactly on decode
    /// (the decoder dequant left-shift is baseQuant + progQuant - 1). All-zero == quality 100 == lossless.</summary>
    private static void QuantizationEncode(short[] buffer, int[] p)
    {
        var q = Quant;
        QuantEncodeBlock(buffer, 0, 1024, q[8] - 6 + p[0]);     // HL1
        QuantEncodeBlock(buffer, 1024, 1024, q[7] - 6 + p[1]);  // LH1
        QuantEncodeBlock(buffer, 2048, 1024, q[9] - 6 + p[2]);  // HH1
        QuantEncodeBlock(buffer, 3072, 256, q[5] - 6 + p[3]);   // HL2
        QuantEncodeBlock(buffer, 3328, 256, q[4] - 6 + p[4]);   // LH2
        QuantEncodeBlock(buffer, 3584, 256, q[6] - 6 + p[5]);   // HH2
        QuantEncodeBlock(buffer, 3840, 64, q[2] - 6 + p[6]);    // HL3
        QuantEncodeBlock(buffer, 3904, 64, q[1] - 6 + p[7]);    // LH3
        QuantEncodeBlock(buffer, 3968, 64, q[3] - 6 + p[8]);    // HH3
        QuantEncodeBlock(buffer, 4032, 64, q[0] - 6 + p[9]);    // LL3
        QuantEncodeBlock(buffer, 0, 4096, 5);                    // undo the <<5 YCbCr scale
    }

    private readonly struct ProgStage
    {
        public readonly byte Quality;
        public readonly int[] Prog;
        public ProgStage(byte quality, int[] prog) { Quality = quality; Prog = prog; }
    }

    /// <summary>Progressive quality stages (coarse→lossless). Band order [HL1,LH1,HH1, HL2,LH2,HH2, HL3,
    /// LH3,HH3, LL3]; values are extra per-band right-shifts (larger = coarser = smaller on wire). LL3
    /// (the DC/base) is never dropped; each stage is monotonically ≤ the previous; final is all-zero
    /// (lossless). Fields clamped 0…8 per [MS-RDPEGFX] 2.2.4.2.1.5.2.</summary>
    private static readonly ProgStage[] ProgStages =
    {
        new(50, new[] { 2, 2, 3, 1, 1, 2, 1, 1, 1, 0 }),
        new(75, new[] { 1, 1, 1, 1, 1, 1, 0, 0, 1, 0 }),
        new(100, new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),  // lossless — identical to SIMPLE
    };

    // ── LL3 differential encode (rfx_differential.h) ─────────────────────────────────────────────
    private static void DifferentialEncode(short[] buffer)
    {
        for (int i = 4032 + 63; i > 4032; i--)
            buffer[i] = unchecked((short)(buffer[i] - buffer[i - 1]));
    }

    // ── RLGR1 entropy encode (rfx_rlgr.c rfx_rlgr_encode) ────────────────────────────────────────
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new(2048);
        private byte _acc;
        private int _bitsLeft = 8;

        public void PutBits(uint pattern, int count)
        {
            int nbits = count;
            while (nbits > 0)
            {
                int b = Math.Min(nbits, _bitsLeft);
                _acc |= (byte)(((pattern >> (nbits - b)) & (uint)((1 << b) - 1)) << (_bitsLeft - b));
                _bitsLeft -= b;
                nbits -= b;
                if (_bitsLeft == 0) { _bytes.Add(_acc); _acc = 0; _bitsLeft = 8; }
            }
        }

        public void PutBit(uint bit, int count)
        {
            if (count <= 0) return;
            if (bit == 0)
            {
                int n = count;
                while (n > 0)
                {
                    int b = Math.Min(n, _bitsLeft);
                    _bitsLeft -= b; n -= b;
                    if (_bitsLeft == 0) { _bytes.Add(_acc); _acc = 0; _bitsLeft = 8; }
                }
                return;
            }
            int nn = count;
            if (_bitsLeft < 8)
            {
                int b = Math.Min(nn, _bitsLeft);
                _acc |= (byte)(((1 << b) - 1) << (_bitsLeft - b));
                _bitsLeft -= b; nn -= b;
                if (_bitsLeft == 0) { _bytes.Add(_acc); _acc = 0; _bitsLeft = 8; }
            }
            while (nn >= 8) { _bytes.Add(0xFF); nn -= 8; }
            if (nn > 0) { _acc = (byte)(((1 << nn) - 1) << (8 - nn)); _bitsLeft = 8 - nn; }
        }

        public byte[] Flush()
        {
            if (_bitsLeft != 8) { _bytes.Add(_acc); _acc = 0; _bitsLeft = 8; }
            return _bytes.ToArray();
        }
    }

    private const uint KPMAX = 80;
    private const int LSGR = 3;
    private const int UP_GR = 4, DN_GR = 6, UQ_GR = 3, DQ_GR = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint UpdateParam(ref uint param, int delta)
    {
        if (delta < 0) { uint d = (uint)(-delta); param = d > param ? 0 : param - d; }
        else param += (uint)delta;
        if (param > KPMAX) param = KPMAX;
        return param >> LSGR;
    }

    private static void CodeGR(BitWriter bs, ref uint krp, uint val)
    {
        int kr = (int)(krp >> LSGR);
        uint vk = val >> kr;
        bs.PutBit(1, (int)vk);
        bs.PutBits(0, 1);
        if (kr > 0) bs.PutBits(val & (uint)((1 << kr) - 1), kr);
        if (vk == 0) UpdateParam(ref krp, -2);
        else if (vk > 1) UpdateParam(ref krp, (int)vk);
    }

    private static byte[] Rlgr1Encode(short[] data)
    {
        var bs = new BitWriter();
        uint k = 1;
        uint kp = 1 << LSGR;
        uint krp = 1 << LSGR;
        int idx = 0;
        int count = data.Length;

        int Next() => idx < count ? data[idx++] : 0;

        while (idx < count)
        {
            if (k != 0)
            {
                // RUN-LENGTH mode: collect the zero run, then the terminating value.
                uint numZeros = 0;
                int input = Next();
                while (input == 0 && idx < count) { numZeros++; input = Next(); }

                uint runmax = 1u << (int)k;
                while (numZeros >= runmax)
                {
                    bs.PutBits(0, 1);
                    numZeros -= runmax;
                    k = UpdateParam(ref kp, UP_GR);
                    runmax = 1u << (int)k;
                }
                bs.PutBits(1, 1);
                if (k > 0) bs.PutBits(numZeros, (int)k);

                uint mag = (uint)(input < 0 ? -input : input);
                bs.PutBits(input < 0 ? 1u : 0u, 1);
                CodeGR(bs, ref krp, mag > 0 ? mag - 1 : 0);
                k = UpdateParam(ref kp, -DN_GR);
            }
            else
            {
                // GOLOMB-RICE mode (RLGR1): code (2*mag - sign).
                int input = Next();
                uint twoMs = input >= 0 ? (uint)(2 * input) : (uint)(-2 * input - 1);
                CodeGR(bs, ref krp, twoMs);
                if (twoMs != 0) k = UpdateParam(ref kp, -DQ_GR);
                else k = UpdateParam(ref kp, UQ_GR);
            }
        }
        return bs.Flush();
    }

    // ── Wire framing (rfx_write_message_progressive_simple) ──────────────────────────────────────
    // Bytes every stream spends before any tile: SYNC(12) + CONTEXT(10) + FRAME_BEGIN(12) + REGION
    // header(18) + one base quant table(5) + prog-quant tables (numProgQuant × 16) + FRAME_END(6).
    private const int ProgQuantTableBytes = 1 + 3 * 5;
    private static readonly int StreamFixedOverhead = 12 + 10 + 12 + 18 + 5 + ProgStages.Length * ProgQuantTableBytes + 6;

    private byte[] WriteStream(List<Tile> tiles)
    {
        var w = new W();

        // SYNC (blockLen 12): magic + version 1.0
        w.U16le(WBT_SYNC); w.U32le(12);
        w.U32le(0xCACCACCA); w.U16le(0x0100);

        // CONTEXT (blockLen 10): ctxId 0, tileSize 64, flags 0 (no subband diffing)
        w.U16le(WBT_CONTEXT); w.U32le(10);
        w.U8(0); w.U16le(64); w.U8(0);

        // FRAME_BEGIN (blockLen 12): frameIndex + regionCount 1
        w.U16le(WBT_FRAME_BEGIN); w.U32le(12);
        w.U32le(_frameIndex); w.U16le(1);

        int numProg = ProgStages.Length;
        int tilesDataSize = 0;
        foreach (var t in tiles) tilesDataSize += 23 + t.Y.Length + t.Cb.Length + t.Cr.Length;
        int blockLen = 18 + tiles.Count * 8 + 5 + numProg * ProgQuantTableBytes + tilesDataSize;

        w.U16le(WBT_REGION); w.U32le((uint)blockLen);
        w.U8(64);                          // tileSize
        w.U16le((ushort)tiles.Count);      // numRects
        w.U8(1);                           // numQuant
        w.U8((byte)numProg);               // numProgQuant
        w.U8(0);                           // flags (no REDUCE_EXTRAPOLATE)
        w.U16le((ushort)tiles.Count);      // numTiles
        w.U32le((uint)tilesDataSize);      // tilesDataSize

        foreach (var t in tiles)
        {
            int x = t.Tx * 64, y = t.Ty * 64;
            w.U16le((ushort)x); w.U16le((ushort)y);
            w.U16le((ushort)Math.Min(64, _width - x)); w.U16le((ushort)Math.Min(64, _height - y));
        }

        // Base quant table in RDPEGFX band packing (rfx.c: LL3/HL3, LH3/HH3, HL2/LH2, HH2/HL1, LH1/HH1).
        var q = Quant;
        w.U8((byte)(q[0] | (q[2] << 4)));
        w.U8((byte)(q[1] | (q[3] << 4)));
        w.U8((byte)(q[5] | (q[4] << 4)));
        w.U8((byte)(q[6] | (q[8] << 4)));
        w.U8((byte)(q[7] | (q[9] << 4)));

        // Prog-quant tables: one RFX_PROGRESSIVE_CODEC_QUANT per stage (quality byte + Y/Cb/Cr comps).
        foreach (var stage in ProgStages)
        {
            w.U8(stage.Quality);
            for (int c = 0; c < 3; c++) WriteComponentQuant(w, stage.Prog);
        }

        foreach (var t in tiles)
        {
            int tileLen = 23 + t.Y.Length + t.Cb.Length + t.Cr.Length;
            w.U16le(WBT_TILE_FIRST); w.U32le((uint)tileLen);
            w.U8(0); w.U8(0); w.U8(0);        // quantIdx Y/Cb/Cr (base table 0)
            w.U16le((ushort)t.Tx); w.U16le((ushort)t.Ty);
            w.U8(0);                          // flags (not a difference tile)
            w.U8((byte)t.Stage);              // progressiveQuality (index into prog-quant array)
            w.U16le((ushort)t.Y.Length); w.U16le((ushort)t.Cb.Length); w.U16le((ushort)t.Cr.Length);
            w.U16le(0);                       // tailLen
            w.Bytes(t.Y); w.Bytes(t.Cb); w.Bytes(t.Cr);
        }

        // FRAME_END (blockLen 6)
        w.U16le(WBT_FRAME_END); w.U32le(6);
        return w.ToArray();
    }

    /// <summary>Pack one 5-byte RFX_COMPONENT_CODEC_QUANT from a prog-quant array in band order
    /// [HL1,LH1,HH1,HL2,LH2,HH2,HL3,LH3,HH3,LL3] (indices 0…9), matching the decoder's nibble layout.</summary>
    private static void WriteComponentQuant(W w, int[] p)
    {
        byte Nib(int i) => (byte)(p[i] & 0x0F);
        w.U8((byte)(Nib(9) | (Nib(6) << 4)));   // LL3 | HL3
        w.U8((byte)(Nib(7) | (Nib(8) << 4)));   // LH3 | HH3
        w.U8((byte)(Nib(3) | (Nib(4) << 4)));   // HL2 | LH2
        w.U8((byte)(Nib(5) | (Nib(0) << 4)));   // HH2 | HL1
        w.U8((byte)(Nib(1) | (Nib(2) << 4)));   // LH1 | HH1
    }
}
