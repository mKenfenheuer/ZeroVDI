// RemoteFX Progressive Codec (CAPROGRESSIVE / RDPGFX_CODECID_CAPROGRESSIVE 0x0009) decoder.
//
// GNOME Remote Desktop's gfx pipeline streams the desktop as RemoteFX Progressive over
// WIRE_TO_SURFACE_2, regardless of the AVC caps we advertise. This module decodes those tiles to
// RGBA so the surface compositor in rdpgfx.js can putImageData them. It is a deliberately literal
// port of FreeRDP's progressive codec (libfreerdp/codec):
//   rfx_rlgr.c          — RLGR1 / RLGR3 entropy decode (MS-RDPRFX 3.1.8.1.7.3)
//   rfx_dwt.c           — inverse 2D DWT (non-extrapolate, 3 levels over a 64x64 tile)
//   rfx_differential.h  — LL3 differential decode
//   progressive.c       — block/region/tile stream parse, dequant (left-shift per subband),
//                          tile reconstruction, YCbCr->RGB
//   prim_colors.c       — yCbCrToRGB_16s8u_P3AC4R fixed-point conversion
//
// Two host encoder profiles exist in the wild and both are supported:
//   - GNOME RD: SIMPLE tiles, non-extrapolate DWT (regionFlags=0), no UPGRADE passes.
//   - Windows RDS: FIRST tiles at reduced quality + UPGRADE (SRL/RAW) refinement passes, with the
//     REDUCE_EXTRAPOLATE region flag set (different subband geometry AND a different inverse DWT).
// UPGRADE passes mutate the persistent per-tile 'current'/'sign' coefficient state, so they are
// required for correctness: skipping them desyncs later DIFFERENCE tiles, not just quality.
//
// All math is fixed-point INT16/INT32 matching FreeRDP exactly (the wavelet/quant are lossy-by-design
// and must bit-match the encoder's assumptions to look right).

