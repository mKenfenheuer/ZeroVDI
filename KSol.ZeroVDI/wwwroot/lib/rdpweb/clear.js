// CLEARCODEC (codecId 0x0008) decoder for MS-RDPEGFX — ported from FreeRDP `libfreerdp/codec/clear.c`
// (Apache-2.0). ClearCodec is what Windows hosts stream over the Graphics pipeline when H.264/AVC is
// not available (no server GPU / "Prioritize H.264/AVC 444" policy off). A single ClearCodec PDU
// produces the pixels for one WIRE_TO_SURFACE_1 dest rect, composed from up to four layers:
//   1. glyph cache       — an optional cached WxH tile (GLYPH_HIT) or a tile to be cached (GLYPH_INDEX)
//   2. residual data     — full-tile RGB run-length fill (the background layer)
//   3. bands data        — vertical bars (VBar) with their own 32768/16384-entry caches
//   4. subcodecs data    — per-region Uncompressed(0) / NSCodec(1) / RLEX(2) palettized runs
//
// We decode into a freshly allocated RGBA buffer sized to the dest rect (canvas-native R,G,B,A order)
// and hand it back to the caller. IMPORTANT: the layers need not cover the whole rect — pixels no
// layer wrote stay TRANSPARENT (alpha 0) and the caller MUST alpha-composite the buffer onto the
// surface (drawImage/source-over, see _finishClear in rdpgfx.js), never putImageData it, or the
// uncovered pixels stamp out as opaque black. That mirrors FreeRDP, which writes each layer straight
// onto the surface and leaves everything else untouched. The glyph/VBar caches live on the
// ClearDecode instance and persist across PDUs (reset on CACHE_RESET / session reset), exactly like
// FreeRDP's CLEAR_CONTEXT.

const CLEARCODEC_FLAG_GLYPH_INDEX = 0x01;
const CLEARCODEC_FLAG_GLYPH_HIT = 0x02;
const CLEARCODEC_FLAG_CACHE_RESET = 0x04;

const CLEARCODEC_VBAR_SIZE = 32768;
const CLEARCODEC_VBAR_SHORT_SIZE = 16384;
const CLEAR_GLYPH_CACHE_SIZE = 4000;

// log2(floor) table for paletteCount-1 → bit width ([MS-RDPEGFX]; FreeRDP CLEAR_LOG2_FLOOR).
const CLEAR_LOG2_FLOOR = (function () {
    const t = new Uint8Array(256);
    for (let i = 1; i < 256; i++) t[i] = 31 - Math.clz32(i); // floor(log2(i))
    return t;
})();
const CLEAR_8BIT_MASKS = [0x00, 0x01, 0x03, 0x07, 0x0f, 0x1f, 0x3f, 0x7f, 0xff];

function ClearDecode() {
    this.seqNumber = 0;
    // Glyph cache: each entry { pixels: Uint32Array(RGBA-packed), count, size }.
    this.glyphCache = new Array(CLEAR_GLYPH_CACHE_SIZE);
    // VBar caches: each entry { pixels: Uint32Array, count, size }.
    this.vbar = new Array(CLEARCODEC_VBAR_SIZE);
    this.vbarShort = new Array(CLEARCODEC_VBAR_SHORT_SIZE);
    this.vbarCursor = 0;
    this.vbarShortCursor = 0;
}

ClearDecode.prototype.reset = function () {
    this.seqNumber = 0;
    this.glyphCache = new Array(CLEAR_GLYPH_CACHE_SIZE);
    this.vbar = new Array(CLEARCODEC_VBAR_SIZE);
    this.vbarShort = new Array(CLEARCODEC_VBAR_SHORT_SIZE);
    this.vbarCursor = 0;
    this.vbarShortCursor = 0;
};

ClearDecode.prototype._resetVBars = function () {
    this.vbar = new Array(CLEARCODEC_VBAR_SIZE);
    this.vbarShort = new Array(CLEARCODEC_VBAR_SHORT_SIZE);
    this.vbarCursor = 0;
    this.vbarShortCursor = 0;
};

// Pack/unpack helpers for the internal Uint32 color (0xAABBGGRR — little-endian RGBA, matching how the
// output Uint8Array R,G,B,A bytes are read as one LE uint32). We standardize on RGBA throughout.
function packRGBA(r, g, b, a) { return ((a << 24) | (b << 16) | (g << 8) | r) >>> 0; }

