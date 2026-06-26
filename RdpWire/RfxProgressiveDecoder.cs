// RemoteFX Progressive Codec (CAPROGRESSIVE / RDPGFX_CODECID_CAPROGRESSIVE 0x0009) decoder.
//
// Server-side twin of wwwroot/lib/rdpweb/progressive.js (the browser decoder) so the session recorder
// can decode GNOME Remote Desktop's progressive desktop stream, composite full frames, and re-encode
// them to H.264. It is a literal port of FreeRDP libfreerdp/codec (rfx_rlgr.c, rfx_dwt.c,
// rfx_differential.h, progressive.c, prim_colors.c). Only SIMPLE/FIRST tiles are decoded (GNOME RD
// never sends progressive UPGRADE passes); UPGRADE blocks are skipped.
//
// All math is fixed-point int16/int32 matching FreeRDP exactly. See progressive.js for the annotated
// reference; this file mirrors it 1:1 (including the lzcnt-via-LeadingZeroCount fix that the JS port
// needed — the C# BitOperations.LeadingZeroCount is exact).

using System;
using System.Collections.Generic;
using System.Numerics;

namespace KSol.RDPGateway.RDP;

internal sealed class RfxProgressiveDecoder
{
    // block types (progressive.h PROGRESSIVE_WBT_*)
    private const ushort WBT_SYNC = 0xCCC0, WBT_FRAME_BEGIN = 0xCCC1, WBT_FRAME_END = 0xCCC2;
    private const ushort WBT_CONTEXT = 0xCCC3, WBT_REGION = 0xCCC4;
    private const ushort WBT_TILE_SIMPLE = 0xCCC5, WBT_TILE_FIRST = 0xCCC6, WBT_TILE_UPGRADE = 0xCCC7;

    private const byte RFX_SUBBAND_DIFFING = 0x01;        // context flags
    private const byte RFX_TILE_DIFFERENCE = 0x01;        // tile flags
    private const byte RFX_DWT_REDUCE_EXTRAPOLATE = 0x01; // region flags

    private const int KPMAX = 80, LSGR = 3, UP_GR = 4, DN_GR = 6, UQ_GR = 3, DQ_GR = 3;

    // Per-surface persistent state: the "current" coefficient grids per tile cell (for RFX_TILE_DIFFERENCE)
    // and reusable scratch buffers. One decoder instance is kept per surfaceId by the caller.
    private readonly short[] _scratchY = new short[4096];
    private readonly short[] _scratchCb = new short[4096];
    private readonly short[] _scratchCr = new short[4096];
    private readonly short[] _signY = new short[4096];
    private readonly short[] _signCb = new short[4096];
    private readonly short[] _signCr = new short[4096];
    private readonly short[] _idwt = new short[4096 * 2 + 64];
    private readonly byte[] _rgba = new byte[64 * 64 * 4]; // BGRA per tile
    private readonly Dictionary<int, short[][]> _tiles = new(); // cellKey -> [curY,curCb,curCr]

    public void Reset() => _tiles.Clear();

    private short[][] TileCell(int xIdx, int yIdx)
    {
        int key = (yIdx << 16) | (xIdx & 0xFFFF);
        if (!_tiles.TryGetValue(key, out var c))
        {
            c = new[] { new short[4096], new short[4096], new short[4096] };
            _tiles[key] = c;
        }
        return c;
    }

    private static short Clampi16(int v) => v < -32768 ? (short)-32768 : (v > 32767 ? (short)32767 : (short)v);
    private static byte Clip8(int v) => v < 0 ? (byte)0 : (v > 255 ? (byte)255 : (byte)v);

    // ---------------------------------------------------------------------------------------------
    // MSB-first bit reader (winpr wBitStream equivalent; see progressive.js BitStream).
    // ---------------------------------------------------------------------------------------------
    private sealed class BitStream
    {
        private readonly byte[] _buf;
        private readonly int _off, _cap;
        public readonly int Length; // total bits
        public int BitPos;
        public uint Accumulator;