(function (global) {
"use strict";

// ---- block types (progressive.h PROGRESSIVE_WBT_*) ----------------------------------------------
var WBT_SYNC         = 0xCCC0;
var WBT_FRAME_BEGIN  = 0xCCC1;
var WBT_FRAME_END    = 0xCCC2;
var WBT_CONTEXT      = 0xCCC3;
var WBT_REGION       = 0xCCC4;
var WBT_TILE_SIMPLE  = 0xCCC5;
var WBT_TILE_FIRST   = 0xCCC6;
var WBT_TILE_UPGRADE = 0xCCC7;

var RFX_SUBBAND_DIFFING        = 0x01; // context flags
var RFX_TILE_DIFFERENCE        = 0x01; // tile flags
var RFX_DWT_REDUCE_EXTRAPOLATE = 0x01; // region flags

// RLGR adaptation constants (rfx_rlgr.c)
var KPMAX = 80, LSGR = 3, UP_GR = 4, DN_GR = 6, UQ_GR = 3, DQ_GR = 3;

function clampi16(v) { return v < -32768 ? -32768 : (v > 32767 ? 32767 : v); }
function clip8(v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

// =================================================================================================
// MSB-first bit reader over a byte buffer. Exposes the same surface the RLGR port needs:
//   .accumulator  — the next up-to-32 unconsumed bits, left-aligned (bit 31 = next bit), zero-padded
//   .remaining()  — unconsumed bits left in the buffer
//   .shift(n)     — consume n bits (1..32), refilling accumulator
//   .shift32()    — consume 32 bits
// This is a simpler, position-based equivalent of winpr's wBitStream (which the FreeRDP port targets);
// it keeps a 64-bit window in two 32-bit halves so the left-aligned accumulator is always full.
// =================================================================================================
function BitStream(data, size) {
    this.buffer = data;
    this.capacity = size;       // bytes
    this.length = size * 8;     // total bits
    this.bitPos = 0;            // absolute bit position of accumulator's top bit
    this.accumulator = 0;       // >>> 0, MSB-first, left-aligned at bitPos
    this._reload();
}
// Read the 32 bits starting at absolute bit position p (zero-padded past end).
BitStream.prototype._bitsAt = function (p) {
    var b = this.buffer, n = this.capacity;
    var bytePos = p >> 3;
    var bitOff = p & 7;
    // read 5 bytes to cover a 32-bit window crossing a byte boundary
    var b0 = bytePos < n ? b[bytePos] : 0;
    var b1 = bytePos + 1 < n ? b[bytePos + 1] : 0;
    var b2 = bytePos + 2 < n ? b[bytePos + 2] : 0;
    var b3 = bytePos + 3 < n ? b[bytePos + 3] : 0;
    var b4 = bytePos + 4 < n ? b[bytePos + 4] : 0;
    // assemble 40 bits then take the top 32 after skipping bitOff
    var hi = ((b0 << 24) | (b1 << 16) | (b2 << 8) | b3) >>> 0;
    if (bitOff === 0) return hi;
    var lo = b4;
    // shift left by bitOff, bring in bits from b4
    var v = ((hi << bitOff) | (lo >>> (8 - bitOff))) >>> 0;
    return v >>> 0;
};
BitStream.prototype._reload = function () { this.accumulator = this._bitsAt(this.bitPos); };
BitStream.prototype.remaining = function () { var r = this.length - this.bitPos; return r < 0 ? 0 : r; };
BitStream.prototype.shift = function (nbits) {
    if (nbits <= 0) return;
    if (nbits > 32) nbits = 32;
    this.bitPos += nbits;
    this._reload();
};
BitStream.prototype.shift32 = function () { this.bitPos += 32; this._reload(); };

// Count leading zeros of a 32-bit value (lzcnt_s). Math.clz32 is exact and avoids the signed-shift
// overflow that a hand-rolled binary search hits in JS (x << 16 can go negative).
function lzcnt(x) { return Math.clz32(x >>> 0); }

// =================================================================================================
// RLGR decode (rfx_rlgr.c rfx_rlgr_decode). mode 1=RLGR1, 3=RLGR3. Writes 'count' INT16 into out.
// =================================================================================================
function rlgrDecode(mode, src, srcSize, out, count) {
    if (mode !== 1 && mode !== 3) mode = 1;
    if (!src || !srcSize) { out.fill(0, 0, count); return; }

    var bs = new BitStream(src, srcSize);
    var k = 1, kp = 1 << LSGR;
    var kr = 1, krp = 1 << LSGR;
    var idx = 0;

    while (bs.remaining() > 0 && idx < count) {
        if (k) {
            // ---- Run-Length (RL) mode ----
            var run = 0;
            // count leading 0s (zeros run marker bits), spanning 32-bit windows
            var cnt = lzcnt(bs.accumulator);
            var nbits = bs.remaining();
            if (cnt > nbits) cnt = nbits;
            var vk = cnt;
            while (cnt === 32 && bs.remaining() > 0) {
                bs.shift32();
                cnt = lzcnt(bs.accumulator);
                nbits = bs.remaining();
                if (cnt > nbits) cnt = nbits;
                vk += cnt;
            }
            bs.shift(vk % 32);
            if (bs.remaining() < 1) break;
            bs.shift(1); // the terminating 1 bit

            while (vk-- > 0) {
                run += (1 << k);
                kp += UP_GR; if (kp > KPMAX) kp = KPMAX;
                k = kp >> LSGR;
            }

            // next k bits: run length remainder
            if (bs.remaining() < k) break;
            var code = 0;
            if (k > 0) { code = (bs.accumulator >>> (32 - k)) & ((1 << k) - 1); bs.shift(k); }
            run += code;

            // sign bit
            if (bs.remaining() < 1) break;
            var sign = (bs.accumulator & 0x80000000) ? 1 : 0;
            bs.shift(1);

            // count leading 1s
            cnt = lzcnt((~bs.accumulator) >>> 0);
            nbits = bs.remaining();
            if (cnt > nbits) cnt = nbits;
            vk = cnt;
            while (cnt === 32 && bs.remaining() > 0) {
                bs.shift32();
                cnt = lzcnt((~bs.accumulator) >>> 0);
                nbits = bs.remaining();
                if (cnt > nbits) cnt = nbits;
                vk += cnt;
            }
            bs.shift(vk % 32);
            if (bs.remaining() < 1) break;
            bs.shift(1);

            // next kr bits: code remainder
            if (bs.remaining() < kr) break;
            var c2 = 0;
            if (kr > 0) { c2 = (bs.accumulator >>> (32 - kr)) & ((1 << kr) - 1); bs.shift(kr); }
            c2 |= (vk << kr);

            if (!vk) { krp = krp > 2 ? krp - 2 : 0; kr = krp >> LSGR; }
            else if (vk !== 1) { krp += vk; if (krp > KPMAX) krp = KPMAX; kr = krp >> LSGR; }

            kp = kp > DN_GR ? kp - DN_GR : 0;
            k = kp >> LSGR;

            var mag = sign ? -(c2 + 1) : (c2 + 1);

            // emit 'run' zeros, then the magnitude
            var rem = count - idx;
            var z = run; if (z > rem) z = rem;
            for (var i = 0; i < z; i++) out[idx++] = 0;
            if (idx < count) out[idx++] = clampi16(mag);
        } else {
            // ---- Golomb-Rice (GR) mode ----
            // count leading 1s
            var gcnt = lzcnt((~bs.accumulator) >>> 0);
            var gnb = bs.remaining();
            if (gcnt > gnb) gcnt = gnb;
            var gvk = gcnt;
            while (gcnt === 32 && bs.remaining() > 0) {
                bs.shift32();
                gcnt = lzcnt((~bs.accumulator) >>> 0);
                gnb = bs.remaining();
                if (gcnt > gnb) gcnt = gnb;
                gvk += gcnt;
            }
            bs.shift(gvk % 32);
            if (bs.remaining() < 1) break;
            bs.shift(1);

            if (bs.remaining() < kr) break;
            var gcode = 0;
            if (kr > 0) { gcode = (bs.accumulator >>> (32 - kr)) & ((1 << kr) - 1); bs.shift(kr); }
            gcode |= (gvk << kr);

            if (!gvk) { krp = krp > 2 ? krp - 2 : 0; kr = krp >> LSGR; }
            else if (gvk !== 1) { krp += gvk; if (krp > KPMAX) krp = KPMAX; kr = krp >> LSGR; }

            if (mode === 1) {
                var gmag;
                if (!gcode) { kp += UQ_GR; if (kp > KPMAX) kp = KPMAX; k = kp >> LSGR; gmag = 0; }
                else {
                    kp = kp > DQ_GR ? kp - DQ_GR : 0; k = kp >> LSGR;
                    gmag = (gcode & 1) ? -((gcode + 1) >> 1) : (gcode >> 1);
                }
                if (idx < count) out[idx++] = clampi16(gmag);
            } else {
                // RLGR3
                var nIdx = 0;
                var mg = 0;
                if (gcode) { mg = gcode; nIdx = 32 - lzcnt(mg >>> 0); }
                if (bs.remaining() < nIdx) break;
                var val1 = 0;
                if (nIdx > 0) { val1 = (bs.accumulator >>> (32 - nIdx)) & ((1 << nIdx) - 1); bs.shift(nIdx); }
                var val2 = gcode - val1;
                if (val1 && val2) { kp = kp > 2 * DQ_GR ? kp - 2 * DQ_GR : 0; k = kp >> LSGR; }
                else if (!val1 && !val2) { kp += 2 * UQ_GR; if (kp > KPMAX) kp = KPMAX; k = kp >> LSGR; }
                var m1 = (val1 & 1) ? -((val1 + 1) >> 1) : (val1 >> 1);
                if (idx < count) out[idx++] = clampi16(m1);
                var m2 = (val2 & 1) ? -((val2 + 1) >> 1) : (val2 >> 1);
                if (idx < count) out[idx++] = clampi16(m2);
            }
        }
    }
    while (idx < count) out[idx++] = 0;
}

// =================================================================================================
// Differential decode of LL3 (rfx_differential.h): out[i+1] += out[i].
// =================================================================================================
function differentialDecode(buf, start, size) {
    for (var i = 0; i < size - 1; i++) {
        buf[start + i + 1] = clampi16(buf[start + i] + buf[start + i + 1]);
    }
}

// =================================================================================================
// Dequant: left-shift each subband by its shift value (progressive_rfx_decode_block).
// shift is the per-band {LL3,HL3,LH3,HH3,HL2,LH2,HH2,HL1,LH1,HH1}.
// Non-extrapolate band layout/offsets/counts from progressive_rfx_decode_component.
// =================================================================================================
function lShift(buf, base, len, sh) {
    if (sh === 0) return;
    for (var i = 0; i < len; i++) buf[base + i] = clampi16(buf[base + i] << sh);
}

// Decode one component (Y/Cb/Cr) into a 64x64 INT16 buffer 'srcDst' (row-major, length 4096).
// 'current' and 'sign' persist per tile cell (coeffDiff accumulation + UPGRADE passes read them).
// shift = per-band dequant shifts. 'extrapolate' selects band geometry AND inverse DWT variant.
function decodeComponent(mode, data, dataLen, srcDst, current, sign, idwtTmp, shift, coeffDiff, extrapolate) {
    rlgrDecode(mode, data, dataLen, srcDst, 4096);
    // CopyMemory(sign, buffer) — the raw RLGR output; UPGRADE passes route zero/non-zero coefficients
    // to the SRL vs RAW streams based on it.
    sign.set(srcDst.subarray(0, 4096));

    if (!extrapolate) {
        // Square subband layout (rfx_dwt.c geometry): 32/16/8 per level.
        differentialDecode(srcDst, 4032, 64); // LL3 differential (last 64 entries)
        lShift(srcDst, 0,    1024, shift.HL1);
        lShift(srcDst, 1024, 1024, shift.LH1);
        lShift(srcDst, 2048, 1024, shift.HH1);
        lShift(srcDst, 3072, 256,  shift.HL2);
        lShift(srcDst, 3328, 256,  shift.LH2);
        lShift(srcDst, 3584, 256,  shift.HH2);
        lShift(srcDst, 3840, 64,   shift.HL3);
        lShift(srcDst, 3904, 64,   shift.LH3);
        lShift(srcDst, 3968, 64,   shift.HH3);
        lShift(srcDst, 4032, 64,   shift.LL3);
    } else {
        // REDUCE_EXTRAPOLATE layout (progressive.c band table):
        //   HL1 31x33 @0, LH1 33x31 @1023, HH1 31x31 @2046, HL2 16x17 @3007, LH2 17x16 @3279,
        //   HH2 16x16 @3551, HL3 8x9 @3807, LH3 9x8 @3879, HH3 8x8 @3951, LL3 9x9 @4015.
        lShift(srcDst, 0,    1023, shift.HL1);
        lShift(srcDst, 1023, 1023, shift.LH1);
        lShift(srcDst, 2046, 961,  shift.HH1);
        lShift(srcDst, 3007, 272,  shift.HL2);
        lShift(srcDst, 3279, 272,  shift.LH2);
        lShift(srcDst, 3551, 256,  shift.HH2);
        lShift(srcDst, 3807, 72,   shift.HL3);
        lShift(srcDst, 3879, 72,   shift.LH3);
        lShift(srcDst, 3951, 64,   shift.HH3);
        differentialDecode(srcDst, 4015, 81); // LL3 differential
        lShift(srcDst, 4015, 81,   shift.LL3);
    }

    // progressive_rfx_dwt_2d_decode: with coeffDiff, add 'current' into the buffer AND store the summed
    // result back into 'current' (FreeRDP add_16s_inplace writes BOTH operands) so the next difference
    // tile accumulates correctly — otherwise 'current' goes stale and later frames decode to grey. Else
    // (full tile) copy buffer->current.
    if (coeffDiff) {
        for (var i = 0; i < 4096; i++) { var v = clampi16(srcDst[i] + current[i]); srcDst[i] = v; current[i] = v; }
    } else {
        current.set(srcDst.subarray(0, 4096));
    }

    inverseDwt(srcDst, idwtTmp, extrapolate);
}

// Inverse DWT, 3 levels, over the packed subband buffer (in-place, pixels end up at [0..4095]).
function inverseDwt(srcDst, idwtTmp, extrapolate) {
    if (!extrapolate) {
        // FreeRDP indexes a single 4096 buffer at offsets 3840 / 3072 / 0.
        dwtBlockAt(srcDst, 3840, idwtTmp, 8);
        dwtBlockAt(srcDst, 3072, idwtTmp, 16);
        dwtBlockAt(srcDst, 0,    idwtTmp, 32);
    } else {
        dwtBlockEx(srcDst, 3807, idwtTmp, 3);
        dwtBlockEx(srcDst, 3007, idwtTmp, 2);
        dwtBlockEx(srcDst, 0,    idwtTmp, 1);
    }
}

// dwt2dDecodeBlock but operating on buffer starting at 'base' (FreeRDP &buffer[base]).
function dwtBlockAt(buffer, base, idwt, subbandWidth) {
    var total = subbandWidth << 1;
    var sw = subbandWidth, sw2 = sw * sw;
    var ll = base + sw2 * 3, hl = base, lh = base + sw2, hh = base + sw2 * 2;
    var lDst = 0, hDst = sw2 * 2;
    var y, n, x;
    for (y = 0; y < sw; y++) {
        idwt[lDst] = clampi16(buffer[ll] - ((buffer[hl] + buffer[hl] + 1) >> 1));
        idwt[hDst] = clampi16(buffer[lh] - ((buffer[hh] + buffer[hh] + 1) >> 1));
        for (n = 1; n < sw; n++) {
            x = n << 1;
            idwt[lDst + x] = clampi16(buffer[ll + n] - ((buffer[hl + n - 1] + buffer[hl + n] + 1) >> 1));
            idwt[hDst + x] = clampi16(buffer[lh + n] - ((buffer[hh + n - 1] + buffer[hh + n] + 1) >> 1));
        }
        for (n = 0; n < sw - 1; n++) {
            x = n << 1;
            idwt[lDst + x + 1] = clampi16((buffer[hl + n] << 1) + ((idwt[lDst + x] + idwt[lDst + x + 2]) >> 1));
            idwt[hDst + x + 1] = clampi16((buffer[hh + n] << 1) + ((idwt[hDst + x] + idwt[hDst + x + 2]) >> 1));
        }
        x = n << 1;
        idwt[lDst + x + 1] = clampi16((buffer[hl + n] << 1) + idwt[lDst + x]);
        idwt[hDst + x + 1] = clampi16((buffer[hh + n] << 1) + idwt[hDst + x]);
        ll += sw; hl += sw; lh += sw; hh += sw;
        lDst += total; hDst += total;
    }
    for (x = 0; x < total; x++) {
        var lIdx = x, hIdx = x + sw * total, dst = base + x;
        buffer[dst] = clampi16(idwt[lIdx] - ((idwt[hIdx] * 2 + 1) >> 1));
        for (n = 1; n < sw; n++) {
            lIdx += total; hIdx += total;
            buffer[dst + 2 * total] = clampi16(idwt[lIdx] - ((idwt[hIdx - total] + idwt[hIdx] + 1) >> 1));
            buffer[dst + total] = clampi16((idwt[hIdx - total] << 1) + ((buffer[dst] + buffer[dst + 2 * total]) >> 1));
            dst += 2 * total;
        }
        buffer[dst + total] = clampi16((idwt[hIdx] << 1) + ((buffer[dst] * 2) >> 1));
    }
}

// =================================================================================================
// REDUCE_EXTRAPOLATE inverse DWT (progressive.c progressive_rfx_idwt_x/_y + dwt_2d_decode_block).
// Band sizes are asymmetric: per level, low count nL=(64>>level)+1, high count nH (31/16/8).
// NOTE: the (a+b)/2 divisions are C integer division (truncate toward zero), NOT >>1 (floor) — use
// |0 truncation or negative coefficients decode wrong.
// =================================================================================================
function idwtX(lb, lo, loStep, hb, hi, hiStep, db, dOff, dStep, nL, nH, nD) {
    for (var i = 0; i < nD; i++) {
        var pL = lo, pH = hi, pX = dOff;
        var H0 = hb[pH++], L0 = lb[pL++];
        var X0 = clampi16(L0 - H0), X2 = clampi16(L0 - H0), H1 = 0, X1 = 0;
        for (var j = 0; j < nH - 1; j++) {
            H1 = hb[pH++]; L0 = lb[pL++];
            X2 = clampi16(L0 - (((H0 + H1) / 2) | 0));
            X1 = clampi16((((X0 + X2) / 2) | 0) + 2 * H0);
            db[pX] = X0; db[pX + 1] = X1; pX += 2;
            X0 = X2; H0 = H1;
        }
        if (nL <= nH + 1) {
            if (nL <= nH) {
                db[pX] = X2; db[pX + 1] = clampi16(X2 + 2 * H0);
            } else {
                L0 = lb[pL++];
                X0 = clampi16(L0 - H0);
                db[pX] = X2; db[pX + 1] = clampi16((((X0 + X2) / 2) | 0) + 2 * H0); db[pX + 2] = X0;
            }
        } else {
            L0 = lb[pL++];
            X0 = clampi16(L0 - ((H0 / 2) | 0));
            db[pX] = X2; db[pX + 1] = clampi16((((X0 + X2) / 2) | 0) + 2 * H0); db[pX + 2] = X0;
            L0 = lb[pL++];
            db[pX + 3] = clampi16(((X0 + L0) / 2) | 0);
        }
        lo += loStep; hi += hiStep; dOff += dStep;
    }
}

function idwtY(lb, lo, loStep, hb, hi, hiStep, db, dOff, dStep, nL, nH, nD) {
    for (var i = 0; i < nD; i++) {
        var pL = lo, pH = hi, pX = dOff;
        var H0 = hb[pH]; pH += hiStep;
        var L0 = lb[pL]; pL += loStep;
        var X0 = clampi16(L0 - H0), X2 = clampi16(L0 - H0), H1 = 0, X1 = 0;
        for (var j = 0; j < nH - 1; j++) {
            H1 = hb[pH]; pH += hiStep;
            L0 = lb[pL]; pL += loStep;
            X2 = clampi16(L0 - (((H0 + H1) / 2) | 0));
            X1 = clampi16((((X0 + X2) / 2) | 0) + 2 * H0);
            db[pX] = X0; pX += dStep;
            db[pX] = X1; pX += dStep;
            X0 = X2; H0 = H1;
        }
        if (nL <= nH + 1) {
            if (nL <= nH) {
                db[pX] = X2; pX += dStep;
                db[pX] = clampi16(X2 + 2 * H0);
            } else {
                L0 = lb[pL];
                X0 = clampi16(L0 - H0);
                db[pX] = X2; pX += dStep;
                db[pX] = clampi16((((X0 + X2) / 2) | 0) + 2 * H0); pX += dStep;
                db[pX] = X0;
            }
        } else {
            L0 = lb[pL]; pL += loStep;
            X0 = clampi16(L0 - ((H0 / 2) | 0));
            db[pX] = X2; pX += dStep;
            db[pX] = clampi16((((X0 + X2) / 2) | 0) + 2 * H0); pX += dStep;
            db[pX] = X0; pX += dStep;
            L0 = lb[pL];
            db[pX] = clampi16(((X0 + L0) / 2) | 0);
        }
        lo++; hi++; dOff++;
    }
}

// One extrapolate DWT level: bands packed HL | LH | HH | LL at 'base', output (nL+nH)² at 'base'.
function dwtBlockEx(buffer, base, temp, level) {
    var nL = (64 >> level) + 1;
    var nH = level === 1 ? 31 : ((64 + (1 << (level - 1))) >> level);
    var dstStep = nL + nH;
    var HL = base;
    var LH = base + nH * nL;
    var HH = LH + nL * nH;
    var LL = HH + nH * nH;
    var L = 0, H = nL * dstStep; // temp offsets
    idwtX(buffer, LL, nL, buffer, HL, nH, temp, L, dstStep, nL, nH, nL); // horizontal LL+HL -> L
    idwtX(buffer, LH, nL, buffer, HH, nH, temp, H, dstStep, nL, nH, nH); // horizontal LH+HH -> H
    idwtY(temp, L, dstStep, temp, H, dstStep, buffer, base, dstStep, nL, nH, dstStep); // vertical
}

// =================================================================================================
// YCbCr -> RGBA (prim_colors.c general_yCbCrToRGB_16s8u_P3AC4R). Inputs are 64x64 INT16 planes.
// =================================================================================================
var C_CrR = Math.round(1.402525 * 65536);
var C_CrG = Math.round(0.714401 * 65536);
var C_CbG = Math.round(0.343730 * 65536);
var C_CbB = Math.round(1.769905 * 65536);
function ycbcrToRgba(yP, cbP, crP, rgba) {
    for (var i = 0; i < 4096; i++) {
        var Y = ((yP[i] + 4096) << 16);
        var Cb = cbP[i], Cr = crP[i];
        var R = (((Cr * C_CrR + Y) >> 16) >> 5);
        var G = (((Y - Cb * C_CbG - Cr * C_CrG) >> 16) >> 5);
        var B = (((Cb * C_CbB + Y) >> 16) >> 5);
        var o = i << 2;
        rgba[o] = clip8(R); rgba[o + 1] = clip8(G); rgba[o + 2] = clip8(B); rgba[o + 3] = 255;
    }
}

// Parse a 5-byte component codec quant into {LL3,...,HH1} (progressive_component_codec_quant_read).
function readQuant(data, o) {
    var b0 = data[o], b1 = data[o + 1], b2 = data[o + 2], b3 = data[o + 3], b4 = data[o + 4];
    return {
        LL3: b0 & 0x0F, HL3: b0 >> 4,
        LH3: b1 & 0x0F, HH3: b1 >> 4,
        HL2: b2 & 0x0F, LH2: b2 >> 4,
        HH2: b3 & 0x0F, HL1: b3 >> 4,
        LH1: b4 & 0x0F, HH1: b4 >> 4,
    };
}
// Per-band quant arithmetic (progressive_rfx_quant_add / _sub / _lsub). quantProg for quality=0xFF
// is the "full" prog quant = all zeros.
var QUANT_ZERO = { LL3: 0, HL3: 0, LH3: 0, HH3: 0, HL2: 0, LH2: 0, HH2: 0, HL1: 0, LH1: 0, HH1: 0 };
function quantAdd(a, b) {
    return {
        LL3: a.LL3 + b.LL3, HL3: a.HL3 + b.HL3, LH3: a.LH3 + b.LH3, HH3: a.HH3 + b.HH3,
        HL2: a.HL2 + b.HL2, LH2: a.LH2 + b.LH2, HH2: a.HH2 + b.HH2,
        HL1: a.HL1 + b.HL1, LH1: a.LH1 + b.LH1, HH1: a.HH1 + b.HH1,
    };
}
// a - b per band, clamped at 0 (numBits for an UPGRADE pass can never be negative).
function quantSub(a, b) {
    function s(x, y) { var d = x - y; return d < 0 ? 0 : d; }
    return {
        LL3: s(a.LL3, b.LL3), HL3: s(a.HL3, b.HL3), LH3: s(a.LH3, b.LH3), HH3: s(a.HH3, b.HH3),
        HL2: s(a.HL2, b.HL2), LH2: s(a.LH2, b.LH2), HH2: s(a.HH2, b.HH2),
        HL1: s(a.HL1, b.HL1), LH1: s(a.LH1, b.LH1), HH1: s(a.HH1, b.HH1),
    };
}
// shift = (quant + quantProg) - 1 per band (progressive_rfx_quant_add + lsub 1).
function quantShift(q, prog) {
    return {
        LL3: q.LL3 + prog.LL3 - 1, HL3: q.HL3 + prog.HL3 - 1, LH3: q.LH3 + prog.LH3 - 1, HH3: q.HH3 + prog.HH3 - 1,
        HL2: q.HL2 + prog.HL2 - 1, LH2: q.LH2 + prog.LH2 - 1, HH2: q.HH2 + prog.HH2 - 1,
        HL1: q.HL1 + prog.HL1 - 1, LH1: q.LH1 + prog.LH1 - 1, HH1: q.HH1 + prog.HH1 - 1,
    };
}
// Resolve a tile's quality byte to its region progQuant entry: null = full quality (all-zero prog
// quant), undefined = invalid index (skip the tile).
function progQuantFor(region, quality, log) {
    if (quality === 0xFF) return null;
    if (quality < region.numProgQuant) return region.progQuants[quality];
    if (log) log("progressive: tile quality " + quality + " >= numProgQuant " + region.numProgQuant);
    return undefined;
}

// =================================================================================================
// Per-surface progressive context: keeps the persistent 'current' coefficient grids per tile cell
// (needed for RFX_TILE_DIFFERENCE) and reusable scratch buffers.
// =================================================================================================
function ProgressiveContext() {
    this.scratchY = new Int16Array(4096);
    this.scratchCb = new Int16Array(4096);
    this.scratchCr = new Int16Array(4096);
    this.idwt = new Int16Array(4096 * 2 + 64); // DWT temp (needs 2*total*sw room)
    this.rgba = new Uint8ClampedArray(64 * 64 * 4);
    this.tiles = {};         // "x,y" -> per-cell persistent state (diff accumulation + upgrades)
    this.gridWidth = 0; this.gridHeight = 0;
    this._blobs = new Array(12); // reusable [srlOff,srlLen,rawOff,rawLen]*3 for UPGRADE tiles
}
ProgressiveContext.prototype.reset = function () { this.tiles = {}; };
ProgressiveContext.prototype._tileCell = function (xIdx, yIdx) {
    var key = xIdx + "," + yIdx;
    var c = this.tiles[key];
    if (!c) {
        c = {
            cur: [new Int16Array(4096), new Int16Array(4096), new Int16Array(4096)],
            sign: [new Int16Array(4096), new Int16Array(4096), new Int16Array(4096)],
            bitPos: null, // [Y,Cb,Cr] per-band bit positions, set by FIRST/SIMPLE, consumed by UPGRADE
        };
        this.tiles[key] = c;
    }
    return c;
};

// =================================================================================================
// Top-level: decode one full RFX_PROGRESSIVE bitstream (the WIRE_TO_SURFACE_2 payload).
// onTile(xIdx, yIdx, rgba64x64, regionRects) is called for each reconstructed 64x64 tile;
// regionRects ([{x,y,w,h}]) are the encapsulating REGION's tileRects — per [MS-RDPEGFX]
// 2.2.4.2.1.5 only pixels inside them may be written to the surface (see FreeRDP
// progressive_decompress, which intersects every tile with the union of these rects).
// Returns { tiles, frames } counts, or null on parse failure.
// =================================================================================================
// Locate the first valid RFX_PROGRESSIVE block header, skipping any leading framing/preamble.
// A valid header is a known blockType with 6 <= blockLen <= remaining. Returns the offset or -1.
function findBlockStart(data, dv, len) {
    for (var off = 0; off <= 16 && off + 6 <= len; off++) {
        var bt = dv.getUint16(off, true);
        if (bt !== WBT_SYNC && bt !== WBT_FRAME_BEGIN && bt !== WBT_CONTEXT &&
            bt !== WBT_REGION && bt !== WBT_FRAME_END) continue;
        var bl = dv.getUint32(off + 2, true);
        if (bl < 6 || off + bl > len) continue;
        // SYNC is the canonical first block (blockLen 12); accept it directly, else accept any known.
        if (bt === WBT_SYNC && bl !== 12) continue;
        return off;
    }
    return -1;
}

function decodeStream(ctx, data, onTile, log, verbose) {
    var dv = new DataView(data.buffer, data.byteOffset, data.byteLength);
    var pos = 0, len = data.length;
    var tilesOut = 0, frames = 0;
    var region = null;
    var contextFlags = 0;

    function u8() { return data[pos++]; }
    function u16() { var v = dv.getUint16(pos, true); pos += 2; return v; }
    function u32() { var v = dv.getUint32(pos, true); pos += 4; return v; }

    // GNOME Remote Desktop prefixes the RFX_PROGRESSIVE block stream with a small per-frame preamble
    // (observed: 4 bytes "XX ba 00 00") before the first WBT_SYNC. Scan the first few offsets for a
    // valid block header (known type + sane blockLen) and start there, so we're robust to that preamble.
    pos = findBlockStart(data, dv, len);
    if (pos < 0) { if (log) log("progressive: no valid block header found"); return null; }

    while (pos + 6 <= len) {
        var blockType = u16();
        var blockLen = u32();
        if (blockLen < 6 || pos + (blockLen - 6) > len) {
            if (log) log("progressive: bad blockLen " + blockLen + " type 0x" + blockType.toString(16));
            break;
        }
        var blockEnd = pos - 6 + blockLen;

        switch (blockType) {
            case WBT_SYNC:
                pos = blockEnd; break;
            case WBT_FRAME_BEGIN:
                // frameIndex(4) regionCount(2)
                pos = blockEnd; frames++; break;
            case WBT_FRAME_END:
                pos = blockEnd; break;
            case WBT_CONTEXT: {
                // ctxId(1) tileSize(2) flags(1)
                /* ctxId */ u8();
                /* tileSize */ u16();
                contextFlags = u8();
                pos = blockEnd; break;
            }
            case WBT_REGION: {
                region = parseRegion(data, pos, blockEnd, dv);
                if (!region) { if (log) log("progressive: region parse failed"); return null; }
                if (verbose && log) {
                    log("progressive: region flags=0x" + region.flags.toString(16) + " numTiles=" + region.numTiles +
                        " numQuant=" + region.numQuant + " numProgQuant=" + region.numProgQuant);
                }
                // parseRegion advanced pos to the tile data; process tiles inline.
                processTiles(ctx, region, contextFlags, onTile, log, verbose);
                tilesOut += region.tileCount;
                pos = blockEnd; break;
            }
            default:
                if (log) log("progressive: unknown block 0x" + blockType.toString(16));
                pos = blockEnd; break;
        }
    }
    return { tiles: tilesOut, frames: frames };
}

// Parse a REGION block; returns a region descriptor with quant tables + the tile-data slice.
function parseRegion(data, start, end, dv) {
    var p = start;
    function r8() { return data[p++]; }
    function r16() { var v = dv.getUint16(p, true); p += 2; return v; }
    function r32() { var v = dv.getUint32(p, true); p += 4; return v; }

    var tileSize = r8();
    var numRects = r16();
    var numQuant = r8();
    var numProgQuant = r8();
    var flags = r8();
    var numTiles = r16();
    var tileDataSize = r32();
    if (tileSize !== 64 || numRects < 1) return null;

    var rects = [];
    for (var i = 0; i < numRects; i++) {
        rects.push({ x: r16(), y: r16(), w: r16(), h: r16() });
    }
    var quants = [];
    for (var q = 0; q < numQuant; q++) { quants.push(readQuant(data, p)); p += 5; }
    var progQuants = [];
    for (var pq = 0; pq < numProgQuant; pq++) {
        var quality = r8();
        var yq = readQuant(data, p); p += 5;
        var cq = readQuant(data, p); p += 5;
        var rq = readQuant(data, p); p += 5;
        progQuants.push({ quality: quality, y: yq, cb: cq, cr: rq });
    }
    return {
        flags: flags, numQuant: numQuant, numProgQuant: numProgQuant,
        quants: quants, progQuants: progQuants,
        rects: rects, numTiles: numTiles, tileCount: numTiles,
        tileDataStart: p, tileDataSize: tileDataSize,
    };
}

// Walk the tile-data blocks of a region and reconstruct each SIMPLE/FIRST tile.
function processTiles(ctx, region, contextFlags, onTile, log, verbose) {
    var data = ctx._streamData;
    var dv = ctx._streamDv;
    var p = region.tileDataStart;
    var end = region.tileDataStart + region.tileDataSize;
    var coeffDiffSub = (contextFlags & RFX_SUBBAND_DIFFING) !== 0;
    var extrapolate = (region.flags & RFX_DWT_REDUCE_EXTRAPOLATE) !== 0;

    function r8() { return data[p++]; }
    function r16() { var v = dv.getUint16(p, true); p += 2; return v; }
    function r32() { var v = dv.getUint32(p, true); p += 4; return v; }

    while (p + 6 <= end) {
        var blockType = r16();
        var blockLen = r32();
        if (blockLen < 6) break;
        var bEnd = p - 6 + blockLen;
        if (bEnd > end) break;

        if (blockType === WBT_TILE_SIMPLE || blockType === WBT_TILE_FIRST) {
            var simple = (blockType === WBT_TILE_SIMPLE);
            var quantIdxY = r8(), quantIdxCb = r8(), quantIdxCr = r8();
            var xIdx = r16(), yIdx = r16();
            var tflags = r8();
            var quality = simple ? 0xFF : r8();
            var yLen = r16(), cbLen = r16(), crLen = r16(), tailLen = r16();
            var yData = p; p += yLen;
            var cbData = p; p += cbLen;
            var crData = p; p += crLen;
            /* tailData */ p += tailLen;

            if (verbose && log) {
                log("progressive: tile " + (simple ? "SIMPLE" : "FIRST") + " x=" + xIdx + " y=" + yIdx +
                    " quality=" + quality + " diff=" + ((tflags & RFX_TILE_DIFFERENCE) !== 0) +
                    " yLen=" + yLen + " cbLen=" + cbLen + " crLen=" + crLen);
            }
            if (quantIdxY < region.numQuant && quantIdxCb < region.numQuant && quantIdxCr < region.numQuant) {
                reconstructTile(ctx, region, xIdx, yIdx, tflags, quality,
                    region.quants[quantIdxY], region.quants[quantIdxCb], region.quants[quantIdxCr],
                    data, yData, yLen, cbData, cbLen, crData, crLen, extrapolate, onTile, log);
            } else if (log) {
                log("progressive: tile " + xIdx + "," + yIdx + " quantIdx out of range (Y=" + quantIdxY +
                    " Cb=" + quantIdxCb + " Cr=" + quantIdxCr + " numQuant=" + region.numQuant +
                    ") — tile SKIPPED, will be BLACK/stale");
            }
            p = bEnd;
        } else if (blockType === WBT_TILE_UPGRADE) {
            // SRL/RAW progressive refinement pass (progressive_tile_read_upgrade): header is 20 bytes,
            // then the 6 per-component SRL/RAW blobs.
            var uQY = r8(), uQCb = r8(), uQCr = r8();
            var uX = r16(), uY = r16();
            var uQuality = r8();
            var ySrlLen = r16(), yRawLen = r16(), cbSrlLen = r16(), cbRawLen = r16(), crSrlLen = r16(), crRawLen = r16();
            var ySrl = p; p += ySrlLen;
            var yRaw = p; p += yRawLen;
            var cbSrl = p; p += cbSrlLen;
            var cbRaw = p; p += cbRawLen;
            var crSrl = p; p += crSrlLen;
            var crRaw = p; p += crRawLen;
            if (verbose && log) {
                log("progressive: tile UPGRADE x=" + uX + " y=" + uY + " quality=" + uQuality +
                    " ySrl=" + ySrlLen + " yRaw=" + yRawLen);
            }
            if (uQY < region.numQuant && uQCb < region.numQuant && uQCr < region.numQuant && p <= bEnd) {
                ctx._blobs[0] = ySrl;  ctx._blobs[1] = ySrlLen;  ctx._blobs[2] = yRaw;  ctx._blobs[3] = yRawLen;
                ctx._blobs[4] = cbSrl; ctx._blobs[5] = cbSrlLen; ctx._blobs[6] = cbRaw; ctx._blobs[7] = cbRawLen;
                ctx._blobs[8] = crSrl; ctx._blobs[9] = crSrlLen; ctx._blobs[10] = crRaw; ctx._blobs[11] = crRawLen;
                upgradeTile(ctx, region, uX, uY, uQuality,
                    region.quants[uQY], region.quants[uQCb], region.quants[uQCr],
                    data, ctx._blobs, extrapolate, onTile, log);
            } else if (log) {
                log("progressive: UPGRADE tile " + uX + "," + uY + " quantIdx out of range or truncated (p=" + p +
                    " bEnd=" + bEnd + ") — upgrade SKIPPED, tile stays at prior quality");
            }
            p = bEnd;
        } else {
            p = bEnd;
        }
    }
}

function reconstructTile(ctx, region, xIdx, yIdx, tflags, quality, qY, qCb, qCr,
                         data, yOff, yLen, cbOff, cbLen, crOff, crLen, extrapolate, onTile, log) {
    var coeffDiff = (tflags & RFX_TILE_DIFFERENCE) !== 0;
    var prog = progQuantFor(region, quality, log);
    if (prog === undefined) {
        if (log) log("progressive: tile " + xIdx + "," + yIdx + " invalid progQuant — SKIPPED, tile will be BLACK/stale");
        return;
    }
    var pY = prog ? prog.y : QUANT_ZERO, pCb = prog ? prog.cb : QUANT_ZERO, pCr = prog ? prog.cr : QUANT_ZERO;
    var shY = quantShift(qY, pY), shCb = quantShift(qCb, pCb), shCr = quantShift(qCr, pCr);
    var cell = ctx._tileCell(xIdx, yIdx);

    var yData = data.subarray(yOff, yOff + yLen);
    var cbData = data.subarray(cbOff, cbOff + cbLen);
    var crData = data.subarray(crOff, crOff + crLen);

    decodeComponent(1, yData, yLen, ctx.scratchY, cell.cur[0], cell.sign[0], ctx.idwt, shY, coeffDiff, extrapolate);
    decodeComponent(1, cbData, cbLen, ctx.scratchCb, cell.cur[1], cell.sign[1], ctx.idwt, shCb, coeffDiff, extrapolate);
    decodeComponent(1, crData, crLen, ctx.scratchCr, cell.cur[2], cell.sign[2], ctx.idwt, shCr, coeffDiff, extrapolate);

    // bitPos = quant + progQuant per component: how many bits each band is still missing; UPGRADE
    // passes deliver (oldBitPos - newBitPos) bits per coefficient.
    cell.bitPos = [quantAdd(qY, pY), quantAdd(qCb, pCb), quantAdd(qCr, pCr)];

    ycbcrToRgba(ctx.scratchY, ctx.scratchCb, ctx.scratchCr, ctx.rgba);
    onTile(xIdx, yIdx, ctx.rgba, region.rects);
}

// =================================================================================================
// UPGRADE pass (progressive_decompress_tile_upgrade): each pass adds numBits low-order bits to every
// coefficient in 'current'. Coefficients whose sign is already known (sign != 0) read their bits from
// the RAW stream; still-zero coefficients read from the SRL (sign+run-length) stream, which also
// reveals their sign. The LL3 band is all-RAW. Afterwards pixels are rebuilt from 'current' (the
// "reverse" DWT path — 'current' itself stays in coefficient domain).
// Band offsets here are the extrapolate layout unconditionally, mirroring FreeRDP (Windows hosts
// only emit upgrades with REDUCE_EXTRAPOLATE set).
// =================================================================================================
function srlRead(state, numBits) {
    var bs = state.srl;
    if (state.nz) { state.nz--; return 0; }
    var k = state.kp >> 3;
    if (!state.mode) {
        // zero encoding
        var bit = (bs.accumulator & 0x80000000) ? 1 : 0;
        bs.shift(1);
        if (!bit) {
            // '0' bit: nz = (1 << k)
            state.nz = (1 << k);
            state.kp += 4; if (state.kp > 80) state.kp = 80;
            state.nz--;
            return 0;
        } else {
            // '1' bit: nz = next k bits, then unary
            state.nz = 0;
            state.mode = 1;
            if (k) {
                state.nz = (bs.accumulator >>> (32 - k)) & ((1 << k) - 1);
                bs.shift(k);
            }
            if (state.nz) { state.nz--; return 0; }
        }
    }
    state.mode = 0;
    // unary encoding: sign bit, then count zeros until a 1 (capped at (1<<numBits)-1)
    var sign = (bs.accumulator & 0x80000000) ? 1 : 0;
    bs.shift(1);
    state.kp = state.kp < 6 ? 0 : state.kp - 6;
    if (numBits === 1) return sign ? -1 : 1;
    var mag = 1, max = (1 << numBits) - 1;
    while (mag < max) {
        var b = (bs.accumulator & 0x80000000) ? 1 : 0;
        bs.shift(1);
        if (b) break;
        mag++;
    }
    return sign ? -mag : mag;
}

function rawRead(raw, numBits) {
    var v = (raw.accumulator >>> (32 - numBits)) & ((1 << numBits) - 1);
    raw.shift(numBits);
    return v;
}

function upgradeBlock(state, current, sign, off, length, shift, numBits, nonLL) {
    if (numBits < 1) return;
    var raw = state.raw;
    if (!nonLL) {
        for (var i = 0; i < length; i++) {
            var v = rawRead(raw, numBits);
            current[off + i] = clampi16(current[off + i] + (v << shift));
        }
        return;
    }
    for (var j = 0; j < length; j++) {
        var input;
        var s = sign[off + j];
        if (s > 0) input = rawRead(raw, numBits);
        else if (s < 0) input = -rawRead(raw, numBits);
        else { input = srlRead(state, numBits); sign[off + j] = clampi16(input); }
        current[off + j] = clampi16(current[off + j] + (input << shift));
    }
}

function upgradeComponent(ctx, shift, numBits, srcDst, current, sign, data, srlOff, srlLen, rawOff, rawLen, extrapolate) {
    var state = {
        kp: 8, mode: 0, nz: 0,
        srl: new BitStream(data.subarray(srlOff, srlOff + srlLen), srlLen),
        raw: new BitStream(data.subarray(rawOff, rawOff + rawLen), rawLen),
    };
    upgradeBlock(state, current, sign, 0,    1023, shift.HL1, numBits.HL1, true);
    upgradeBlock(state, current, sign, 1023, 1023, shift.LH1, numBits.LH1, true);
    upgradeBlock(state, current, sign, 2046, 961,  shift.HH1, numBits.HH1, true);
    upgradeBlock(state, current, sign, 3007, 272,  shift.HL2, numBits.HL2, true);
    upgradeBlock(state, current, sign, 3279, 272,  shift.LH2, numBits.LH2, true);
    upgradeBlock(state, current, sign, 3551, 256,  shift.HH2, numBits.HH2, true);
    upgradeBlock(state, current, sign, 3807, 72,   shift.HL3, numBits.HL3, true);
    upgradeBlock(state, current, sign, 3879, 72,   shift.LH3, numBits.LH3, true);
    upgradeBlock(state, current, sign, 3951, 64,   shift.HH3, numBits.HH3, true);
    upgradeBlock(state, current, sign, 4015, 81,   shift.LL3, numBits.LL3, false);
    // reverse DWT: rebuild pixels from the upgraded coefficients
    srcDst.set(current.subarray(0, 4096));
    inverseDwt(srcDst, ctx.idwt, extrapolate);
}

function upgradeTile(ctx, region, xIdx, yIdx, quality, qY, qCb, qCr, data, blobs, extrapolate, onTile, log) {
    var cell = ctx._tileCell(xIdx, yIdx);
    if (!cell.bitPos) {
        // Logged per-tile-cell (not just once globally) — an UPGRADE with no prior FIRST leaves this
        // specific tile stuck at whatever it last rendered (often nothing = black), so seeing which
        // coordinates repeatedly hit this is the key signal for chasing stale tiles.
        if (log && !cell._loggedUpgNoFirst) {
            cell._loggedUpgNoFirst = 1;
            log("progressive: UPGRADE for tile " + xIdx + "," + yIdx + " without FIRST — skipped, tile stays BLACK/stale");
        }
        return;
    }
    var prog = progQuantFor(region, quality, log);
    if (prog === undefined) {
        if (log) log("progressive: UPGRADE tile " + xIdx + "," + yIdx + " invalid progQuant — skipped");
        return;
    }
    var progs = [prog ? prog.y : QUANT_ZERO, prog ? prog.cb : QUANT_ZERO, prog ? prog.cr : QUANT_ZERO];
    var quants = [qY, qCb, qCr];
    var scratch = [ctx.scratchY, ctx.scratchCb, ctx.scratchCr];
    for (var c = 0; c < 3; c++) {
        var newBitPos = quantAdd(quants[c], progs[c]);
        var numBits = quantSub(cell.bitPos[c], newBitPos);
        var shift = quantShift(quants[c], progs[c]);
        upgradeComponent(ctx, shift, numBits, scratch[c], cell.cur[c], cell.sign[c],
            data, blobs[c * 4], blobs[c * 4 + 1], blobs[c * 4 + 2], blobs[c * 4 + 3], extrapolate);
        cell.bitPos[c] = newBitPos;
    }
    ycbcrToRgba(ctx.scratchY, ctx.scratchCb, ctx.scratchCr, ctx.rgba);
    onTile(xIdx, yIdx, ctx.rgba, region.rects);
}

global.RfxProgressive = {
    Context: ProgressiveContext,
    // decode(ctx, payload Uint8Array, onTile(xIdx,yIdx,rgbaUint8ClampedArray,regionRects), log, verbose) -> {tiles,frames}|null
    // `verbose` enables per-tile/per-region tracing (block headers, quant indices, cache state) on top
    // of the always-on error logging, for chasing a specific black/stale tile back to its cause.
    decode: function (ctx, payload, onTile, log, verbose) {
        ctx._streamData = payload;
        ctx._streamDv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
        try {
            return decodeStream(ctx, payload, onTile, log, verbose);
        } catch (e) {
            if (log) log("progressive: decode exception: " + (e && e.message ? e.message : e));
            return null;
        }
    },
};

})(typeof window !== "undefined" ? window : globalThis);