// Decode a ClearCodec bitmap into an RGBA Uint8ClampedArray sized width*height*4 (top-down). Returns
// { rgba, width, height } or null on error. `data` is the WIRE_TO_SURFACE bitmapData (Uint8Array).
// `verbose`, when truthy, additionally logs per-tile cache hit/miss and layer composition detail — this
// is the tracing needed to root-cause a specific black/stale tile (which layer/cache slot produced it),
// as opposed to the always-on error logging below which only fires on malformed/unexpected data.
ClearDecode.prototype.decode = function (data, width, height, log, verbose) {
    const r = new ByteReader(data);
    if (r.remaining() < 2) return null;
    const glyphFlags = r.u8();
    const seqNumber = r.u8();

    if (!this.seqNumber && seqNumber) this.seqNumber = seqNumber;
    if (seqNumber !== this.seqNumber) {
        // FreeRDP treats this as a fatal decode error and tears down/reconnects the whole session (see
        // clear_decompress in libfreerdp/codec/clear.c) — we have no equivalent "abort and reconnect on
        // a bad GFX tile" path, and dropping the PDU here used to mean the destination rect silently
        // never repaints again: once this.seqNumber diverges from the host (e.g. one lost/reordered
        // PDU), EVERY later ClearCodec PDU fails this check forever, leaving permanent stale rectangles
        // on screen. Resync instead: accept the host's seqNumber as the new expected value and keep
        // decoding this PDU. We lose one frame of validation, not indefinitely broken graphics.
        if (log) log("clear: seqNumber " + seqNumber + " != expected " + this.seqNumber + " — resyncing");
        this.seqNumber = seqNumber;
    }
    this.seqNumber = (seqNumber + 1) & 0xff;

    if (glyphFlags & CLEARCODEC_FLAG_CACHE_RESET) this._resetVBars();

    // Output framebuffer for this tile (RGBA, top-down). nDstStep = width*4; origin (0,0).
    const out = new Uint8ClampedArray(width * height * 4);
    const out32 = new Uint32Array(out.buffer);

    // --- glyph layer -----------------------------------------------------------------------------
    let glyphEntry = null; // set when GLYPH_INDEX (we must cache the final tile after composing)
    if (glyphFlags & CLEARCODEC_FLAG_GLYPH_INDEX || glyphFlags & CLEARCODEC_FLAG_GLYPH_HIT) {
        if ((glyphFlags & CLEARCODEC_FLAG_GLYPH_HIT) && !(glyphFlags & CLEARCODEC_FLAG_GLYPH_INDEX)) {
            if (log) log("clear: invalid glyph flags 0x" + glyphFlags.toString(16));
            return null;
        }
        if (glyphFlags & CLEARCODEC_FLAG_GLYPH_INDEX) {
            if (r.remaining() < 2) return null;
            const glyphIndex = r.u16le();
            if (glyphIndex >= CLEAR_GLYPH_CACHE_SIZE) return null;

            if (glyphFlags & CLEARCODEC_FLAG_GLYPH_HIT) {
                // GLYPH_HIT: copy the cached tile straight to the output and we're done.
                const e = this.glyphCache[glyphIndex];
                const count = width * height;
                if (!e || !e.pixels || count > e.count) {
                    if (log) log("clear: glyph hit miss/too-small idx=" + glyphIndex + " " + width + "x" + height +
                        " cached=" + (e ? e.count : "none") + " — tile will be BLACK");
                    return null;
                }
                if (verbose && log) log("clear: glyph HIT idx=" + glyphIndex + " " + width + "x" + height);
                out32.set(e.pixels.subarray(0, count));
                return { rgba: out, width, height };
            }
            // GLYPH_INDEX (no hit): compose the tile below, then cache it into this slot.
            const count = width * height;
            let e = this.glyphCache[glyphIndex];
            if (!e || count > e.size) { e = { pixels: new Uint32Array(count), count, size: count }; this.glyphCache[glyphIndex] = e; }
            e.count = count;
            glyphEntry = e;
            if (verbose && log) log("clear: glyph STORE idx=" + glyphIndex + " " + width + "x" + height);
        }
    }

    // --- composition header ----------------------------------------------------------------------
    if (r.remaining() < 12) {
        // If both glyph flags set, the tile came entirely from a GLYPH_HIT (handled above). Otherwise
        // a short tile with GLYPH_INDEX|HIT both set is valid and already returned; here it's an error
        // unless both flags are set (FreeRDP allows that early-out).
        const mask = CLEARCODEC_FLAG_GLYPH_HIT | CLEARCODEC_FLAG_GLYPH_INDEX;
        if ((glyphFlags & mask) === mask) { if (glyphEntry) glyphEntry.pixels.set(out32.subarray(0, width * height)); return { rgba: out, width, height, glyphEntry }; }
        if (log) log("clear: short composition header (remaining=" + r.remaining() + ")");
        return null;
    }
    const residualByteCount = r.u32le();
    const bandsByteCount = r.u32le();
    const subcodecByteCount = r.u32le();

    if (verbose && log) {
        log("clear: tile " + width + "x" + height + " seq=" + seqNumber +
            " residual=" + residualByteCount + " bands=" + bandsByteCount + " subcodec=" + subcodecByteCount);
    }

    if (residualByteCount > 0 && !this._residual(r, residualByteCount, width, height, out32, log)) return null;
    if (bandsByteCount > 0 && !this._bands(r, bandsByteCount, width, height, out32, log, verbose)) return null;
    if (subcodecByteCount > 0 && !this._subcodecs(r, subcodecByteCount, width, height, out, out32, log, verbose)) return null;

    // Cache the tile if this was a GLYPH_INDEX (so a later GLYPH_HIT can re-use it). What's stored
    // here is only a provisional copy: layers need not cover every pixel, and FreeRDP caches the glyph
    // FROM THE DESTINATION SURFACE after composing (freerdp_image_copy out of pDstData), i.e. with the
    // pre-existing surface pixels baked into the uncovered spots. The caller MUST overwrite
    // glyphEntry.pixels with the composed surface rect after painting (see rdpgfx.js _finishClear) —
    // otherwise a later GLYPH_HIT replays alpha-0 holes and the host/client pixel models diverge
    // (visible as thin never-repainted strips of stale window chrome).
    if (glyphEntry) glyphEntry.pixels.set(out32.subarray(0, width * height));

    return { rgba: out, width, height, glyphEntry };
};