        public BitStream(byte[] buf, int off, int len)
        {
            _buf = buf; _off = off; _cap = len; Length = len * 8; BitPos = 0; Reload();
        }
        private uint BitsAt(int p)
        {
            int bytePos = p >> 3, bitOff = p & 7;
            byte b0 = bytePos < _cap ? _buf[_off + bytePos] : (byte)0;
            byte b1 = bytePos + 1 < _cap ? _buf[_off + bytePos + 1] : (byte)0;
            byte b2 = bytePos + 2 < _cap ? _buf[_off + bytePos + 2] : (byte)0;
            byte b3 = bytePos + 3 < _cap ? _buf[_off + bytePos + 3] : (byte)0;
            byte b4 = bytePos + 4 < _cap ? _buf[_off + bytePos + 4] : (byte)0;
            uint hi = ((uint)b0 << 24) | ((uint)b1 << 16) | ((uint)b2 << 8) | b3;
            if (bitOff == 0) return hi;
            return (hi << bitOff) | ((uint)b4 >> (8 - bitOff));
        }
        private void Reload() => Accumulator = BitsAt(BitPos);
        public int Remaining() { int r = Length - BitPos; return r < 0 ? 0 : r; }
        public void Shift(int n) { if (n <= 0) return; if (n > 32) n = 32; BitPos += n; Reload(); }
        public void Shift32() { BitPos += 32; Reload(); }
    }

    private static int Lzcnt(uint x) => BitOperations.LeadingZeroCount(x); // exact, 32 for x==0

    // RLGR decode (rfx_rlgr.c). mode 1=RLGR1, 3=RLGR3. Writes count int16 into outBuf.
    private static void RlgrDecode(int mode, byte[] src, int srcOff, int srcLen, short[] outBuf, int count)
    {
        Array.Clear(outBuf, 0, count);
        if (mode != 1 && mode != 3) mode = 1;
        if (srcLen <= 0) return;

        var bs = new BitStream(src, srcOff, srcLen);
        int k = 1, kp = 1 << LSGR, kr = 1, krp = 1 << LSGR, idx = 0;

        while (bs.Remaining() > 0 && idx < count)
        {
            if (k != 0)
            {
                // Run-Length mode
                int run = 0;
                int cnt = Lzcnt(bs.Accumulator);
                int nbits = bs.Remaining();
                if (cnt > nbits) cnt = nbits;
                int vk = cnt;
                while (cnt == 32 && bs.Remaining() > 0)
                {
                    bs.Shift32();
                    cnt = Lzcnt(bs.Accumulator);
                    nbits = bs.Remaining(); if (cnt > nbits) cnt = nbits;
                    vk += cnt;
                }
                bs.Shift(vk % 32);
                if (bs.Remaining() < 1) break;
                bs.Shift(1);

                while (vk-- > 0) { run += (1 << k); kp += UP_GR; if (kp > KPMAX) kp = KPMAX; k = kp >> LSGR; }

                if (bs.Remaining() < k) break;
                int code = 0;
                if (k > 0) { code = (int)((bs.Accumulator >> (32 - k)) & (uint)((1 << k) - 1)); bs.Shift(k); }
                run += code;

                if (bs.Remaining() < 1) break;
                int sign = (bs.Accumulator & 0x80000000u) != 0 ? 1 : 0;
                bs.Shift(1);

                cnt = Lzcnt(~bs.Accumulator);
                nbits = bs.Remaining(); if (cnt > nbits) cnt = nbits;
                vk = cnt;
                while (cnt == 32 && bs.Remaining() > 0)
                {
                    bs.Shift32();
                    cnt = Lzcnt(~bs.Accumulator);
                    nbits = bs.Remaining(); if (cnt > nbits) cnt = nbits;
                    vk += cnt;
                }
                bs.Shift(vk % 32);
                if (bs.Remaining() < 1) break;
                bs.Shift(1);

                if (bs.Remaining() < kr) break;
                int c2 = 0;
                if (kr > 0) { c2 = (int)((bs.Accumulator >> (32 - kr)) & (uint)((1 << kr) - 1)); bs.Shift(kr); }
                c2 |= (vk << kr);

                if (vk == 0) { krp = krp > 2 ? krp - 2 : 0; kr = krp >> LSGR; }
                else if (vk != 1) { krp += vk; if (krp > KPMAX) krp = KPMAX; kr = krp >> LSGR; }

                kp = kp > DN_GR ? kp - DN_GR : 0; k = kp >> LSGR;

                int mag = sign != 0 ? -(c2 + 1) : (c2 + 1);
                int rem = count - idx, z = run; if (z > rem) z = rem;
                for (int i = 0; i < z; i++) outBuf[idx++] = 0;
                if (idx < count) outBuf[idx++] = Clampi16(mag);
            }
            else
            {
                // Golomb-Rice mode
                int gcnt = Lzcnt(~bs.Accumulator);
                int gnb = bs.Remaining(); if (gcnt > gnb) gcnt = gnb;
                int gvk = gcnt;
                while (gcnt == 32 && bs.Remaining() > 0)
                {
                    bs.Shift32();
                    gcnt = Lzcnt(~bs.Accumulator);
                    gnb = bs.Remaining(); if (gcnt > gnb) gcnt = gnb;
                    gvk += gcnt;
                }
                bs.Shift(gvk % 32);
                if (bs.Remaining() < 1) break;
                bs.Shift(1);

                if (bs.Remaining() < kr) break;
                int gcode = 0;
                if (kr > 0) { gcode = (int)((bs.Accumulator >> (32 - kr)) & (uint)((1 << kr) - 1)); bs.Shift(kr); }
                gcode |= (gvk << kr);

                if (gvk == 0) { krp = krp > 2 ? krp - 2 : 0; kr = krp >> LSGR; }
                else if (gvk != 1) { krp += gvk; if (krp > KPMAX) krp = KPMAX; kr = krp >> LSGR; }

                if (mode == 1)
                {
                    int gmag;
                    if (gcode == 0) { kp += UQ_GR; if (kp > KPMAX) kp = KPMAX; k = kp >> LSGR; gmag = 0; }
                    else { kp = kp > DQ_GR ? kp - DQ_GR : 0; k = kp >> LSGR; gmag = (gcode & 1) != 0 ? -((gcode + 1) >> 1) : (gcode >> 1); }
                    if (idx < count) outBuf[idx++] = Clampi16(gmag);
                }
                else
                {
                    int nIdx = 0, mg = 0;
                    if (gcode != 0) { mg = gcode; nIdx = 32 - Lzcnt((uint)mg); }
                    if (bs.Remaining() < nIdx) break;
                    int val1 = 0;
                    if (nIdx > 0) { val1 = (int)((bs.Accumulator >> (32 - nIdx)) & (uint)((1 << nIdx) - 1)); bs.Shift(nIdx); }
                    int val2 = gcode - val1;
                    if (val1 != 0 && val2 != 0) { kp = kp > 2 * DQ_GR ? kp - 2 * DQ_GR : 0; k = kp >> LSGR; }
                    else if (val1 == 0 && val2 == 0) { kp += 2 * UQ_GR; if (kp > KPMAX) kp = KPMAX; k = kp >> LSGR; }
                    int m1 = (val1 & 1) != 0 ? -((val1 + 1) >> 1) : (val1 >> 1);
                    if (idx < count) outBuf[idx++] = Clampi16(m1);
                    int m2 = (val2 & 1) != 0 ? -((val2 + 1) >> 1) : (val2 >> 1);
                    if (idx < count) outBuf[idx++] = Clampi16(m2);
                }
            }
        }
        // remainder already zeroed by Array.Clear
    }

