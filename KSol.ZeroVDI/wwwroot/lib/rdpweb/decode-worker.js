// Dedicated Worker for RemoteFX Progressive (progressive.js) — a synchronous, CPU-bound, bit-exact
// port of FreeRDP with no DOM dependency in its decode math; only the caller (rdpgfx.js) touches
// canvas/putImageData. Running it here means a large progressive frame (hundreds of tiles) no longer
// blocks input/render on the main thread. ClearCodec deliberately does NOT run here: its glyph cache
// must be snapshotted from the composed destination surface (FreeRDP semantics), which only the main
// thread can read — see rdpgfx.js _decodeClear. Its tiles are small, so main-thread decode is cheap.
//
// Message ordering: the main thread posts one message per WIRE_TO_SURFACE_1/2 PDU, in PDU order, and
// this worker replies in the same order it receives them (no internal parallelism/reordering), so the
// main thread can paint replies as they arrive without extra sequencing — GFX PDUs for progressive and
// ClearCodec are never interleaved out of order by the host for the same surface, and painting via
// putImageData is inherently ordered by when we call it.
//
// H.264 (WebCodecs) ALSO runs here now (see below): although VideoDecoder.decode() is async, the
// bitstream parsing (SPS scan, Annex-B NAL split), the decode-queue bookkeeping, and — the real cost —
// the per-frame output callback + createImageBitmap all executed on the main thread, competing with
// input dispatch during heavy video. Decoding in the worker and transferring the finished VideoFrame
// back (zero-copy — VideoFrame is Transferable) leaves the main thread only a single drawImage(frame)
// per frame. WebCodecs VideoDecoder is available in Worker scope. Audio, camera, and all input handling
// still stay on the main thread: input requires DOM events (only fire on the main thread) and Web Audio
// is already async and hardware-backed.

importScripts("progressive.js");

const progCtx = {}; // surfaceId -> RfxProgressive.Context

// Intersect a (surface-clamped) tile rect [tx,ty)..(tr,tb) with the region's tileRects. Returns a
// list of [left,top,right,bottom] sub-rects to blit. If any rect fully covers the tile, that single
// full-tile clip is returned. Overlapping region rects can yield overlapping clips; the pixels are
// identical so double-painting is harmless. No rects at all (defensive) → paint the whole tile.
function clipTileToRects(tx, ty, tr, tb, rects) {
    if (!rects || !rects.length) return [[tx, ty, tr, tb]];
    const clips = [];
    for (let i = 0; i < rects.length; i++) {
        const rc = rects[i];
        const l = tx > rc.x ? tx : rc.x;
        const t = ty > rc.y ? ty : rc.y;
        const r = tr < rc.x + rc.w ? tr : rc.x + rc.w;
        const b = tb < rc.y + rc.h ? tb : rc.y + rc.h;
        if (r <= l || b <= t) continue;
        if (l === tx && t === ty && r === tr && b === tb) return [[tx, ty, tr, tb]];
        clips.push([l, t, r, b]);
    }
    return clips;
}

function log(m) { postMessage({ cmd: "log", message: m }); }