// Residual layer: BGR + runLength runs filling the whole tile in row-major order.
ClearDecode.prototype._residual = function (r, byteCount, width, height, out32, log) {
    let suboffset = 0;
    let pixelIndex = 0;
    const pixelCount = width * height;
    while (suboffset < byteCount) {
        if (r.remaining() < 4) return false;
        const b = r.u8(), g = r.u8(), rr = r.u8();
        let runLengthFactor = r.u8();
        suboffset += 4;
        if (runLengthFactor >= 0xff) {
            if (r.remaining() < 2) return false;
            runLengthFactor = r.u16le(); suboffset += 2;
            if (runLengthFactor >= 0xffff) { if (r.remaining() < 4) return false; runLengthFactor = r.u32le(); suboffset += 4; }
        }
        if (pixelIndex >= pixelCount || runLengthFactor > pixelCount - pixelIndex) {
            if (log) log("clear: residual run overflow");
            return false;
        }
        const color = packRGBA(rr, g, b, 0xff);
        out32.fill(color, pixelIndex, pixelIndex + runLengthFactor);
        pixelIndex += runLengthFactor;
    }
    // [MS-RDPEGFX] 2.2.4.1.1.1: the residual layer's pixel count "MUST be less than or equal to" the
    // image's — underfill is legal (the rest is covered by bands/subcodecs or simply left untouched).
    // Failing here dropped the whole PDU and left the dest rect stale (visible as drag trails / ghost
    // window frames). Uncovered pixels stay transparent and the caller's composite leaves them as-is.
    return true;
};