    private static void DifferentialDecode(short[] buf, int start, int size)
    {
        for (int i = 0; i < size - 1; i++) buf[start + i + 1] = Clampi16(buf[start + i] + buf[start + i + 1]);
    }

    private static void LShift(short[] buf, int b, int len, int sh)
    {
        if (sh == 0) return;
        for (int i = 0; i < len; i++) buf[b + i] = Clampi16(buf[b + i] << sh);
    }

    // Inverse 2D DWT block (rfx_dwt.c rfx_dwt_2d_decode_block) operating on buffer at base offset.
    private static void DwtBlockAt(short[] buffer, int bbase, short[] idwt, int sw)
    {
        int total = sw << 1, sw2 = sw * sw;
        int ll = bbase + sw2 * 3, hl = bbase, lh = bbase + sw2, hh = bbase + sw2 * 2;
        int lDst = 0, hDst = sw2 * 2;
        int n, x;
        for (int y = 0; y < sw; y++)
        {
            idwt[lDst] = Clampi16(buffer[ll] - ((buffer[hl] + buffer[hl] + 1) >> 1));
            idwt[hDst] = Clampi16(buffer[lh] - ((buffer[hh] + buffer[hh] + 1) >> 1));
            for (n = 1; n < sw; n++)
            {
                x = n << 1;
                idwt[lDst + x] = Clampi16(buffer[ll + n] - ((buffer[hl + n - 1] + buffer[hl + n] + 1) >> 1));
                idwt[hDst + x] = Clampi16(buffer[lh + n] - ((buffer[hh + n - 1] + buffer[hh + n] + 1) >> 1));
            }
            for (n = 0; n < sw - 1; n++)
            {
                x = n << 1;
                idwt[lDst + x + 1] = Clampi16((buffer[hl + n] << 1) + ((idwt[lDst + x] + idwt[lDst + x + 2]) >> 1));
                idwt[hDst + x + 1] = Clampi16((buffer[hh + n] << 1) + ((idwt[hDst + x] + idwt[hDst + x + 2]) >> 1));
            }
            x = n << 1;
            idwt[lDst + x + 1] = Clampi16((buffer[hl + n] << 1) + idwt[lDst + x]);
            idwt[hDst + x + 1] = Clampi16((buffer[hh + n] << 1) + idwt[hDst + x]);
            ll += sw; hl += sw; lh += sw; hh += sw; lDst += total; hDst += total;
        }
        for (x = 0; x < total; x++)
        {
            int lIdx = x, hIdx = x + sw * total, dst = bbase + x;
            buffer[dst] = Clampi16(idwt[lIdx] - ((idwt[hIdx] * 2 + 1) >> 1));
            for (n = 1; n < sw; n++)
            {
                lIdx += total; hIdx += total;
                buffer[dst + 2 * total] = Clampi16(idwt[lIdx] - ((idwt[hIdx - total] + idwt[hIdx] + 1) >> 1));
                buffer[dst + total] = Clampi16((idwt[hIdx - total] << 1) + ((buffer[dst] + buffer[dst + 2 * total]) >> 1));
                dst += 2 * total;
            }
            buffer[dst + total] = Clampi16((idwt[hIdx] << 1) + ((buffer[dst] * 2) >> 1));
        }
    }