function decodeProgressive(msg) {
    const { reqId, surfaceId, surfWidth, surfHeight } = msg;
    // Keyed by surfaceId ONLY, matching FreeRDP (see the matching comment in rdpgfx.js's
    // _decodeProgressiveSyncInner): Windows 11 bumps codecContextId on every progressive PDU while
    // still sending diff-flagged FIRST tiles that accumulate onto the tile state from the previous
    // PDU — per-context keying decoded them against zeroed state (washed-out grey → poisoned cache).
    let ctx = progCtx[surfaceId];
    if (!ctx) { ctx = new RfxProgressive.Context(); progCtx[surfaceId] = ctx; }

    const tiles = [];
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    let anyClipped = false;
    let res = null, error = null;
    try {
        res = RfxProgressive.decode(ctx, new Uint8Array(msg.bitmapData), function (xIdx, yIdx, rgba, rects) {
            // NOTE: fires once per updated-this-GFX-frame tile per PDU (frame set accumulates across
            // PDUs sharing msg.frameId — FreeRDP update_tiles semantics; see progressive.js).
            const tx = xIdx * 64, ty = yIdx * 64;
            if (tx < 0 || ty < 0 || tx >= surfWidth || ty >= surfHeight) return;
            const tr = Math.min(tx + 64, surfWidth), tb = Math.min(ty + 64, surfHeight);
            // [MS-RDPEGFX] 2.2.4.2.1.5: a tile may only update pixels inside the REGION's tileRects
            // (FreeRDP progressive_decompress intersects each tile with their union). The 64x64 cell is
            // rebuilt from persistent coefficient state, so OUTSIDE the rects it can hold content older
            // than what another codec (ClearCodec/surface copies — Windows hosts mix them) has painted
            // there since; blitting the whole cell resurrects those pixels as stale squares.
            const clips = clipTileToRects(tx, ty, tr, tb, rects);
            for (let ci = 0; ci < clips.length; ci++) {
                const x = clips[ci][0], y = clips[ci][1];
                const w = clips[ci][2] - x, h = clips[ci][3] - y;
                let copy;
                if (w === 64 && h === 64 && x === tx && y === ty) {
                    copy = rgba.slice(0);
                } else {
                    copy = new Uint8ClampedArray(w * h * 4);
                    for (let row = 0; row < h; row++) {
                        const src = ((y - ty + row) * 64 + (x - tx)) * 4;
                        copy.set(rgba.subarray(src, src + w * 4), row * w * 4);
                    }
                }
                // The bounding-box composite below indexes its written[] mask by 64-aligned cell, so
                // any sub-cell clip (or multiple clips per cell) must force the per-tile sparse path.
                if (x !== tx || y !== ty || clips[ci][2] !== tr || clips[ci][3] !== tb || clips.length > 1) anyClipped = true;
                tiles.push({ x, y, w, h, rgba: copy });
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x + w > maxX) maxX = x + w;
                if (y + h > maxY) maxY = y + h;
            }
        }, log, msg.verbose, msg.frameId);
    } catch (e) {
        error = "progressive EXCEPTION (" + msg.bitmapData.byteLength + "B): " + (e && e.stack ? e.stack : e);
    }

    if (!res) {
        postMessage({ cmd: "progressive-result", reqId, surfaceId, ok: false,
            error: error || ("progressive decode failed (" + msg.bitmapData.byteLength + " bytes)") });
        return;
    }
    if (!tiles.length) {
        postMessage({ cmd: "progressive-result", reqId, surfaceId, ok: true, empty: true });
        return;
    }

    // Composite only into the bounding box, but ONLY where a tile actually landed. A region's changed
    // tiles are frequently a sparse/non-rectangular set (GNOME RD only sends tiles that actually changed
    // — an L-shaped or scattered dirty area is normal), so minX/minY/maxX/maxY can span 64x64 cells that
    // never got an onTile() call this PDU. Those previously got left as zero-filled (opaque black) in a
    // single bounding-box buffer and blitted straight over whatever good pixels were already there —
    // that's what was punching black holes into freshly-exposed windows. Track which cells were actually
    // written and skip re-blitting any that weren't.
    let holes = anyClipped;
    let frame = null, bw = 0, bh = 0;
    if (!holes) {
        bw = maxX - minX; bh = maxY - minY;
        const colTiles = Math.ceil(bw / 64), rowTiles = Math.ceil(bh / 64);
        const written = new Uint8Array(colTiles * rowTiles);
        frame = new Uint8ClampedArray(bw * bh * 4);
        for (const t of tiles) {
            const ox = t.x - minX, oy = t.y - minY;
            written[(oy / 64 | 0) * colTiles + (ox / 64 | 0)] = 1;
            for (let row = 0; row < t.h; row++) {
                const src = row * t.w * 4;
                const dst = ((oy + row) * bw + ox) * 4;
                frame.set(t.rgba.subarray(src, src + t.w * 4), dst);
            }
        }
        for (let i = 0; i < written.length; i++) if (!written[i]) { holes = true; break; }
    }
    postMessage(
        holes
            ? { cmd: "progressive-result", reqId, surfaceId, ok: true, sparse: true,
                tiles: tiles.map(function (t) { return { x: t.x, y: t.y, w: t.w, h: t.h, buffer: t.rgba.buffer }; }) }
            : { cmd: "progressive-result", reqId, surfaceId, ok: true, minX, minY, bw, bh, buffer: frame.buffer },
        holes ? tiles.map(function (t) { return t.rgba.buffer; }) : [frame.buffer]
    );
}

// ================================================================================================
// H.264 (AVC420) decode via WebCodecs VideoDecoder — in the worker
// ================================================================================================
// One decoder per surface. RDP AVC420 carries Annex-B NAL units (SPS/PPS in the first I-frame, then
// slices). We forward the spec-native Annex-B bitstream verbatim (no avcC `description`): the host
// includes SPS/PPS in-band on every keyframe and never reorders (no B-frames on a live desktop), so the
// decoder honours the SPS/VUI low-delay signaling and emits each frame immediately. Ported from the
// former main-thread H264SurfaceDecoder in rdpgfx.js — behaviour kept byte-for-byte identical; only the
// thread it runs on changed. The finished VideoFrame is transferred back to the main thread for paint.

const h264 = {}; // surfaceId -> H264Dec