// Bands layer: vertical bars. Each band spans columns [xStart..xEnd] at rows [yStart..yEnd], built from
// per-column VBar entries (short-vbar cache + full-vbar cache), then written column-by-column to the tile.
ClearDecode.prototype._bands = function (r, byteCount, width, height, out32, log, verbose) {
    let suboffset = 0;
    while (suboffset < byteCount) {
        if (r.remaining() < 11) return false;
        const xStart = r.u16le(), xEnd = r.u16le(), yStart = r.u16le(), yEnd = r.u16le();
        const cb = r.u8(), cg = r.u8(), cr = r.u8();
        suboffset += 11;
        if (xEnd < xStart || yEnd < yStart) return false;
        const colorBkg = packRGBA(cr, cg, cb, 0xff);
        const vBarCount = (xEnd - xStart) + 1;
        const vBarHeight = (yEnd - yStart + 1);
        if (vBarHeight > 52) { if (log) log("clear: vBarHeight>52"); return false; }
        if (verbose && log) log("clear: band x=[" + xStart + "," + xEnd + "] y=[" + yStart + "," + yEnd + "] vBarCount=" + vBarCount);

        for (let i = 0; i < vBarCount; i++) {
            if (r.remaining() < 2) return false;
            const vBarHeader = r.u16le(); suboffset += 2;
            let vBarEntry = null, vBarShortEntry = null, vBarUpdate = false;
            let vBarYOn = 0, vBarShortPixelCount = 0;

            if ((vBarHeader & 0xc000) === 0x4000) {        // SHORT_VBAR_CACHE_HIT
                const idx = vBarHeader & 0x3fff;
                vBarShortEntry = this.vbarShort[idx];
                if (!vBarShortEntry) { if (log) log("clear: missing shortVBar " + idx); return false; }
                if (r.remaining() < 1) return false;
                vBarYOn = r.u8(); suboffset += 1;
                vBarShortPixelCount = vBarShortEntry.count;
                vBarUpdate = true;
            } else if ((vBarHeader & 0xc000) === 0x0000) { // SHORT_VBAR_CACHE_MISS
                vBarYOn = vBarHeader & 0xff;
                const vBarYOff = (vBarHeader >> 8) & 0x3f;
                if (vBarYOff < vBarYOn) return false;
                vBarShortPixelCount = vBarYOff - vBarYOn;
                if (vBarShortPixelCount > 52) return false;
                if (r.remaining() < vBarShortPixelCount * 3) return false;
                const e = { pixels: new Uint32Array(vBarShortPixelCount), count: vBarShortPixelCount, size: vBarShortPixelCount };
                for (let y = 0; y < vBarShortPixelCount; y++) {
                    const b = r.u8(), g = r.u8(), rr = r.u8();
                    e.pixels[y] = packRGBA(rr, g, b, 0xff);
                }
                this.vbarShort[this.vbarShortCursor] = e;
                vBarShortEntry = e;
                suboffset += vBarShortPixelCount * 3;
                this.vbarShortCursor = (this.vbarShortCursor + 1) % CLEARCODEC_VBAR_SHORT_SIZE;
                vBarUpdate = true;
            } else if ((vBarHeader & 0x8000) === 0x8000) { // VBAR_CACHE_HIT
                const idx = vBarHeader & 0x7fff;
                vBarEntry = this.vbar[idx];
                if (!vBarEntry || vBarEntry.size === 0) {
                    // Empty cache slot — fill dummy (matches FreeRDP's warn+fill path). This is a known
                    // black/stale-tile cause: a VBAR_CACHE_HIT referencing a slot we never populated
                    // (cache desync, e.g. from a prior seqNumber resync or CACHE_RESET race) silently
                    // produces a blank column instead of the intended pixels.
                    if (log) log("clear: VBAR_CACHE_HIT on empty/undersized slot " + idx +
                        " (col " + i + " of " + vBarCount + ", x=" + (xStart + i) + ") — filling blank, expect stale/black column");
                    vBarEntry = { pixels: new Uint32Array(vBarHeight), count: vBarHeight, size: vBarHeight };
                    this.vbar[idx] = vBarEntry;
                } else if (verbose && log) {
                    log("clear: VBar HIT idx=" + idx + " col=" + i + " h=" + vBarHeight);
                }
            } else {
                if (log) log("clear: invalid vBarHeader 0x" + vBarHeader.toString(16));
                return false;
            }

            if (vBarUpdate) {
                const e = { pixels: new Uint32Array(vBarHeight), count: vBarHeight, size: vBarHeight };
                let pos = 0;
                // rows [0, vBarYOn) → background
                let count = Math.min(vBarYOn, vBarHeight);
                for (let k = 0; k < count; k++) e.pixels[pos++] = colorBkg;
                // rows [vBarYOn, vBarYOn+shortPixelCount) → short vbar pixels
                count = vBarShortPixelCount;
                if (vBarYOn + count > vBarHeight) count = Math.max(0, vBarHeight - vBarYOn);
                for (let k = 0; k < count; k++) e.pixels[pos++] = vBarShortEntry.pixels[k];
                // remaining rows → background
                while (pos < vBarHeight) e.pixels[pos++] = colorBkg;
                this.vbar[this.vbarCursor] = e;
                this.vbarCursor = (this.vbarCursor + 1) % CLEARCODEC_VBAR_SIZE;
                vBarEntry = e;
            }

            if (vBarEntry.count !== vBarHeight) {
                // Normalize (FreeRDP resizes); pad/truncate to vBarHeight.
                const fixed = new Uint32Array(vBarHeight);
                fixed.set(vBarEntry.pixels.subarray(0, Math.min(vBarEntry.count, vBarHeight)));
                vBarEntry = { pixels: fixed, count: vBarHeight, size: vBarHeight };
            }

            // Write this column to the tile at (xStart+i, yStart+y).
            if (i < width) {
                const xCol = xStart + i;
                let cnt = Math.min(vBarEntry.count, height);
                for (let y = 0; y < cnt; y++) {
                    const yy = yStart + y;
                    if (xCol < width && yy < height) out32[yy * width + xCol] = vBarEntry.pixels[y];
                }
            }
        }
    }
    return true;
};