    // Per-band dequant shifts {LL3,HL3,LH3,HH3,HL2,LH2,HH2,HL1,LH1,HH1}.
    private struct Quant
    {
        public int LL3, HL3, LH3, HH3, HL2, LH2, HH2, HL1, LH1, HH1;
    }
    private static Quant ReadQuant(byte[] d, int o)
    {
        byte b0 = d[o], b1 = d[o + 1], b2 = d[o + 2], b3 = d[o + 3], b4 = d[o + 4];
        return new Quant
        {
            LL3 = b0 & 0x0F, HL3 = b0 >> 4,
            LH3 = b1 & 0x0F, HH3 = b1 >> 4,
            HL2 = b2 & 0x0F, LH2 = b2 >> 4,
            HH2 = b3 & 0x0F, HL1 = b3 >> 4,
            LH1 = b4 & 0x0F, HH1 = b4 >> 4,
        };
    }
    // shift = quant - 1 per band (quantProg is all-zeros for SIMPLE/quality 0xFF).
    private static Quant ToShift(in Quant q) => new Quant
    {
        LL3 = q.LL3 - 1, HL3 = q.HL3 - 1, LH3 = q.LH3 - 1, HH3 = q.HH3 - 1,
        HL2 = q.HL2 - 1, LH2 = q.LH2 - 1, HH2 = q.HH2 - 1,
        HL1 = q.HL1 - 1, LH1 = q.LH1 - 1, HH1 = q.HH1 - 1,
    };

    private void DecodeComponent(byte[] data, int dataOff, int dataLen, short[] srcDst, short[] current,
                                 short[] sign, in Quant shift, bool coeffDiff)
    {
        RlgrDecode(1, data, dataOff, dataLen, srcDst, 4096);
        Array.Copy(srcDst, sign, 4096);

        DifferentialDecode(srcDst, 4032, 64);
        LShift(srcDst, 0, 1024, shift.HL1);
        LShift(srcDst, 1024, 1024, shift.LH1);
        LShift(srcDst, 2048, 1024, shift.HH1);
        LShift(srcDst, 3072, 256, shift.HL2);
        LShift(srcDst, 3328, 256, shift.LH2);
        LShift(srcDst, 3584, 256, shift.HH2);
        LShift(srcDst, 3840, 64, shift.HL3);
        LShift(srcDst, 3904, 64, shift.LH3);
        LShift(srcDst, 3968, 64, shift.HH3);
        LShift(srcDst, 4032, 64, shift.LL3);

        // coeffDiff: add 'current' into the buffer AND store the summed result back into 'current'
        // (FreeRDP add_16s_inplace writes BOTH operands) so successive difference tiles accumulate
        // correctly; otherwise 'current' goes stale and later frames decode to grey.
        if (coeffDiff) { for (int i = 0; i < 4096; i++) { short v = Clampi16(srcDst[i] + current[i]); srcDst[i] = v; current[i] = v; } }
        else Array.Copy(srcDst, current, 4096);

        DwtBlockAt(srcDst, 3840, _idwt, 8);
        DwtBlockAt(srcDst, 3072, _idwt, 16);
        DwtBlockAt(srcDst, 0, _idwt, 32);
    }