function H264Dec(surfaceId) {
    this.surfaceId = surfaceId;
    this.decoder = null;
    this.configured = false;
    this.unsupported = false;
    this._gotKey = false;
    this._pendingRegions = []; // FIFO of region-rect lists, matched to output frames in order
    this._ts = 0;
    this._decCount = 0;
}

// Scan Annex-B for an IDR (NAL type 5) or SPS (7) to mark a keyframe; VideoDecoder requires the first
// chunk (and any after a flush) to be a key frame.
H264Dec.prototype._isKeyFrame = function (data) {
    for (let i = 0; i + 4 < data.length; i++) {
        if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1) {
            const nalType = data[i + 3] & 0x1f;
            if (nalType === 7 || nalType === 5) return true; // SPS or IDR slice
        } else if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 0 && data[i + 3] === 1) {
            const nalType = (data[i + 4] || 0) & 0x1f;
            if (nalType === 7 || nalType === 5) return true;
        }
    }
    return false;
};

// Split an Annex-B buffer into its NAL units (payloads WITHOUT the start code). Handles 3/4-byte codes.
function annexbSplit(data) {
    const nals = [];
    let i = 0;
    const n = data.length;
    function startLen(p) {
        if (p + 3 < n && data[p] === 0 && data[p + 1] === 0 && data[p + 2] === 0 && data[p + 3] === 1) return 4;
        if (p + 2 < n && data[p] === 0 && data[p + 1] === 0 && data[p + 2] === 1) return 3;
        return 0;
    }
    while (i < n && startLen(i) === 0) i++;
    while (i < n) {
        const sl = startLen(i);
        if (sl === 0) { i++; continue; }
        const start = i + sl;
        let j = start;
        while (j < n && startLen(j) === 0) j++;
        if (j > start) nals.push(data.subarray(start, j));
        i = j;
    }
    return nals;
}

// Build the WebCodecs codec string "avc1.PPCCLL" from SPS bytes (profile_idc, constraints, level_idc).
function avcCodecString(sps) {
    const hx = function (b) { return (b & 0xff).toString(16).padStart(2, "0"); };
    return "avc1." + hx(sps[1]) + hx(sps[2]) + hx(sps[3]);
}

// Configure the decoder from the keyframe's SPS. Spec-native Annex-B in-band config (no description).
H264Dec.prototype._configureFrom = function (annexb) {
    if (this.configured || this.unsupported) return;
    if (typeof VideoDecoder === "undefined") {
        this.unsupported = true;
        log("rdpgfx(worker): WebCodecs VideoDecoder unavailable — H.264 cannot be decoded");
        return;
    }
    const nals = annexbSplit(annexb);
    let sps = null;
    for (const nal of nals) { if ((nal[0] & 0x1f) === 7 && !sps) sps = nal; }
    const codec = sps ? avcCodecString(sps) : "avc1.4d402a";
    const self = this;
    if (!this.decoder) {
        this.decoder = new VideoDecoder({
            output: function (frame) { self._onFrame(frame); },
            // A WebCodecs decoder error closes the decoder. Reset our state so the NEXT keyframe rebuilds
            // a fresh decoder, otherwise every later decode() silently throws and the picture freezes.
            error: function (e) {
                log("rdpgfx(worker): H264 decoder error: " + (e && e.message || e) + " — will rebuild on next keyframe");
                try { if (self.decoder && self.decoder.state !== "closed") self.decoder.close(); } catch (_) {}
                self.decoder = null;
                self.configured = false;
                self._gotKey = false;
                self._pendingRegions = [];
            },
        });
    }
    try {
        this.decoder.configure({ codec: codec, optimizeForLatency: true });
        this.configured = true;
        log("rdpgfx(worker): H264 configured codec=" + codec + " mode=annexb");
    } catch (e) {
        this.unsupported = true;
        log("rdpgfx(worker): VideoDecoder.configure(" + codec + ") failed: " + (e && e.message || e));
    }
};

H264Dec.prototype.decode = function (annexb, regions) {
    const key = this._isKeyFrame(annexb);
    if (!this.configured && key) this._configureFrom(annexb);
    if (this.unsupported || !this.decoder || !this.configured) {
        if (!this._gotKey && !key) return; // still waiting for the first keyframe to configure
        return;
    }
    // VideoDecoder must START on a key frame; drop deltas until the first keyframe arrives.
    if (!this._gotKey && !key) return;
    if (key) this._gotKey = true;
    this._pendingRegions.push(regions);
    try {
        this.decoder.decode(new EncodedVideoChunk({ type: key ? "key" : "delta", timestamp: this._ts++, data: annexb }));
        this._decCount++;
        if (this._decCount <= 8 || key) {
            log("rdpgfx(worker): H264 decode() #" + this._decCount + " type=" + (key ? "key" : "delta") +
                " bytes=" + annexb.length + " state=" + this.decoder.state + " queue=" + this.decoder.decodeQueueSize);
        }
    } catch (e) {
        // decode() throwing means the decoder is in a bad/closed state. Reset so the next keyframe
        // rebuilds it, and ask the main thread to nudge the host for a fresh keyframe.
        log("rdpgfx(worker): H264 decode() threw: " + (e && e.message || e) + " — resetting decoder");
        try { if (this.decoder && this.decoder.state !== "closed") this.decoder.close(); } catch (_) {}
        this.decoder = null; this.configured = false; this._gotKey = false; this._pendingRegions = [];
        postMessage({ cmd: "h264-need-keyframe", surfaceId: this.surfaceId });
    }
};