// Subcodecs layer: per-region tiles via Uncompressed(0) / NSCodec(1) / RLEX(2).
ClearDecode.prototype._subcodecs = function (r, byteCount, nWidth, nHeight, out, out32, log, verbose) {
    let suboffset = 0;
    while (suboffset < byteCount) {
        if (r.remaining() < 13) return false;
        const xStart = r.u16le(), yStart = r.u16le(), width = r.u16le(), height = r.u16le();
        const bitmapDataByteCount = r.u32le();
        const subcodecId = r.u8();
        suboffset += 13;
        if (r.remaining() < bitmapDataByteCount) return false;
        if (xStart + width > nWidth || yStart + height > nHeight) {
            if (log) log("clear: subcodec region out of bounds x=" + xStart + " y=" + yStart +
                " " + width + "x" + height + " tile=" + nWidth + "x" + nHeight);
            return false;
        }
        if (verbose && log) {
            log("clear: subcodec id=" + subcodecId + " region x=" + xStart + " y=" + yStart +
                " " + width + "x" + height + " bytes=" + bitmapDataByteCount);
        }

        const region = r.bytes(bitmapDataByteCount); // detaches a view into the source
        if (subcodecId === 0) {                       // Uncompressed BGR24, top-down
            const need = width * height * 3;
            if (bitmapDataByteCount !== need) {
                if (log) log("clear: uncompressed region size mismatch got=" + bitmapDataByteCount + " need=" + need);
                return false;
            }
            let p = 0;
            for (let y = 0; y < height; y++) {
                let di = ((yStart + y) * nWidth + xStart);
                for (let x = 0; x < width; x++) {
                    const b = region[p++], g = region[p++], rr = region[p++];
                    out32[di++] = packRGBA(rr, g, b, 0xff);
                }
            }
        } else if (subcodecId === 2) {                // RLEX palettized runs
            if (!this._rlex(region, width, height, xStart, yStart, nWidth, nHeight, out32, log)) {
                if (log) log("clear: RLEX region x=" + xStart + " y=" + yStart + " " + width + "x" + height +
                    " decode failed — tile will be BLACK/stale at this region");
                return false;
            }
        } else if (subcodecId === 1) {                // NSCodec (YCoCg + RLE)
            if (!this._nscodec(region, width, height, xStart, yStart, nWidth, nHeight, out32, log)) {
                if (log) log("clear: NSCodec region x=" + xStart + " y=" + yStart + " " + width + "x" + height +
                    " decode failed — region left unchanged (stale)");
                // Non-fatal: leave the region as-is and continue with the rest of the tile.
            }
        } else {
            if (log) log("clear: unknown subcodecId " + subcodecId + " region " + width + "x" + height);
            return false;
        }
        suboffset += bitmapDataByteCount;
    }
    return true;
};

