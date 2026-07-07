// RemoteFX Progressive Codec (CAPROGRESSIVE / RDPGFX_CODECID_CAPROGRESSIVE 0x0009) decoder.
//
// Server-side twin of wwwroot/lib/rdpweb/progressive.js (the browser decoder) so the session recorder
// can decode a host's progressive desktop stream, composite full frames, and re-encode them to H.264.
// It is a literal port of FreeRDP libfreerdp/codec (rfx_rlgr.c, rfx_dwt.c, rfx_differential.h,
// progressive.c, prim_colors.c). Supports both host encoder profiles:
//   - GNOME RD: SIMPLE tiles, non-extrapolate DWT (regionFlags=0), no UPGRADE passes.
//   - Windows RDS: FIRST tiles at reduced quality + UPGRADE (SRL/RAW) refinement passes with the
//     REDUCE_EXTRAPOLATE region flag (different subband geometry AND a different inverse DWT).
//
// All math is fixed-point int16/int32 matching FreeRDP exactly. See progressive.js for the annotated
// reference; this file mirrors it 1:1 (including the lzcnt-via-LeadingZeroCount fix that the JS port
// needed — the C# BitOperations.LeadingZeroCount is exact).

using System;
using System.Collections.Generic;
using System.Numerics;

namespace KSol.ZeroVDI.RDP;

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

    // Per-surface persistent state: the "current"/"sign" coefficient grids and bit positions per tile
    // cell (for RFX_TILE_DIFFERENCE and UPGRADE passes) plus reusable scratch buffers. One decoder
    // instance is kept per surfaceId by the caller.
    private sealed class Cell
    {
        public readonly short[][] Cur = { new short[4096], new short[4096], new short[4096] };
        public readonly short[][] Sign = { new short[4096], new short[4096], new short[4096] };
        public Quant[]? BitPos; // [Y,Cb,Cr], set by FIRST/SIMPLE, consumed by UPGRADE
    }

    private readonly short[] _scratchY = new short[4096];
    private readonly short[] _scratchCb = new short[4096];
    private readonly short[] _scratchCr = new short[4096];
    private readonly short[] _idwt = new short[4096 * 2 + 64];
    private readonly byte[] _rgba = new byte[64 * 64 * 4]; // BGRA per tile
    private readonly Dictionary<int, Cell> _tiles = new();

    public void Reset() => _tiles.Clear();

    private Cell TileCell(int xIdx, int yIdx)
    {
        int key = (yIdx << 16) | (xIdx & 0xFFFF);
        if (!_tiles.TryGetValue(key, out var c))
        {
            c = new Cell();
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

    // ---------------------------------------------------------------------------------------------
    // REDUCE_EXTRAPOLATE inverse DWT (progressive.c progressive_rfx_idwt_x/_y + dwt_2d_decode_block).
    // Band sizes are asymmetric: per level, low count nL=(64>>level)+1, high count nH (31/16/8).
    // The (a+b)/2 divisions are C integer division (truncate toward zero) — C# int division matches.
    // ---------------------------------------------------------------------------------------------
    private static void IdwtX(short[] lb, int lo, int loStep, short[] hb, int hi, int hiStep,
                              short[] db, int dOff, int dStep, int nL, int nH, int nD)
    {
        for (int i = 0; i < nD; i++)
        {
            int pL = lo, pH = hi, pX = dOff;
            int H0 = hb[pH++], L0 = lb[pL++];
            short X0 = Clampi16(L0 - H0), X2 = Clampi16(L0 - H0);
            for (int j = 0; j < nH - 1; j++)
            {
                int H1 = hb[pH++];
                L0 = lb[pL++];
                X2 = Clampi16(L0 - ((H0 + H1) / 2));
                short X1 = Clampi16((X0 + X2) / 2 + 2 * H0);
                db[pX] = X0; db[pX + 1] = X1; pX += 2;
                X0 = X2; H0 = H1;
            }
            if (nL <= nH + 1)
            {
                if (nL <= nH)
                {
                    db[pX] = X2; db[pX + 1] = Clampi16(X2 + 2 * H0);
                }
                else
                {
                    L0 = lb[pL];
                    X0 = Clampi16(L0 - H0);
                    db[pX] = X2; db[pX + 1] = Clampi16((X0 + X2) / 2 + 2 * H0); db[pX + 2] = X0;
                }
            }
            else
            {
                L0 = lb[pL++];
                X0 = Clampi16(L0 - H0 / 2);
                db[pX] = X2; db[pX + 1] = Clampi16((X0 + X2) / 2 + 2 * H0); db[pX + 2] = X0;
                L0 = lb[pL];
                db[pX + 3] = Clampi16((X0 + L0) / 2);
            }
            lo += loStep; hi += hiStep; dOff += dStep;
        }
    }

    private static void IdwtY(short[] lb, int lo, int loStep, short[] hb, int hi, int hiStep,
                              short[] db, int dOff, int dStep, int nL, int nH, int nD)
    {
        for (int i = 0; i < nD; i++)
        {
            int pL = lo, pH = hi, pX = dOff;
            int H0 = hb[pH]; pH += hiStep;
            int L0 = lb[pL]; pL += loStep;
            short X0 = Clampi16(L0 - H0), X2 = Clampi16(L0 - H0);
            for (int j = 0; j < nH - 1; j++)
            {
                int H1 = hb[pH]; pH += hiStep;
                L0 = lb[pL]; pL += loStep;
                X2 = Clampi16(L0 - ((H0 + H1) / 2));
                short X1 = Clampi16((X0 + X2) / 2 + 2 * H0);
                db[pX] = X0; pX += dStep;
                db[pX] = X1; pX += dStep;
                X0 = X2; H0 = H1;
            }
            if (nL <= nH + 1)
            {
                if (nL <= nH)
                {
                    db[pX] = X2; pX += dStep;
                    db[pX] = Clampi16(X2 + 2 * H0);
                }
                else
                {
                    L0 = lb[pL];
                    X0 = Clampi16(L0 - H0);
                    db[pX] = X2; pX += dStep;
                    db[pX] = Clampi16((X0 + X2) / 2 + 2 * H0); pX += dStep;
                    db[pX] = X0;
                }
            }
            else
            {
                L0 = lb[pL]; pL += loStep;
                X0 = Clampi16(L0 - H0 / 2);
                db[pX] = X2; pX += dStep;
                db[pX] = Clampi16((X0 + X2) / 2 + 2 * H0); pX += dStep;
                db[pX] = X0; pX += dStep;
                L0 = lb[pL];
                db[pX] = Clampi16((X0 + L0) / 2);
            }
            lo++; hi++; dOff++;
        }
    }

    // One extrapolate DWT level: bands packed HL | LH | HH | LL at 'bbase', output (nL+nH)² at 'bbase'.
    private static void DwtBlockEx(short[] buffer, int bbase, short[] temp, int level)
    {
        int nL = (64 >> level) + 1;
        int nH = level == 1 ? 31 : ((64 + (1 << (level - 1))) >> level);
        int dstStep = nL + nH;
        int hl = bbase;
        int lh = bbase + nH * nL;
        int hh = lh + nL * nH;
        int ll = hh + nH * nH;
        int l = 0, h = nL * dstStep; // temp offsets
        IdwtX(buffer, ll, nL, buffer, hl, nH, temp, l, dstStep, nL, nH, nL); // horizontal LL+HL -> L
        IdwtX(buffer, lh, nL, buffer, hh, nH, temp, h, dstStep, nL, nH, nH); // horizontal LH+HH -> H
        IdwtY(temp, l, dstStep, temp, h, dstStep, buffer, bbase, dstStep, nL, nH, dstStep); // vertical
    }

    // Inverse DWT, 3 levels, over the packed subband buffer (in-place, pixels end up at [0..4095]).
    private void InverseDwt(short[] srcDst, bool extrapolate)
    {
        if (!extrapolate)
        {
            DwtBlockAt(srcDst, 3840, _idwt, 8);
            DwtBlockAt(srcDst, 3072, _idwt, 16);
            DwtBlockAt(srcDst, 0, _idwt, 32);
        }
        else
        {
            DwtBlockEx(srcDst, 3807, _idwt, 3);
            DwtBlockEx(srcDst, 3007, _idwt, 2);
            DwtBlockEx(srcDst, 0, _idwt, 1);
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
    // Per-band quant arithmetic (progressive_rfx_quant_add / _sub / _lsub). quantProg for quality=0xFF
    // is the "full" prog quant = all zeros (default(Quant)).
    private static Quant QuantAdd(in Quant a, in Quant b) => new Quant
    {
        LL3 = a.LL3 + b.LL3, HL3 = a.HL3 + b.HL3, LH3 = a.LH3 + b.LH3, HH3 = a.HH3 + b.HH3,
        HL2 = a.HL2 + b.HL2, LH2 = a.LH2 + b.LH2, HH2 = a.HH2 + b.HH2,
        HL1 = a.HL1 + b.HL1, LH1 = a.LH1 + b.LH1, HH1 = a.HH1 + b.HH1,
    };
    // a - b per band, clamped at 0 (numBits for an UPGRADE pass can never be negative).
    private static Quant QuantSub(in Quant a, in Quant b)
    {
        static int S(int x, int y) { int d = x - y; return d < 0 ? 0 : d; }
        return new Quant
        {
            LL3 = S(a.LL3, b.LL3), HL3 = S(a.HL3, b.HL3), LH3 = S(a.LH3, b.LH3), HH3 = S(a.HH3, b.HH3),
            HL2 = S(a.HL2, b.HL2), LH2 = S(a.LH2, b.LH2), HH2 = S(a.HH2, b.HH2),
            HL1 = S(a.HL1, b.HL1), LH1 = S(a.LH1, b.LH1), HH1 = S(a.HH1, b.HH1),
        };
    }
    // shift = (quant + quantProg) - 1 per band (progressive_rfx_quant_add + lsub 1).
    private static Quant QuantShift(in Quant q, in Quant prog) => new Quant
    {
        LL3 = q.LL3 + prog.LL3 - 1, HL3 = q.HL3 + prog.HL3 - 1, LH3 = q.LH3 + prog.LH3 - 1, HH3 = q.HH3 + prog.HH3 - 1,
        HL2 = q.HL2 + prog.HL2 - 1, LH2 = q.LH2 + prog.LH2 - 1, HH2 = q.HH2 + prog.HH2 - 1,
        HL1 = q.HL1 + prog.HL1 - 1, LH1 = q.LH1 + prog.LH1 - 1, HH1 = q.HH1 + prog.HH1 - 1,
    };

    // Region prog quant entry (quality(1) + Y/Cb/Cr quant 5 bytes each); tile quality bytes index it.
    private readonly struct ProgQuant
    {
        public ProgQuant(Quant y, Quant cb, Quant cr) { Y = y; Cb = cb; Cr = cr; }
        public readonly Quant Y, Cb, Cr;
    }

    private void DecodeComponent(byte[] data, int dataOff, int dataLen, short[] srcDst, short[] current,
                                 short[] sign, in Quant shift, bool coeffDiff, bool extrapolate)
    {
        RlgrDecode(1, data, dataOff, dataLen, srcDst, 4096);
        Array.Copy(srcDst, sign, 4096);

        if (!extrapolate)
        {
            // Square subband layout (rfx_dwt.c geometry): 32/16/8 per level.
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
        }
        else
        {
            // REDUCE_EXTRAPOLATE layout: HL1 31x33 @0, LH1 33x31 @1023, HH1 31x31 @2046,
            // HL2 16x17 @3007, LH2 17x16 @3279, HH2 16x16 @3551, HL3 8x9 @3807, LH3 9x8 @3879,
            // HH3 8x8 @3951, LL3 9x9 @4015.
            LShift(srcDst, 0, 1023, shift.HL1);
            LShift(srcDst, 1023, 1023, shift.LH1);
            LShift(srcDst, 2046, 961, shift.HH1);
            LShift(srcDst, 3007, 272, shift.HL2);
            LShift(srcDst, 3279, 272, shift.LH2);
            LShift(srcDst, 3551, 256, shift.HH2);
            LShift(srcDst, 3807, 72, shift.HL3);
            LShift(srcDst, 3879, 72, shift.LH3);
            LShift(srcDst, 3951, 64, shift.HH3);
            DifferentialDecode(srcDst, 4015, 81);
            LShift(srcDst, 4015, 81, shift.LL3);
        }

        // coeffDiff: add 'current' into the buffer AND store the summed result back into 'current'
        // (FreeRDP add_16s_inplace writes BOTH operands) so successive difference tiles accumulate
        // correctly; otherwise 'current' goes stale and later frames decode to grey.
        if (coeffDiff) { for (int i = 0; i < 4096; i++) { short v = Clampi16(srcDst[i] + current[i]); srcDst[i] = v; current[i] = v; } }
        else Array.Copy(srcDst, current, 4096);

        InverseDwt(srcDst, extrapolate);
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
        ProgQuant[] progQuants = Array.Empty<ProgQuant>();
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
                                     out progQuants, out tileDataStart, out tileDataSize)) return tiles;
                    haveRegion = true;
                    tiles += ProcessTiles(data, regionFlags, contextFlags, numQuant, quants, progQuants,
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
                                    out Quant[] quants, out ProgQuant[] progQuants,
                                    out int tileDataStart, out int tileDataSize)
    {
        flags = 0; numQuant = 0; quants = Array.Empty<Quant>(); progQuants = Array.Empty<ProgQuant>();
        tileDataStart = 0; tileDataSize = 0;
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
        var pq = new ProgQuant[numProgQuant];
        for (int i = 0; i < numProgQuant; i++)
        {
            if (p + 16 > end) return false;
            p++; // quality byte of the entry (informational; tiles index the array positionally)
            var y = ReadQuant(data, p); p += 5;
            var cb = ReadQuant(data, p); p += 5;
            var cr = ReadQuant(data, p); p += 5;
            pq[i] = new ProgQuant(y, cb, cr);
        }
        if (p > end) return false;

        numQuant = nQuant; quants = q; progQuants = pq; tileDataStart = p; tileDataSize = (int)tds;
        return true;
    }

    private int ProcessTiles(byte[] data, byte regionFlags, byte contextFlags, int numQuant, Quant[] quants,
                             ProgQuant[] progQuants, int tileDataStart, int tileDataSize,
                             Action<int, int, byte[]> onTile)
    {
        int p = tileDataStart;
        int end = tileDataStart + tileDataSize;
        if (end > data.Length) end = data.Length;
        int count = 0;
        bool extrapolate = (regionFlags & RFX_DWT_REDUCE_EXTRAPOLATE) != 0;

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
                byte quality = simple ? (byte)0xFF : data[o++];
                ushort yLen = U16(o); o += 2;
                ushort cbLen = U16(o); o += 2;
                ushort crLen = U16(o); o += 2;
                ushort tailLen = U16(o); o += 2;
                int yData = o; o += yLen;
                int cbData = o; o += cbLen;
                int crData = o; o += crLen;
                // tail skipped

                if (quantIdxY < numQuant && quantIdxCb < numQuant && quantIdxCr < numQuant &&
                    (quality == 0xFF || quality < progQuants.Length) &&
                    o <= bEnd && crData + crLen <= data.Length)
                {
                    ReconstructTile(data, xIdx, yIdx, tflags, quality,
                                    quants[quantIdxY], quants[quantIdxCb], quants[quantIdxCr], progQuants,
                                    yData, yLen, cbData, cbLen, crData, crLen, extrapolate, onTile);
                    count++;
                }
            }
            else if (blockType == WBT_TILE_UPGRADE)
            {
                // SRL/RAW progressive refinement pass (progressive_tile_read_upgrade): 20-byte header,
                // then the 6 per-component SRL/RAW blobs.
                if (o + 20 > bEnd) { p = bEnd; continue; }
                byte uQY = data[o++], uQCb = data[o++], uQCr = data[o++];
                ushort uX = U16(o); o += 2;
                ushort uY = U16(o); o += 2;
                byte uQuality = data[o++];
                ushort ySrlLen = U16(o); o += 2;
                ushort yRawLen = U16(o); o += 2;
                ushort cbSrlLen = U16(o); o += 2;
                ushort cbRawLen = U16(o); o += 2;
                ushort crSrlLen = U16(o); o += 2;
                ushort crRawLen = U16(o); o += 2;
                int ySrl = o; o += ySrlLen;
                int yRaw = o; o += yRawLen;
                int cbSrl = o; o += cbSrlLen;
                int cbRaw = o; o += cbRawLen;
                int crSrl = o; o += crSrlLen;
                int crRaw = o; o += crRawLen;

                if (uQY < numQuant && uQCb < numQuant && uQCr < numQuant &&
                    (uQuality == 0xFF || uQuality < progQuants.Length) && o <= bEnd)
                {
                    if (UpgradeTile(data, uX, uY, uQuality,
                                    quants[uQY], quants[uQCb], quants[uQCr], progQuants,
                                    ySrl, ySrlLen, yRaw, yRawLen, cbSrl, cbSrlLen, cbRaw, cbRawLen,
                                    crSrl, crSrlLen, crRaw, crRawLen, extrapolate, onTile))
                        count++;
                }
            }
            p = bEnd;
        }
        return count;
    }

    private void ReconstructTile(byte[] data, int xIdx, int yIdx, byte tflags, byte quality,
                                 in Quant qY, in Quant qCb, in Quant qCr, ProgQuant[] progQuants,
                                 int yOff, int yLen, int cbOff, int cbLen, int crOff, int crLen,
                                 bool extrapolate, Action<int, int, byte[]> onTile)
    {
        bool coeffDiff = (tflags & RFX_TILE_DIFFERENCE) != 0;
        // quality byte indexes the region progQuant table; 0xFF = full quality (all-zero prog quant).
        ProgQuant prog = quality == 0xFF ? default : progQuants[quality];
        Quant shY = QuantShift(qY, prog.Y), shCb = QuantShift(qCb, prog.Cb), shCr = QuantShift(qCr, prog.Cr);
        var cell = TileCell(xIdx, yIdx);

        DecodeComponent(data, yOff, yLen, _scratchY, cell.Cur[0], cell.Sign[0], shY, coeffDiff, extrapolate);
        DecodeComponent(data, cbOff, cbLen, _scratchCb, cell.Cur[1], cell.Sign[1], shCb, coeffDiff, extrapolate);
        DecodeComponent(data, crOff, crLen, _scratchCr, cell.Cur[2], cell.Sign[2], shCr, coeffDiff, extrapolate);

        // bitPos = quant + progQuant per component: how many bits each band is still missing; UPGRADE
        // passes deliver (oldBitPos - newBitPos) bits per coefficient.
        cell.BitPos = new[] { QuantAdd(qY, prog.Y), QuantAdd(qCb, prog.Cb), QuantAdd(qCr, prog.Cr) };

        YCbCrToBgra(_scratchY, _scratchCb, _scratchCr);
        onTile(xIdx, yIdx, _rgba);
    }

    // ---------------------------------------------------------------------------------------------
    // UPGRADE pass (progressive_decompress_tile_upgrade): each pass adds numBits low-order bits to
    // every coefficient in 'current'. Coefficients with known sign read from the RAW stream; still-
    // zero ones read from the SRL stream (which also reveals their sign). LL3 is all-RAW. Pixels are
    // then rebuilt from 'current' (reverse DWT path — 'current' stays in coefficient domain).
    // Band offsets are the extrapolate layout unconditionally, mirroring FreeRDP.
    // ---------------------------------------------------------------------------------------------
    private sealed class SrlState
    {
        public int Kp = 8, Mode, Nz;
        public BitStream Srl = null!, Raw = null!;
    }

    private static short SrlRead(SrlState state, int numBits)
    {
        var bs = state.Srl;
        if (state.Nz != 0) { state.Nz--; return 0; }
        int k = state.Kp >> 3;
        if (state.Mode == 0)
        {
            // zero encoding
            int bit = (bs.Accumulator & 0x80000000u) != 0 ? 1 : 0;
            bs.Shift(1);
            if (bit == 0)
            {
                state.Nz = 1 << k;
                state.Kp += 4; if (state.Kp > 80) state.Kp = 80;
                state.Nz--;
                return 0;
            }
            state.Nz = 0;
            state.Mode = 1;
            if (k != 0)
            {
                state.Nz = (int)((bs.Accumulator >> (32 - k)) & (uint)((1 << k) - 1));
                bs.Shift(k);
            }
            if (state.Nz != 0) { state.Nz--; return 0; }
        }
        state.Mode = 0;
        // unary encoding: sign bit, then count zeros until a 1 (capped at (1<<numBits)-1)
        int sign = (bs.Accumulator & 0x80000000u) != 0 ? 1 : 0;
        bs.Shift(1);
        state.Kp = state.Kp < 6 ? 0 : state.Kp - 6;
        if (numBits == 1) return (short)(sign != 0 ? -1 : 1);
        int mag = 1, max = (1 << numBits) - 1;
        while (mag < max)
        {
            int b = (bs.Accumulator & 0x80000000u) != 0 ? 1 : 0;
            bs.Shift(1);
            if (b != 0) break;
            mag++;
        }
        return (short)(sign != 0 ? -mag : mag);
    }

    private static int RawRead(BitStream raw, int numBits)
    {
        int v = (int)((raw.Accumulator >> (32 - numBits)) & (uint)((1 << numBits) - 1));
        raw.Shift(numBits);
        return v;
    }

    private static void UpgradeBlock(SrlState state, short[] current, short[] sign, int off, int length,
                                     int shift, int numBits, bool nonLL)
    {
        if (numBits < 1) return;
        var raw = state.Raw;
        if (!nonLL)
        {
            for (int i = 0; i < length; i++)
            {
                int v = RawRead(raw, numBits);
                current[off + i] = Clampi16(current[off + i] + (v << shift));
            }
            return;
        }
        for (int i = 0; i < length; i++)
        {
            int input;
            short s = sign[off + i];
            if (s > 0) input = RawRead(raw, numBits);
            else if (s < 0) input = -RawRead(raw, numBits);
            else { input = SrlRead(state, numBits); sign[off + i] = Clampi16(input); }
            current[off + i] = Clampi16(current[off + i] + (input << shift));
        }
    }

    private void UpgradeComponent(in Quant shift, in Quant numBits, short[] srcDst, short[] current,
                                  short[] sign, byte[] data, int srlOff, int srlLen, int rawOff, int rawLen,
                                  bool extrapolate)
    {
        var state = new SrlState
        {
            Srl = new BitStream(data, srlOff, srlLen),
            Raw = new BitStream(data, rawOff, rawLen),
        };
        UpgradeBlock(state, current, sign, 0, 1023, shift.HL1, numBits.HL1, true);
        UpgradeBlock(state, current, sign, 1023, 1023, shift.LH1, numBits.LH1, true);
        UpgradeBlock(state, current, sign, 2046, 961, shift.HH1, numBits.HH1, true);
        UpgradeBlock(state, current, sign, 3007, 272, shift.HL2, numBits.HL2, true);
        UpgradeBlock(state, current, sign, 3279, 272, shift.LH2, numBits.LH2, true);
        UpgradeBlock(state, current, sign, 3551, 256, shift.HH2, numBits.HH2, true);
        UpgradeBlock(state, current, sign, 3807, 72, shift.HL3, numBits.HL3, true);
        UpgradeBlock(state, current, sign, 3879, 72, shift.LH3, numBits.LH3, true);
        UpgradeBlock(state, current, sign, 3951, 64, shift.HH3, numBits.HH3, true);
        UpgradeBlock(state, current, sign, 4015, 81, shift.LL3, numBits.LL3, false);
        // reverse DWT: rebuild pixels from the upgraded coefficients
        Array.Copy(current, srcDst, 4096);
        InverseDwt(srcDst, extrapolate);
    }

    private bool UpgradeTile(byte[] data, int xIdx, int yIdx, byte quality,
                             in Quant qY, in Quant qCb, in Quant qCr, ProgQuant[] progQuants,
                             int ySrl, int ySrlLen, int yRaw, int yRawLen,
                             int cbSrl, int cbSrlLen, int cbRaw, int cbRawLen,
                             int crSrl, int crSrlLen, int crRaw, int crRawLen,
                             bool extrapolate, Action<int, int, byte[]> onTile)
    {
        var cell = TileCell(xIdx, yIdx);
        if (cell.BitPos == null) return false; // UPGRADE without a preceding FIRST — skip
        ProgQuant prog = quality == 0xFF ? default : progQuants[quality];
        Span<Quant> progC = stackalloc Quant[] { prog.Y, prog.Cb, prog.Cr };
        Span<Quant> quantC = stackalloc Quant[] { qY, qCb, qCr };
        var scratch = new[] { _scratchY, _scratchCb, _scratchCr };
        Span<int> offs = stackalloc int[] { ySrl, ySrlLen, yRaw, yRawLen, cbSrl, cbSrlLen, cbRaw, cbRawLen, crSrl, crSrlLen, crRaw, crRawLen };
        for (int c = 0; c < 3; c++)
        {
            Quant newBitPos = QuantAdd(quantC[c], progC[c]);
            Quant numBits = QuantSub(cell.BitPos[c], newBitPos);
            Quant shift = QuantShift(quantC[c], progC[c]);
            UpgradeComponent(shift, numBits, scratch[c], cell.Cur[c], cell.Sign[c],
                             data, offs[c * 4], offs[c * 4 + 1], offs[c * 4 + 2], offs[c * 4 + 3], extrapolate);
            cell.BitPos[c] = newBitPos;
        }
        YCbCrToBgra(_scratchY, _scratchCb, _scratchCr);
        onTile(xIdx, yIdx, _rgba);
        return true;
    }
}