    private static readonly int C_CrR = (int)Math.Round(1.402525 * 65536);
    private static readonly int C_CrG = (int)Math.Round(0.714401 * 65536);
    private static readonly int C_CbG = (int)Math.Round(0.343730 * 65536);
    private static readonly int C_CbB = (int)Math.Round(1.769905 * 65536);
    private void YCbCrToBgra(short[] y, short[] cb, short[] cr)
    {
        for (int i = 0; i < 4096; i++)
        {
            int Y = (y[i] + 4096) << 16;
            int Cb = cb[i], Cr = cr[i];
            int R = ((Cr * C_CrR + Y) >> 16) >> 5;
            int G = ((Y - Cb * C_CbG - Cr * C_CrG) >> 16) >> 5;
            int B = ((Cb * C_CbB + Y) >> 16) >> 5;
            int o = i << 2;
            _rgba[o] = Clip8(B); _rgba[o + 1] = Clip8(G); _rgba[o + 2] = Clip8(R); _rgba[o + 3] = 255;
        }
    }

    /// <summary>
    /// Decode a WIRE_TO_SURFACE_2 progressive payload. For each reconstructed 64x64 tile, calls
    /// onTile(xIdx, yIdx, bgra) with a 64x64x4 BGRA buffer (reused — copy if retained). Returns the
    /// number of tiles decoded, or -1 on parse failure.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> payloadSpan, Action<int, int, byte[]> onTile)
    {
        // Copy to a byte[] so the BitStream can index past the end with zero-padding.
        var data = payloadSpan.ToArray();
        int len = data.Length;
        int pos = FindBlockStart(data, len);
        if (pos < 0) return -1;

        int tiles = 0;
        bool haveRegion = false;
        byte contextFlags = 0;
        Quant[] quants = Array.Empty<Quant>();
        int numQuant = 0;
        byte regionFlags = 0;
        int tileDataStart = 0, tileDataSize = 0;

        ushort U16(int o) => (ushort)(data[o] | (data[o + 1] << 8));
        uint U32(int o) => (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24));