// RLEX subcodec ([MS-RDPEGFX] 3.1.8.2.3): a palette then palettized run + suite (gradient) runs.
ClearDecode.prototype._rlex = function (data, width, height, nXRel, nYRel, nWidth, nHeight, out32, log) {
    const r = new ByteReader(data);
    if (r.remaining() < 1) return false;
    const paletteCount = r.u8();
    let bitmapDataOffset = 1 + paletteCount * 3;
    if (paletteCount < 1 || paletteCount > 127) return false;
    if (r.remaining() < paletteCount * 3) return false;
    const palette = new Uint32Array(128);
    for (let i = 0; i < paletteCount; i++) {
        const b = r.u8(), g = r.u8(), rr = r.u8();
        palette[i] = packRGBA(rr, g, b, 0xff);
    }
    let pixelIndex = 0;
    const pixelCount = width * height;
    const numBits = CLEAR_LOG2_FLOOR[paletteCount - 1] + 1;
    let x = 0, y = 0;

    const put = (color) => {
        const xx = nXRel + x, yy = nYRel + y;
        if (xx < nWidth && yy < nHeight) out32[yy * nWidth + xx] = color;
        if (++x >= width) { y++; x = 0; }
    };

    while (bitmapDataOffset < data.length) {
        if (r.remaining() < 2) return false;
        const tmp = r.u8();
        let runLengthFactor = r.u8();
        bitmapDataOffset += 2;
        const suiteDepth = (tmp >> numBits) & CLEAR_8BIT_MASKS[8 - numBits];
        const stopIndex = tmp & CLEAR_8BIT_MASKS[numBits];
        const startIndex = stopIndex - suiteDepth;
        if (runLengthFactor >= 0xff) {
            if (r.remaining() < 2) return false;
            runLengthFactor = r.u16le(); bitmapDataOffset += 2;
            if (runLengthFactor >= 0xffff) { if (r.remaining() < 4) return false; runLengthFactor = r.u32le(); bitmapDataOffset += 4; }
        }
        if (startIndex >= paletteCount || stopIndex >= paletteCount) return false;
        let suiteIndex = startIndex;
        const color = palette[suiteIndex];
        if (pixelIndex + runLengthFactor > pixelCount) return false;
        for (let i = 0; i < runLengthFactor; i++) put(color);
        pixelIndex += runLengthFactor;
        if (pixelIndex + (suiteDepth + 1) > pixelCount) return false;
        for (let i = 0; i <= suiteDepth; i++) {
            if (suiteIndex > 127) return false;
            put(palette[suiteIndex]);
            suiteIndex++;
        }
        pixelIndex += (suiteDepth + 1);
    }
    if (pixelIndex !== pixelCount) { if (log) log("clear: rlex underfill " + pixelIndex + "/" + pixelCount); return false; }
    return true;
};