// Output callback: match the frame to its region list (FIFO) and transfer it to the main thread to
// paint. VideoFrame is Transferable, so this is a zero-copy handoff; the main thread closes it.
H264Dec.prototype._onFrame = function (frame) {
    const regions = this._pendingRegions.shift() || null;
    postMessage({ cmd: "h264-frame", surfaceId: this.surfaceId, frame: frame, regions: regions }, [frame]);
};

H264Dec.prototype.close = function () {
    if (this.decoder && this.decoder.state !== "closed") { try { this.decoder.close(); } catch (_) {} }
    this.decoder = null;
    this._pendingRegions = [];
};

// AVC420 PDU: RFX_AVC420_METABLOCK (region rects + quant) then the Annex-B bitstream. The metablock's
// region rects are informational for us (we paint the whole decoded frame), but we still parse+forward
// them so paint code can stay identical to the old main-thread path. AVC444 is unwrapped on the main
// thread to stream 1 before it reaches here.
function decodeH264(msg) {
    const reqId = msg.reqId, surfaceId = msg.surfaceId;
    const data = new Uint8Array(msg.bitmapData);
    // Parse the metablock inline (small): numRegionRects(4) + rects(8 each) + quant(2 each) + bitstream.
    let off = 0;
    const rd32 = function () { const v = data[off] | (data[off+1]<<8) | (data[off+2]<<16) | (data[off+3]<<24); off += 4; return v >>> 0; };
    const rd16 = function () { const v = data[off] | (data[off+1]<<8); off += 2; return v; };
    const numRects = rd32();
    const rects = [];
    for (let i = 0; i < numRects; i++) rects.push({ left: rd16(), top: rd16(), right: rd16(), bottom: rd16() });
    off += numRects * 2; // quantQualityVals
    const bitstream = data.subarray(off);
    let dec = h264[surfaceId];
    if (!dec) { dec = new H264Dec(surfaceId); h264[surfaceId] = dec; }
    dec.decode(bitstream, rects);
    // Acknowledge submission so the main thread's ordered-decode barrier advances (H.264 output is async,
    // like progressive; the h264-frame reply lands later and paints, but the PDU has been consumed).
    postMessage({ cmd: "h264-submitted", reqId: reqId, surfaceId: surfaceId });
}

self.onmessage = function (e) {
    const msg = e.data;
    try {
        switch (msg.cmd) {
            case "progressive": decodeProgressive(msg); break;
            case "h264": decodeH264(msg); break;
            case "reset":
                for (const k in progCtx) delete progCtx[k];
                for (const k in h264) { h264[k].close(); delete h264[k]; }
                break;
            case "destroy-surface":
                delete progCtx[msg.surfaceId];
                if (h264[msg.surfaceId]) { h264[msg.surfaceId].close(); delete h264[msg.surfaceId]; }
                break;
            default:
                log("decode-worker: unknown cmd " + msg.cmd);
        }
    } catch (err) {
        // A throw ESCAPING a handler is catastrophic for the ordered-decode barrier: decodeProgressive/
        // decodeH264 that die before posting their reply leave the main thread's decode #N unsettled, so
        // settledSeq stalls below decodeSeq and EVERY later order-sensitive op (SURFACE_TO_CACHE, …) queues
        // forever and snapshots black (observed: queuedOps in the thousands, inflightDecodes stuck > 0).
        // So for a decode command, ALWAYS emit a (failed) result carrying its reqId so the barrier advances.
        // decodeProgressive/decodeH264 catch their own inner errors and reply normally; this only fires for
        // a throw OUTSIDE those inner try blocks (e.g. building the decode context).
        log("decode-worker: handler threw for cmd " + (msg && msg.cmd) + ": " + (err && err.stack ? err.stack : err));
        if (msg && (msg.cmd === "progressive" || msg.cmd === "h264") && msg.reqId != null) {
            postMessage({ cmd: "progressive-result", reqId: msg.reqId, surfaceId: msg.surfaceId, ok: false,
                error: "worker handler threw: " + (err && err.message || err) });
        }
    }
};