        while (pos + 6 <= len)
        {
            ushort blockType = U16(pos);
            uint blockLen = U32(pos + 2);
            if (blockLen < 6 || pos + (long)blockLen > len) break;
            int blockEnd = pos + (int)blockLen;
            int bodyOff = pos + 6;

            switch (blockType)
            {
                case WBT_SYNC:
                case WBT_FRAME_BEGIN:
                case WBT_FRAME_END:
                    break;
                case WBT_CONTEXT:
                    // ctxId(1) tileSize(2) flags(1)
                    if (bodyOff + 4 <= blockEnd) contextFlags = data[bodyOff + 3];
                    break;
                case WBT_REGION:
                    if (!ParseRegion(data, bodyOff, blockEnd, out regionFlags, out numQuant, out quants,
                                     out tileDataStart, out tileDataSize)) return tiles;
                    haveRegion = true;
                    tiles += ProcessTiles(data, regionFlags, contextFlags, numQuant, quants,
                                          tileDataStart, tileDataSize, onTile);
                    break;
            }
            pos = blockEnd;
        }
        return haveRegion ? tiles : 0;
    }

    private static int FindBlockStart(byte[] data, int len)
    {
        for (int off = 0; off <= 16 && off + 6 <= len; off++)
        {
            ushort bt = (ushort)(data[off] | (data[off + 1] << 8));
            if (bt != WBT_SYNC && bt != WBT_FRAME_BEGIN && bt != WBT_CONTEXT && bt != WBT_REGION && bt != WBT_FRAME_END)
                continue;
            uint bl = (uint)(data[off + 2] | (data[off + 3] << 8) | (data[off + 4] << 16) | (data[off + 5] << 24));
            if (bl < 6 || off + (long)bl > len) continue;
            if (bt == WBT_SYNC && bl != 12) continue;
            return off;
        }
        return -1;
    }

    private static bool ParseRegion(byte[] data, int start, int end, out byte flags, out int numQuant,
                                    out Quant[] quants, out int tileDataStart, out int tileDataSize)
    {
        flags = 0; numQuant = 0; quants = Array.Empty<Quant>(); tileDataStart = 0; tileDataSize = 0;
        int p = start;
        if (p + 11 > end) return false;
        byte tileSize = data[p++];
        ushort numRects = (ushort)(data[p] | (data[p + 1] << 8)); p += 2;
        byte nQuant = data[p++];
        byte numProgQuant = data[p++];
        flags = data[p++];
        ushort numTiles = (ushort)(data[p] | (data[p + 1] << 8)); p += 2;
        uint tds = (uint)(data[p] | (data[p + 1] << 8) | (data[p + 2] << 16) | (data[p + 3] << 24)); p += 4;
        if (tileSize != 64 || numRects < 1) return false;

        p += numRects * 8; // skip rects (x,y,w,h each u16)
        if (p > end) return false;
        var q = new Quant[nQuant];
        for (int i = 0; i < nQuant; i++) { if (p + 5 > end) return false; q[i] = ReadQuant(data, p); p += 5; }
        p += numProgQuant * 16; // skip prog quant (quality(1)+3*5)
        if (p > end) return false;

        numQuant = nQuant; quants = q; tileDataStart = p; tileDataSize = (int)tds;
        return true;
    }

    private int ProcessTiles(byte[] data, byte regionFlags, byte contextFlags, int numQuant, Quant[] quants,
                             int tileDataStart, int tileDataSize, Action<int, int, byte[]> onTile)
    {
        int p = tileDataStart;
        int end = tileDataStart + tileDataSize;
        if (end > data.Length) end = data.Length;
        int count = 0;

        ushort U16(int o) => (ushort)(data[o] | (data[o + 1] << 8));
        uint U32(int o) => (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24));

        while (p + 6 <= end)
        {
            ushort blockType = U16(p);
            uint blockLen = U32(p + 2);
            if (blockLen < 6) break;
            int bEnd = p + (int)blockLen;
            if (bEnd > end) break;
            int o = p + 6;

            if (blockType == WBT_TILE_SIMPLE || blockType == WBT_TILE_FIRST)
            {
                bool simple = blockType == WBT_TILE_SIMPLE;
                if (o + (simple ? 16 : 17) > bEnd) { p = bEnd; continue; }
                byte quantIdxY = data[o++], quantIdxCb = data[o++], quantIdxCr = data[o++];
                ushort xIdx = U16(o); o += 2;
                ushort yIdx = U16(o); o += 2;
                byte tflags = data[o++];
                if (!simple) o++; // quality
                ushort yLen = U16(o); o += 2;
                ushort cbLen = U16(o); o += 2;
                ushort crLen = U16(o); o += 2;
                ushort tailLen = U16(o); o += 2;
                int yData = o; o += yLen;
                int cbData = o; o += cbLen;
                int crData = o; o += crLen;
                // tail skipped

                if (quantIdxY < numQuant && quantIdxCb < numQuant && quantIdxCr < numQuant &&
                    o <= bEnd && crData + crLen <= data.Length)
                {
                    ReconstructTile(data, regionFlags, contextFlags, xIdx, yIdx, tflags,
                                    quants[quantIdxY], quants[quantIdxCb], quants[quantIdxCr],
                                    yData, yLen, cbData, cbLen, crData, crLen, onTile);
                    count++;
                }
            }
            // WBT_TILE_UPGRADE: skipped (GNOME RD doesn't send it)
            p = bEnd;
        }
        return count;
    }

    private void ReconstructTile(byte[] data, byte regionFlags, byte contextFlags, int xIdx, int yIdx, byte tflags,
                                 in Quant qY, in Quant qCb, in Quant qCr,
                                 int yOff, int yLen, int cbOff, int cbLen, int crOff, int crLen,
                                 Action<int, int, byte[]> onTile)
    {
        bool coeffDiff = (tflags & RFX_TILE_DIFFERENCE) != 0;
        Quant shY = ToShift(qY), shCb = ToShift(qCb), shCr = ToShift(qCr);
        var cell = TileCell(xIdx, yIdx);

        DecodeComponent(data, yOff, yLen, _scratchY, cell[0], _signY, shY, coeffDiff);
        DecodeComponent(data, cbOff, cbLen, _scratchCb, cell[1], _signCb, shCb, coeffDiff);
        DecodeComponent(data, crOff, crLen, _scratchCr, cell[2], _signCr, shCr, coeffDiff);

        YCbCrToBgra(_scratchY, _scratchCb, _scratchCr);
        onTile(xIdx, yIdx, _rgba);
    }
}