// NSCodec subcodec ([MS-RDPNSC]; FreeRDP nsc.c). Header: 4× planeByteCount(u32), ColorLossLevel(u8),
// ChromaSubsamplingLevel(u8), reserved(u16), then the four RLE-compressed planes Y, Co, Cg, A. Decode:
// RLE-inflate each plane, then per pixel YCoCg→RGB with the colorloss shift and (if subsampled) 2×2
// chroma supersampling. Writes the region into out32 at (nXRel,nYRel). Returns false on malformed data.
ClearDecode.prototype._nscodec = function (data, width, height, nXRel, nYRel, nWidth, nHeight, out32, log) {
    const r = new ByteReader(data);
    if (r.remaining() < 20) return false;
    const planeByteCount = [r.u32le(), r.u32le(), r.u32le(), r.u32le()];
    const colorLossLevel = r.u8();
    const chroma = r.u8();
    r.skip(2); // reserved
    if (colorLossLevel < 1 || colorLossLevel > 7) return false;
    const shift = colorLossLevel - 1;

    const tempWidth = (width + 7) & ~7;       // ROUND_UP_TO 8
    const tempHeight = (height + 1) & ~1;     // ROUND_UP_TO 2
    const planeLen = tempWidth * tempHeight;  // max decoded plane size

    // OrgByteCount per plane (the decoded/unpacked size).
    const org = [width * height, width * height, width * height, width * height];
    if (chroma) {
        org[0] = tempWidth * height;
        org[1] = (tempWidth >> 1) * (tempHeight >> 1);
        org[2] = org[1];
    }

    // RLE planes are concatenated after the header. Inflate each into its own buffer.
    const planes = [new Uint8Array(planeLen), new Uint8Array(planeLen), new Uint8Array(planeLen), new Uint8Array(planeLen)];
    let off = r.o;
    const buf = data;
    for (let i = 0; i < 4; i++) {
        const planeSize = planeByteCount[i];
        if (off + planeSize > buf.length) return false;
        const rle = buf.subarray(off, off + planeSize);
        if (planeSize === 0) {
            planes[i].fill(0xff, 0, org[i]);          // absent plane → all 0xFF (opaque alpha / neutral)
        } else if (planeSize < org[i]) {
            if (!nscRleDecode(rle, planes[i], org[i])) return false;
        } else {
            planes[i].set(buf.subarray(off, off + org[i]));
        }
        off += planeSize;
    }

    const yP = planes[0], coP = planes[1], cgP = planes[2], aP = planes[3];
    for (let y = 0; y < height; y++) {
        let yi, ci;
        if (chroma) { yi = y * tempWidth; ci = (y >> 1) * (tempWidth >> 1); }
        else { yi = y * width; ci = y * width; }
        const ai = y * width;
        let co_i = ci, cg_i = ci;
        for (let x = 0; x < width; x++) {
            const yv = yP[yi + x];
            // Co/Cg are signed; FreeRDP reads them as int8 after the colorloss left-shift.
            let cov = ((coP[co_i] << shift) << 24) >> 24;  // sign-extend int8(value<<shift)
            let cgv = ((cgP[cg_i] << shift) << 24) >> 24;
            const r8 = yv + cov - cgv;
            const g8 = yv + cgv;
            const b8 = yv - cov - cgv;
            const xx = nXRel + x, yy = nYRel + y;
            if (xx < nWidth && yy < nHeight) {
                // Alpha is forced opaque: the surface is opaque (FreeRDP ignores NSCodec alpha there
                // too), and our caller alpha-composites the result — a decoded alpha < 255 would BLEND
                // this region with stale content instead of replacing it (translucent ghost squares).
                // Alpha 0 is reserved as the "layer didn't cover this pixel" sentinel.
                out32[yy * nWidth + xx] = packRGBA(
                    r8 < 0 ? 0 : r8 > 255 ? 255 : r8,
                    g8 < 0 ? 0 : g8 > 255 ? 255 : g8,
                    b8 < 0 ? 0 : b8 > 255 ? 255 : b8,
                    0xff);
            }
            // chroma advances every 2 luma columns when subsampled, else every column.
            if (chroma) { if (x & 1) { co_i++; cg_i++; } } else { co_i++; cg_i++; }
        }
    }
    return true;
};

// NSCodec RLE plane decode (FreeRDP nsc_rle_decode): the last 4 bytes are copied literally; runs use a
// repeated-byte marker. `out` must hold at least `originalSize` bytes. Returns false on malformed input.
function nscRleDecode(input, out, originalSize) {
    let inPos = 0, inSize = input.length;
    let outPos = 0;
    let left = originalSize;
    while (left > 4) {
        if (inSize < 1) return false;
        inSize--;
        const value = input[inPos++];
        if (left === 5) { out[outPos++] = value; left--; }
        else if (inSize < 1) return false;
        else if (value === input[inPos]) {       // run: value repeated
            inSize--; inPos++;
            if (inSize < 1) return false;
            let len;
            if (input[inPos] < 0xff) { inSize--; len = input[inPos++] + 2; }
            else {
                if (inSize < 5) return false;
                inSize -= 5; inPos++;             // skip the 0xFF marker
                len = (input[inPos] | (input[inPos + 1] << 8) | (input[inPos + 2] << 16) | (input[inPos + 3] << 24)) >>> 0;
                inPos += 4;
            }
            if (left < len) return false;
            out.fill(value, outPos, outPos + len);
            outPos += len; left -= len;
        } else { out[outPos++] = value; left--; } // single literal
    }
    if (left < 4 || inSize < 4) return false;
    out[outPos] = input[inPos]; out[outPos + 1] = input[inPos + 1];
    out[outPos + 2] = input[inPos + 2]; out[outPos + 3] = input[inPos + 3];
    return true;
}

if (typeof window !== "undefined") window.ClearDecode = ClearDecode;
if (typeof module !== "undefined") module.exports = { ClearDecode };
