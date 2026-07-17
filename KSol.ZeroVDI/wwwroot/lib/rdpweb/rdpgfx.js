// MS-RDPEGFX Graphics Pipeline — client-side surface compositor with H.264 (AVC420) decode.
//
// The host streams desktop graphics over the "Microsoft::Windows::RDS::Graphics" dynamic virtual
// channel instead of the legacy bitmap fastpath when the GFX path is negotiated. Every DVC payload
// is ZGFX-compressed (see zgfx.js) and, once inflated, contains one or more RDPGFX PDUs:
//
//   CAPS_CONFIRM         host picks one of the capsets we advertised
//   RESET_GRAPHICS       desktop size (and monitor layout) — sizes the output
//   CREATE/DELETE_SURFACE off-screen surfaces the host draws into
//   MAP_SURFACE_TO_OUTPUT binds a surface's (0,0) to an origin on the output (the visible desktop)
//   START_FRAME/END_FRAME frame boundaries; END_FRAME must be acknowledged (FRAME_ACKNOWLEDGE)
//   WIRE_TO_SURFACE_1    a codec-encoded bitmap (we handle AVC420 = H.264) targeting a surface rect
//
// We keep one RdpGfx instance per session. It owns the surfaces, drives WebCodecs VideoDecoder for
// H.264, and after each END_FRAME hands the host the composited output region to blit to the canvas
// via the onPaint callback. Rendering is gated behind the ?gfx=1 toggle in protocol.js.
//
// Structures ported from FreeRDP `channels/rdpgfx/client` + `include/freerdp/channels/rdpgfx.h`.

// Intersect a (surface-clamped) progressive tile rect [tx,ty)..(tr,tb) with its REGION's tileRects.
// Same helper as in decode-worker.js (keep in sync): returns [left,top,right,bottom] sub-rects; a
// rect fully covering the tile short-circuits to a single full-tile clip; no rects → whole tile.
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

// ---- RDPGFX command ids ([MS-RDPEGFX] 2.2.1 / rdpgfx.h) ------------------------------------------
const RDPGFX_CMDID_WIRETOSURFACE_1 = 0x0001;
const RDPGFX_CMDID_WIRETOSURFACE_2 = 0x0002;
const RDPGFX_CMDID_DELETEENCODINGCONTEXT = 0x0003;
const RDPGFX_CMDID_SOLIDFILL = 0x0004;
const RDPGFX_CMDID_SURFACETOSURFACE = 0x0005;
const RDPGFX_CMDID_SURFACETOCACHE = 0x0006;   // snapshot a surface rect into a cache slot
const RDPGFX_CMDID_CACHETOSURFACE = 0x0007;   // blit a cached slot back onto a surface
const RDPGFX_CMDID_EVICTCACHEENTRY = 0x0008;
const RDPGFX_CMDID_CREATESURFACE = 0x0009;
const RDPGFX_CMDID_DELETESURFACE = 0x000a;
const RDPGFX_CMDID_STARTFRAME = 0x000b;
const RDPGFX_CMDID_ENDFRAME = 0x000c;
const RDPGFX_CMDID_FRAMEACKNOWLEDGE = 0x000d;
const RDPGFX_CMDID_QOEFRAMEACKNOWLEDGE = 0x0016;
const RDPGFX_CMDID_RESETGRAPHICS = 0x000e;
const RDPGFX_CMDID_MAPSURFACETOOUTPUT = 0x000f;
const RDPGFX_CMDID_CACHEIMPORTREPLY = 0x0011;
const RDPGFX_CMDID_CAPSADVERTISE = 0x0012;
const RDPGFX_CMDID_CAPSCONFIRM = 0x0013;
const RDPGFX_CMDID_MAPSURFACETOWINDOW = 0x0015;
const RDPGFX_CMDID_MAPSURFACETOSCALEDOUTPUT = 0x0017;
const RDPGFX_CMDID_MAPSURFACETOSCALEDWINDOW = 0x0018;

const RDPGFX_HEADER_SIZE = 8;

// ---- caps versions + flags (rdpgfx.h) -----------------------------------------------------------
const RDPGFX_CAPVERSION_8 = 0x00080004;
const RDPGFX_CAPVERSION_81 = 0x00080105;
const RDPGFX_CAPVERSION_10 = 0x000a0002;
const RDPGFX_CAPVERSION_101 = 0x000a0100;
const RDPGFX_CAPVERSION_102 = 0x000a0200;
const RDPGFX_CAPVERSION_103 = 0x000a0301;
const RDPGFX_CAPVERSION_104 = 0x000a0400;
const RDPGFX_CAPVERSION_105 = 0x000a0502;
const RDPGFX_CAPVERSION_106 = 0x000a0600;
const RDPGFX_CAPVERSION_107 = 0x000a0701;
// Undocumented Azure/Win11 versions — mstsc negotiates AVC via these (observed: host selects 0x000b0101
// "Server and client both are AVC capable"). Without advertising them the host falls back to Progressive.
const RDPGFX_CAPVERSION_111 = 0x000b0101;
const RDPGFX_CAPVERSION_112 = 0x000b0200;
const RDPGFX_CAPVERSION_113 = 0x000b0300;
const RDPGFX_CAPS_FLAG_THINCLIENT = 0x00000001;     // 8.0+
const RDPGFX_CAPS_FLAG_SMALL_CACHE = 0x00000002;    // 8.0+: advertise a small offscreen cache
const RDPGFX_CAPS_FLAG_AVC420_ENABLED = 0x00000010; // 8.1+: AVC420 (H.264) permitted
const RDPGFX_CAPS_FLAG_AVC_DISABLED = 0x00000020;   // 10.0+: forbid AVC on this capset
const RDPGFX_CAPS_FLAG_AVC_THINCLIENT = 0x00000040; // 10.3+
const RDPGFX_CAPS_FLAG_SCALEDMAP_DISABLE = 0x00000080; // 10.7+: tell host NOT to use SCALED output map

// ---- codec ids (rdpgfx.h) -----------------------------------------------------------------------
const RDPGFX_CODECID_UNCOMPRESSED = 0x0000;
const RDPGFX_CODECID_CAPROGRESSIVE = 0x0009;
const RDPGFX_CODECID_CAPROGRESSIVE_V2 = 0x000d;
const RDPGFX_CODECID_CLEARCODEC = 0x0008;
const RDPGFX_CODECID_PLANAR = 0x000a;
const RDPGFX_CODECID_AVC420 = 0x000b;
const RDPGFX_CODECID_AVC444 = 0x000e;
const RDPGFX_CODECID_AVC444v2 = 0x000f;

// pixelFormat ([MS-RDPEGFX] 2.2.1.4) — both map to a 32bpp BGRX/BGRA surface.
const GFX_PIXEL_FORMAT_XRGB_8888 = 0x20;
const GFX_PIXEL_FORMAT_ARGB_8888 = 0x21;

// FRAME_ACKNOWLEDGE.queueDepth ([MS-RDPEGFX] 2.2.2.13): we send QUEUE_DEPTH_UNAVAILABLE (0) per frame,
// paired with a QOE_FRAME_ACKNOWLEDGE — the combination mstsc uses to keep the host streaming.
const RDPGFX_QUEUE_DEPTH_UNAVAILABLE = 0x00000000;

// URL of decode-worker.js, resolved relative to THIS script (document.currentScript is only valid while
// rdpgfx.js is first executing — RdpGfx instances are constructed later, per session, by protocol.js).
// asp-append-version query strings on the <script> tag are preserved so the worker is cache-busted with
// the rest of the client bundle.
var RDPGFX_WORKER_URL = (typeof document !== "undefined" && document.currentScript)
    ? new URL("decode-worker.js", document.currentScript.src).toString()
    : null;

// cb: { onLog, onPaint(canvas, sx, sy, sw, sh, dx, dy), onReset(w, h), send(payload) }
//   onPaint blits a region (sx,sy,sw,sh) of an offscreen surface canvas to output pixel (dx,dy).
//   onReset announces a new desktop size from RESET_GRAPHICS.
//   send transmits a complete RDPGFX PDU (the caller wraps it in ZGFX-less DVC framing — the client
//   sends uncompressed, descriptor 0xE0 single raw segment, which the host accepts).
function RdpGfx(cb) {
    this.cb = cb || {};
    this.mode = (cb && cb.mode) || "avc420"; // "clearcodec" | "avc420" | "avc444"
    this.zgfx = new ZgfxDecode();
    this.surfaces = {};          // surfaceId -> { width, height, canvas, ctx }
    this.cache = {};             // cacheSlot -> { canvas, w, h } (SURFACE_TO_CACHE snapshots)
    this.outputMap = {};         // surfaceId -> { originX, originY } (MAP_SURFACE_TO_OUTPUT)
    this.confirmedVersion = 0;
    this.decoders = {};          // surfaceId -> H264SurfaceDecoder
    // ClearCodec and RemoteFX Progressive are pure-JS, CPU-bound, bit-exact ports of FreeRDP.
    // Progressive (large frames, hundreds of tiles) runs in decode-worker.js; ClearCodec decodes on
    // the main thread — its glyph cache must snapshot the composed destination surface (FreeRDP
    // semantics, see _decodeClear/_finishClear), and its tiles are small enough not to block.
    this.clear = (typeof ClearDecode !== "undefined") ? new ClearDecode() : null; // ClearCodec ctx (glyph/vBar caches)
    // RemoteFX Progressive (GNOME Remote Desktop streams this over WIRE_TO_SURFACE_2). Per-surface
    // context (holds the persistent per-tile coefficient grids needed for RFX_TILE_DIFFERENCE).
    this.progressive = (typeof RfxProgressive !== "undefined") ? RfxProgressive : null;
    this.progCtx = {};           // surfaceId -> RfxProgressive.Context (main-thread fallback only)
    this.framesDecoded = 0;
    this._dirty = [];            // output rects touched in the current frame, flushed at END_FRAME
    this.outputWidth = 0;
    this.outputHeight = 0;

    this._worker = null;         // decode-worker.js instance (progressive/clear offload), or null
    this._workerReqId = 0;
    this._workerPending = {};    // reqId -> { surfaceId }
    // Progressive decode+paint is asynchronous (offloaded to decode-worker.js — see
    // _decodeProgressive), but SOLIDFILL / SURFACE_TO_CACHE / SURFACE_TO_SURFACE / CACHE_TO_SURFACE /
    // ClearCodec / uncompressed paints are synchronous canvas ops. [MS-RDPEGFX] PDUs are strictly ordered — a
    // SURFACE_TO_CACHE after a WIRE_TO_SURFACE must snapshot that PDU's painted result, a CACHE_TO_SURFACE
    // after a SURFACE_TO_CACHE must see the slot populated, and a blit/fill must not be stamped over by an
    // OLDER wire decode landing later. Per-surface defer queues can't express the cache-slot and cross-
    // surface dependencies (a deferred SURFACE_TO_CACHE with a non-deferred CACHE_TO_SURFACE = "empty
    // cache slot" black rects at session start), so ordering is global: every async decode gets a
    // sequence number, order-sensitive sync ops queue in ONE FIFO behind the decodes submitted before
    // them, and settle-time flushing replays them in exact PDU order (worker replies are FIFO).
    this._decodeSeq = 0;         // decodes submitted (worker + sync fallback)
    this._decodeSettledSeq = 0;  // decodes fully landed (painted or failed)
    this._orderedQueue = [];     // [{barrier, cmdId, body}] sync ops waiting for barrier <= settledSeq
    this._curFrameId = 0;        // START_FRAME id; scopes the progressive decoder's updated-tile set
    this._initWorker();
}

// Spin up decode-worker.js next to this script. Failure (CSP, no Worker support) is non-fatal — the
// caller falls back to decoding progressive/clear synchronously on the main thread (the pre-Worker
// behavior), so a locked-down environment still renders, just with the old blocking-decode tradeoff.
RdpGfx.prototype._initWorker = function () {
    if (typeof Worker === "undefined" || !RDPGFX_WORKER_URL) return;
    // Diagnostic escape hatch: window.RDP_GFX_NO_WORKER = true forces synchronous main-thread decode
    // (the pre-Worker path) so we can A/B whether an async decode race is behind a rendering artifact.
    if (typeof window !== "undefined" && window.RDP_GFX_NO_WORKER) {
        this._log("rdpgfx: decode worker disabled by RDP_GFX_NO_WORKER — using main-thread decode");
        return;
    }
    try {
        this._worker = new Worker(RDPGFX_WORKER_URL);
        const self = this;
        this._worker.onmessage = function (e) { self._onWorkerMessage(e.data); };
        this._worker.onerror = function (e) {
            self._log("rdpgfx: decode worker error, falling back to main-thread decode: " + e.message);
            self._worker = null;
        };
    } catch (e) {
        this._log("rdpgfx: could not start decode worker, using main-thread decode: " + e);
        this._worker = null;
    }
};

RdpGfx.prototype._onWorkerMessage = function (msg) {
    if (msg.cmd === "log") { this._log(msg.message); return; }
    // H.264 output frames are DECOUPLED from PDU submission (VideoDecoder emits asynchronously), so they
    // carry no reqId and don't touch the ordered-decode barrier — they just paint whatever surface they
    // belong to, in decode order (the worker's per-surface FIFO preserves it). The barrier for AVC is
    // settled at SUBMISSION time (h264-submitted) instead: WebCodecs guarantees monotonic in→out order,
    // so a following SURFACE_TO_SURFACE that we release once the PDU is consumed still composites the
    // right pixels because the decoder can't reorder a later frame ahead of this surface's earlier one.
    if (msg.cmd === "h264-frame") { this._paintH264Frame(msg); return; }
    if (msg.cmd === "h264-need-keyframe") { if (this.requestKeyframe) this.requestKeyframe(msg.surfaceId); return; }
    const pending = this._workerPending[msg.reqId];
    if (!pending) return; // reset() cleared it — stale pre-reset reply; the seq counters were reset too
    delete this._workerPending[msg.reqId];
    const surf = this.surfaces[pending.surfaceId];
    // Settle even when the surface was destroyed while the decode was in flight (only the paint is
    // skipped) — every submitted decode must advance the barrier or the ordered queue wedges forever.
    if (surf && msg.cmd === "progressive-result") {
        if (pending.diagBytes) msg.diagBytes = pending.diagBytes;
        this._finishProgressive(pending.surfaceId, surf, msg);
    }
    // "h264-submitted" only advances the barrier (its frame paints later, off-barrier).
    this._decodeSettled();
};

// Paint a VideoFrame transferred from the decode worker. Runs the exact old onDecodedFrame paint path
// (a single drawImage of the whole decoded frame), which is all that remains on the main thread for
// H.264 now — the decode itself happened in the worker.
RdpGfx.prototype._paintH264Frame = function (msg) {
    const surf = this.surfaces[msg.surfaceId];
    const frame = msg.frame;
    if (!surf) { if (frame && frame.close) frame.close(); return; }
    this.onDecodedFrame(msg.surfaceId, frame, msg.regions);
};

RdpGfx.prototype._log = function (m) { if (this.cb.onLog) this.cb.onLog(m); };

// Diagnostic ring buffer. EVERY diagnostic line goes here in addition to the normal console log, so the
// last N diag events survive even when console logging is off (RDP_LOG=0) — including in PRODUCTION. This
// is always-on because it's cheap (just retaining strings); only the pixel-scan work that PRODUCES some of
// these lines is gated behind RDP_GFX_DIAG. Dump it from devtools with window.rdpDiagDump() (newest last),
// or clear with window.rdpDiagDump(true). Bounded so it can't grow without limit on a long session.
RdpGfx.prototype._DIAG_RING_MAX = 2000;
RdpGfx.prototype._diagLog = function (m) {
    if (!this._diagRing) this._diagRing = [];
    this._diagRing.push({ t: Date.now(), m: m });
    if (this._diagRing.length > this._DIAG_RING_MAX) this._diagRing.shift();
    // Still emit to the console when logging is enabled, so live tailing is unchanged.
    if (this.cb.onLog) this.cb.onLog(m);
};

// Return the buffered diagnostic lines (each "ISO-time  message"), newest last. Pass clear=true to also
// empty the buffer after reading. Exposed to the page via window.rdpDiagDump (see client.js).
RdpGfx.prototype.diagDump = function (clear) {
    const out = (this._diagRing || []).map(function (e) {
        return new Date(e.t).toISOString().substr(11, 12) + "  " + e.m;
    });
    if (clear) this._diagRing = [];
    return out;
};

// RDP_LOG == 2 (see client.js) turns on verbose per-tile ClearCodec/Progressive decode tracing, on top
// of the always-on error logging those decoders already do. Read live (not cached) so toggling
// window.RDP_LOG in devtools takes effect on the next PDU without a reconnect.
RdpGfx.prototype._verbose = function () { return typeof window !== "undefined" && window.RDP_LOG == 2; };

// Black-partial-frame diagnostics. Set window.RDP_GFX_DIAG = 1 in devtools (no reconnect needed) to
// turn on: after every ClearCodec / Progressive paint the just-written region is scanned, and if it
// came out fully (or almost fully) black/transparent — the classic "black partial frame" symptom —
// we log the offending PDU's geometry AND a hex dump of the FULL codec message that produced it, so
// the exact bytes can be replayed/inspected. Off by default (the scan reads back canvas pixels, which
// is not free). RDP_GFX_DIAG = 2 dumps EVERY Clear/Progressive message regardless of blackness.
RdpGfx.prototype._diag = function () {
    return (typeof window !== "undefined" && window.RDP_GFX_DIAG) ? (window.RDP_GFX_DIAG | 0) : 0;
};

// Hex-dump up to `max` bytes of a Uint8Array as space-separated pairs, with a trailing "(+N more)"
// when truncated. Kept compact so a full tile's worth of bytes stays greppable on one logical line.
RdpGfx.prototype._hexDump = function (bytes, max) {
    if (!bytes || !bytes.length) return "<empty>";
    max = max || 512;
    const n = Math.min(bytes.length, max);
    let s = "";
    for (let i = 0; i < n; i++) {
        const h = bytes[i].toString(16);
        s += (h.length < 2 ? "0" + h : h) + (i + 1 < n ? " " : "");
    }
    if (bytes.length > n) s += " …(+" + (bytes.length - n) + " more, " + bytes.length + " total)";
    return s;
};

// Scan a painted surface region for content. Returns { total, nonBlack, opaque, black:bool } where
// `black` is true when essentially every sampled pixel is black-or-transparent — i.e. the region
// decoded to a black partial frame. Samples on a stride grid so a large region stays cheap. Any
// getImageData failure (tainted/oversized canvas) is swallowed and reported as non-black so diagnostics
// never break rendering.
RdpGfx.prototype._scanBlack = function (surf, rect) {
    try {
        const x = Math.max(0, rect.left | 0), y = Math.max(0, rect.top | 0);
        const w = Math.min(surf.width, rect.right | 0) - x;
        const h = Math.min(surf.height, rect.bottom | 0) - y;
        if (w <= 0 || h <= 0) return { total: 0, nonBlack: 0, opaque: 0, black: false };
        const img = surf.ctx.getImageData(x, y, w, h).data;
        // Cap total samples so a full-desktop progressive frame doesn't stall on a huge readback loop.
        const px = w * h;
        const step = px > 65536 ? Math.ceil(px / 65536) : 1;
        let nonBlack = 0, opaque = 0, total = 0;
        for (let i = 0; i < px; i += step) {
            const s = i * 4;
            total++;
            if (img[s + 3] !== 0) opaque++;
            if (img[s] !== 0 || img[s + 1] !== 0 || img[s + 2] !== 0) nonBlack++;
        }
        // "black" = <0.2% of sampled pixels have any non-zero color channel.
        return { total, nonBlack, opaque, black: total > 0 && nonBlack * 500 < total };
    } catch (e) {
        return { total: 0, nonBlack: 1, opaque: 1, black: false };
    }
};

// On-demand snapshot: scan EVERY surface for black regions right now and log the result. Callable from
// devtools regardless of the RDP_GFX_DIAG flag (the client exposes it as window.rdpDiagScan()), so a
// black area that turned black BEFORE diagnostics were switched on can still be pinned to a surface and
// its mapped output origin. Reports the whole surface plus a coarse 8x8 grid of which cells are black.
RdpGfx.prototype.diagScanAll = function () {
    const out = [];
    for (const id in this.surfaces) {
        const surf = this.surfaces[id];
        const full = this._scanBlack(surf, { left: 0, top: 0, right: surf.width, bottom: surf.height });
        const map = this.outputMap[id];
        const grid = [];
        const gx = 8, gy = 8;
        for (let cy = 0; cy < gy; cy++) {
            let row = "";
            for (let cx = 0; cx < gx; cx++) {
                const rc = {
                    left: Math.floor(surf.width * cx / gx), top: Math.floor(surf.height * cy / gy),
                    right: Math.floor(surf.width * (cx + 1) / gx), bottom: Math.floor(surf.height * (cy + 1) / gy),
                };
                row += this._scanBlack(surf, rc).black ? "#" : ".";
            }
            grid.push(row);
        }
        const line = "rdpgfx: [DIAG] surface " + id + " " + surf.width + "x" + surf.height +
            " touched=" + surf.touched + " mapped=" + (map ? ("@" + map.originX + "," + map.originY) : "no") +
            " black=" + full.black + " (nonBlack " + full.nonBlack + "/" + full.total + " sampled)\n" + grid.join("\n");
        this._diagLog(line);
        out.push({ surfaceId: id | 0, width: surf.width, height: surf.height, black: full.black, grid });
    }
    if (!out.length) this._diagLog("rdpgfx: [DIAG] no surfaces");
    return out;
};

// Common diagnostic report for a codec paint. `codec` is "ClearCodec"/"Progressive", `rect` the region
// just written, `msgBytes` the FULL raw codec message (Uint8Array) that produced it. Logs a hex dump
// when the region is black (RDP_GFX_DIAG>=1) or unconditionally (RDP_GFX_DIAG>=2).
RdpGfx.prototype._diagPaint = function (codec, surfaceId, surf, rect, msgBytes) {
    const level = this._diag();
    if (!level) return;
    const scan = this._scanBlack(surf, rect);
    const geo = "surface=" + surfaceId + " rect=[" + rect.left + "," + rect.top + "," +
        rect.right + "," + rect.bottom + "] (" + (rect.right - rect.left) + "x" + (rect.bottom - rect.top) + ")";
    if (scan.black) {
        this._diagLog("rdpgfx: [DIAG] BLACK " + codec + " frame — " + geo +
            " sampled=" + scan.total + " nonBlack=" + scan.nonBlack + " opaque=" + scan.opaque +
            " msgLen=" + (msgBytes ? msgBytes.length : 0) + "B bytes=[" + this._hexDump(msgBytes, 1024) + "]");
    } else if (level >= 2) {
        this._diagLog("rdpgfx: [DIAG] " + codec + " frame — " + geo +
            " sampled=" + scan.total + " nonBlack=" + scan.nonBlack + " opaque=" + scan.opaque +
            " msgLen=" + (msgBytes ? msgBytes.length : 0) + "B bytes=[" + this._hexDump(msgBytes, 256) + "]");
    }
};

// Reset session state (reconnect). Keep no surfaces/decoders across sessions.
RdpGfx.prototype.reset = function () {
    this.zgfx.reset();
    for (const id in this.decoders) this.decoders[id].close();
    this.surfaces = {}; this.outputMap = {}; this.decoders = {}; this.cache = {};
    this.progCtx = {};
    this._dirty = []; this.confirmedVersion = 0; this.framesDecoded = 0;
    this._qoeT0 = null;
    if (this.clear) this.clear.reset();
    this._workerPending = {};
    this._decodeSeq = 0; this._decodeSettledSeq = 0; this._orderedQueue = [];
    this._curFrameId = 0;
    if (this._worker) this._worker.postMessage({ cmd: "reset" });
};

// Session teardown (page navigation / client disposed) — actually terminate the worker thread.
RdpGfx.prototype.destroy = function () {
    if (this._worker) { this._worker.terminate(); this._worker = null; }
};

// Build the CAPS_ADVERTISE PDU. The host picks the HIGHEST version it accepts and honors that capset's
// flags. CRITICAL (from an mstsc wire log against this host): the host enables AVC/H.264 only when the
// client advertises the **v11.x (0x000b01xx) capsets** — mstsc logs "Gfx Caps negotiation completed.
// Selected caps version 0xb0101" then "Server and client both are AVC capable." A client capping at v10
// gets RemoteFX Progressive instead. So for AVC modes we advertise the full FreeRDP-style set up through
// v111/112/113 with flags=0 (AVC enabled, AVC444 not forced).
//
// Mode:
//   "clearcodec" — v8 + v8.1(AVC420) only → host streams ClearCodec (codecId 0x8) which clear.js decodes.
//   "avc420"     — full set v8..v8.1(AVC420)..v10..v10.x..v11.x, all caps10 flags=0 (AVC ENABLED). The
//                  host selects the highest (v11.x) and streams single-stream AVC420 H.264.
//   "avc444"     — same advertisement; the host may additionally use AVC444 (we decode the luma view).
RdpGfx.prototype.buildCapsAdvertise = function () {
    let caps;
    if (this.mode === "avc420" || this.mode === "avc444") {
        // mstsc negotiates v11.1 (0x000b0101) and the host then free-runs AVC444v2 (codecId 0x0f). When we
        // advertised v11.1/v11.2/v11.3 the host confirmed v11.2 and streamed AVC420 (0x0b) then stalled.
        // FreeRDP's v11.x capsets are length=4, flags=caps10Flags(=0). Cap at v11.1 (drop v11.2/v11.3) so
        // the host can only pick v11.1 — matching mstsc's outcome. (Adding a flag 0x4 made the host REJECT
        // the v11.1 capset entirely and fall back to v10.7 — so v11.x flags MUST be 0.)
        // EXACT byte-for-byte match of the working macOS Remote Desktop app's CAPS_ADVERTISE, recovered
        // by MPPC-decompressing its bulk-compressed caps PDU from a MITM c2s capture (it advertises caps
        // RDP-bulk-compressed; we send uncompressed, but the host confirms v11.1 from both). The decisive
        // difference we'd been missing: the macOS app sets SMALL_CACHE (0x02) on nearly every capset, and
        // its confirmed v11.1 capset carries flags=0x82 (SMALL_CACHE | SCALEDMAP_DISABLE) — we were
        // sending 0x80 (SCALEDMAP_DISABLE only). It also advertises a DIFFERENT capset LIST (includes
        // v10.3 and v11.3; OMITS v10.1/v10.5/v10.6). Match the list and flags exactly. The 0x02 bit on
        // the confirmed capset is the suspected fix for the host's 2nd RESET / stall.
        const SC = RDPGFX_CAPS_FLAG_SMALL_CACHE;            // 0x02
        const SC_AVCOFF = SC | RDPGFX_CAPS_FLAG_AVC_DISABLED;        // 0x22
        const SC_NOSCALE = SC | RDPGFX_CAPS_FLAG_SCALEDMAP_DISABLE;  // 0x82
        caps = [
            { version: RDPGFX_CAPVERSION_8,   flags: SC, len: 4 },
            { version: RDPGFX_CAPVERSION_81,  flags: SC, len: 4 },
            { version: RDPGFX_CAPVERSION_10,  flags: SC_AVCOFF, len: 4 },
            { version: RDPGFX_CAPVERSION_102, flags: SC_AVCOFF, len: 4 },
            { version: RDPGFX_CAPVERSION_103, flags: RDPGFX_CAPS_FLAG_AVC_DISABLED, len: 4 }, // 0x20
            { version: RDPGFX_CAPVERSION_104, flags: SC, len: 4 },
            { version: RDPGFX_CAPVERSION_107, flags: SC_NOSCALE, len: 4 },
            { version: RDPGFX_CAPVERSION_111, flags: SC_NOSCALE, len: 4 },
            { version: RDPGFX_CAPVERSION_113, flags: SC_NOSCALE, len: 4 },
        ];
    } else if (this.mode === "progressive") {
        // v10+ with AVC explicitly disabled: per MS-RDPEGFX a Windows host negotiating GFX at v10+
        // without AVC available falls back to RemoteFX Progressive rather than ClearCodec (which is
        // what a v8/v8.1-only advertisement gets you — see the "clearcodec" branch below).
        const AVCOFF = RDPGFX_CAPS_FLAG_AVC_DISABLED;
        caps = [
            { version: RDPGFX_CAPVERSION_8, flags: 0, len: 4 },
            { version: RDPGFX_CAPVERSION_81, flags: 0, len: 4 },
            { version: RDPGFX_CAPVERSION_10, flags: AVCOFF, len: 4 },
            { version: RDPGFX_CAPVERSION_102, flags: AVCOFF, len: 4 },
            { version: RDPGFX_CAPVERSION_103, flags: AVCOFF, len: 4 },
            { version: RDPGFX_CAPVERSION_104, flags: AVCOFF, len: 4 },
        ];
    } else {
        caps = [
            { version: RDPGFX_CAPVERSION_8, flags: 0, len: 4 },
            { version: RDPGFX_CAPVERSION_81, flags: RDPGFX_CAPS_FLAG_AVC420_ENABLED, len: 4 },
        ];
    }
    const body = new ByteWriter();
    body.u16le(caps.length);                 // capsSetCount
    for (const c of caps) {
        body.u32le(c.version);               // version
        body.u32le(c.len);                   // capsDataLength
        body.u32le(c.flags);                 // capsData flags (first 4 bytes)
        for (let k = 4; k < c.len; k += 4) body.u32le(0); // pad the rest (v10.1 has 16-byte body)
    }
    return this._wrapPdu(RDPGFX_CMDID_CAPSADVERTISE, body.toArray());
};

// Wrap a PDU body in an RDPGFX_HEADER {cmdId(2), flags(2), pduLength(4)}.
RdpGfx.prototype._wrapPdu = function (cmdId, body) {
    const w = new ByteWriter();
    w.u16le(cmdId);
    w.u16le(0x0000);                         // flags
    w.u32le(RDPGFX_HEADER_SIZE + body.length);
    w.bytes(body);
    return w.toArray();
};

// Inbound: a complete (reassembled) DVC payload on the Graphics channel. ZGFX-inflate then walk the
// concatenated PDUs. Each PDU's pduLength bounds its body; we always advance to header+pduLength.
RdpGfx.prototype.onChannelData = function (data) {
    const inflated = this.zgfx.decompress(data);
    if (!inflated) { this._log("rdpgfx: zgfx decompress failed (" + data.length + " bytes)"); return; }

    let off = 0;
    while (off + RDPGFX_HEADER_SIZE <= inflated.length) {
        const r = new ByteReader(inflated.subarray(off));
        const cmdId = r.u16le();
        /* flags */ r.u16le();
        const pduLength = r.u32le();
        if (pduLength < RDPGFX_HEADER_SIZE || off + pduLength > inflated.length) {
            this._log("rdpgfx: bad pduLength " + pduLength + " at off " + off + " (blob " + inflated.length + ")");
            break;
        }
        // The PDU body is everything after the 8-byte header, up to pduLength.
        const body = inflated.subarray(off + RDPGFX_HEADER_SIZE, off + pduLength);
        // PDU-type census (RDP_GFX_DIAG): tally every cmdId we receive and, for WIRE_TO_SURFACE_1/2, the
        // codecId. This answers "does the host actually send content PDUs (Progressive / large ClearCodec),
        // or only cache ops?" — the whole question behind the persistent black. Summarised every 200 PDUs.
        if (this._diag()) this._censusPdu(cmdId, body);
        this._dispatch(cmdId, body);
        off += pduLength;
    }
};

// Tally received PDU types (and wire codecIds) and periodically log the histogram. Diagnostics only.
RdpGfx.prototype._censusPdu = function (cmdId, body) {
    if (!this._census) { this._census = {}; this._censusN = 0; }
    let key = "cmd0x" + cmdId.toString(16);
    if ((cmdId === RDPGFX_CMDID_WIRETOSURFACE_1 || cmdId === RDPGFX_CMDID_WIRETOSURFACE_2) && body.length >= 4) {
        const codecId = body[2] | (body[3] << 8);
        key += ":codec0x" + codecId.toString(16);
    }
    this._census[key] = (this._census[key] || 0) + 1;
    if (++this._censusN % 200 === 0) {
        const parts = Object.keys(this._census).sort().map((k) => k + "=" + this._census[k]);
        this._diagLog("rdpgfx: [DIAG] PDU census (" + this._censusN + " total): " + parts.join("  "));
    }
};

RdpGfx.prototype._dispatch = function (cmdId, body) {
    // Order-sensitive sync ops (see the _decodeSeq comment in the constructor): they read a surface or a
    // cache slot, or write pixels an older in-flight decode would otherwise stamp over later. They may
    // only run once every decode submitted before them has landed — and once anything is queued, all
    // later order-sensitive ops queue behind it (FIFO), or a CACHE_TO_SURFACE could still overtake the
    // queued SURFACE_TO_CACHE that populates its slot. WIRE_TO_SURFACE_1 with the UNCOMPRESSED codec
    // also paints synchronously (_drawUncompressed) and needs the same gating — Windows interleaves
    // small uncompressed strips with ClearCodec, and letting them jump ahead of in-flight decodes let
    // the older decode land later and stamp stale pixels over the strip. (Codec-decoded wire PDUs stay
    // ungated on purpose: they paint at their own settle slot, which is already in PDU order.)
    let orderSensitive =
        cmdId === RDPGFX_CMDID_SOLIDFILL || cmdId === RDPGFX_CMDID_SURFACETOSURFACE ||
        cmdId === RDPGFX_CMDID_SURFACETOCACHE || cmdId === RDPGFX_CMDID_CACHETOSURFACE;
    if (!orderSensitive && cmdId === RDPGFX_CMDID_WIRETOSURFACE_1 && body.length >= 4) {
        const codecId = body[2] | (body[3] << 8); // peek
        // ClearCodec decodes synchronously on the main thread too (it must read the composed surface
        // for FreeRDP-faithful glyph caching — see _decodeClear), so it needs the same gating.
        orderSensitive = codecId === RDPGFX_CODECID_UNCOMPRESSED || codecId === RDPGFX_CODECID_CLEARCODEC;
    }
    if (orderSensitive && (this._decodeSettledSeq < this._decodeSeq || this._orderedQueue.length)) {
        // body is a subarray view into the ZGFX inflate buffer, which is REUSED for the next PDU —
        // anything that outlives this dispatch call must own its bytes or it decodes garbage later.
        this._orderedQueue.push({ barrier: this._decodeSeq, cmdId, body: body.slice() });
        return;
    }
    this._dispatchNow(cmdId, body);
};

RdpGfx.prototype._dispatchNow = function (cmdId, body) {
    const r = new ByteReader(body);
    switch (cmdId) {
        case RDPGFX_CMDID_CAPSCONFIRM: return this._onCapsConfirm(r);
        case RDPGFX_CMDID_RESETGRAPHICS: return this._onResetGraphics(r);
        case RDPGFX_CMDID_CREATESURFACE: return this._onCreateSurface(r);
        case RDPGFX_CMDID_DELETESURFACE: return this._onDeleteSurface(r);
        case RDPGFX_CMDID_MAPSURFACETOOUTPUT: return this._onMapSurfaceToOutput(r);
        case RDPGFX_CMDID_MAPSURFACETOSCALEDOUTPUT: return this._onMapSurfaceToScaledOutput(r);
        case RDPGFX_CMDID_STARTFRAME: return this._onStartFrame(r);
        case RDPGFX_CMDID_ENDFRAME: return this._onEndFrame(r);
        case RDPGFX_CMDID_WIRETOSURFACE_1: return this._onWireToSurface1(r);
        case RDPGFX_CMDID_WIRETOSURFACE_2: return this._onWireToSurface2(r);
        case RDPGFX_CMDID_SOLIDFILL: return this._onSolidFill(r);
        case RDPGFX_CMDID_SURFACETOSURFACE: return this._onSurfaceToSurface(r);
        case RDPGFX_CMDID_SURFACETOCACHE: return this._onSurfaceToCache(r);
        case RDPGFX_CMDID_CACHETOSURFACE: return this._onCacheToSurface(r);
        case RDPGFX_CMDID_CACHEIMPORTREPLY: return; // we offer no cache; reply is empty/ignored
        case RDPGFX_CMDID_DELETEENCODINGCONTEXT: return;
        default:
            this._log("rdpgfx: unhandled cmdId 0x" + cmdId.toString(16));
    }
};

// Called after each decode has fully landed (painted or failed): advance the settle sequence and run
// every queued sync op whose barrier is now met, in original PDU order. Stop at the first op still
// waiting — FIFO order must never be violated by skipping ahead.
RdpGfx.prototype._decodeSettled = function () {
    if (this._decodeSettledSeq < this._decodeSeq) this._decodeSettledSeq++;
    while (this._orderedQueue.length && this._orderedQueue[0].barrier <= this._decodeSettledSeq) {
        const cmd = this._orderedQueue.shift();
        this._dispatchNow(cmd.cmdId, cmd.body);
    }
};

RdpGfx.prototype._onCapsConfirm = function (r) {
    const version = r.u32le();
    /* capsDataLength */ r.u32le();
    const flags = r.remaining() >= 4 ? r.u32le() : 0;
    this.confirmedVersion = version;
    const avc = (version >= RDPGFX_CAPVERSION_81) && !(flags & RDPGFX_CAPS_FLAG_AVC_DISABLED);
    this._log("rdpgfx: CAPS_CONFIRM version=0x" + version.toString(16) + " flags=0x" +
        flags.toString(16) + " (AVC/H264 " + (avc ? "enabled" : "DISABLED") + ")");
};

// RESET_GRAPHICS: desktop width/height + monitor layout, padded to a fixed 340-byte PDU. We only need
// the desktop size to (re)size the output compositor.
RdpGfx.prototype._onResetGraphics = function (r) {
    const width = r.u32le();
    const height = r.u32le();
    /* monitorCount + monitor defs + pad: ignored (single-monitor assumption) */
    this.outputWidth = width;
    this.outputHeight = height;
    this._log("rdpgfx: RESET_GRAPHICS " + width + "x" + height);
    if (this.cb.onReset) this.cb.onReset(width, height);
};

// Build a surface record with a fresh alpha:false canvas of the given size. Shared by CREATE_SURFACE and
// the orphan path so both produce identical, opaque-backed surfaces.
//   alpha:false — a GFX surface is opaque desktop content ([MS-RDPEGFX] 3.3.8.x: when mapped to output the
// alpha channel MUST be ignored). An opaque-backed canvas initializes to opaque black, so regions no codec
// has written yet don't blend the previous output frame through when drawImage'd to the visible canvas
// (the "ghost of the last frame" artifact). Our decoders write A=0xff, so real content is unaffected.
RdpGfx.prototype._makeSurfaceRecord = function (width, height, pixelFormat) {
    const canvas = (typeof OffscreenCanvas !== "undefined")
        ? new OffscreenCanvas(width, height)
        : Object.assign(document.createElement("canvas"), { width: width, height: height });
    const ctx = canvas.getContext("2d", { alpha: false });
    return { width, height, canvas, ctx, pixelFormat, touched: false };
};

// Lazily materialise a surface for an id the host is drawing into before (or between) CREATE_SURFACE. It
// is a REAL surface (so ClearCodec paints AND caches its glyph from a correct destination, keeping codec
// state in sync), just flagged `orphan` so the next CREATE_SURFACE for this id ADOPTS its canvas instead
// of wiping to black. Sized to at least the dest rect seen so far; CREATE_SURFACE resizes to the true size.
RdpGfx.prototype._ensureOrphanSurface = function (surfaceId, minW, minH, pixelFormat) {
    let surf = this.surfaces[surfaceId];
    if (surf) return surf;
    // Size to the full known output (from RESET_GRAPHICS) so later, larger dest rects don't clip against a
    // too-small orphan; fall back to the caller's minimum bound before RESET is known. CREATE_SURFACE
    // resizes to the true dimensions on adopt regardless.
    const w = Math.max(this.outputWidth || 0, minW | 0, 1);
    const h = Math.max(this.outputHeight || 0, minH | 0, 1);
    surf = this._makeSurfaceRecord(w, h, pixelFormat);
    surf.orphan = true;
    this.surfaces[surfaceId] = surf;
    return surf;
};

RdpGfx.prototype._onCreateSurface = function (r) {
    const surfaceId = r.u16le();
    const width = r.u16le();
    const height = r.u16le();
    const pixelFormat = r.u8();
    const orphan = this.surfaces[surfaceId];
    if (orphan && orphan.orphan) {
        // Adopt the orphan: the host already streamed content into this id before its CREATE_SURFACE (or
        // across a DELETE→CREATE reuse). Keep those pixels — copy the orphan canvas into a correctly-sized
        // surface — instead of allocating a fresh black one and losing everything painted so far. This is
        // the pixel half of the ClearCodec-state fix in _onWireToSurface1 (the codec caches were already
        // kept in sync by decoding into the orphan). Preserve `touched` so a real painted orphan blits.
        const rec = this._makeSurfaceRecord(width, height, pixelFormat);
        try { rec.ctx.drawImage(orphan.canvas, 0, 0); } catch (e) { /* size mismatch is fine — clipped */ }
        rec.touched = orphan.touched;
        this.surfaces[surfaceId] = rec;
        this._log("rdpgfx: CREATE_SURFACE id=" + surfaceId + " " + width + "x" + height +
            " fmt=0x" + pixelFormat.toString(16) + " (adopted orphan, content preserved)");
        return;
    }
    // Reuse of a surfaceId implies the old one is gone — drop it first (FreeRDP does the same).
    this._destroySurface(surfaceId);
    // `touched` flips true on the first content write (_afterSurfaceUpdate). Untouched surfaces are never
    // blitted to the output (see _paintSurface) — painting a brand-new empty surface would wipe the last
    // good frame during a host-side reconfigure (GNOME RD deletes+recreates its surface and re-maps it on
    // every DISPLAYCONTROL_MONITOR_LAYOUT, then streams nothing until damage occurs).
    this.surfaces[surfaceId] = this._makeSurfaceRecord(width, height, pixelFormat);
    this._log("rdpgfx: CREATE_SURFACE id=" + surfaceId + " " + width + "x" + height +
        " fmt=0x" + pixelFormat.toString(16));
};

RdpGfx.prototype._destroySurface = function (surfaceId) {
    if (this.decoders[surfaceId]) { this.decoders[surfaceId].close(); delete this.decoders[surfaceId]; }
    delete this.surfaces[surfaceId];
    delete this.outputMap[surfaceId];
    // Drop the progressive per-tile cache too — a recreated surfaceId is a brand-new surface; keeping the
    // old cache would mis-accumulate (and leak). The next frame must repaint from scratch. The real
    // per-tile state now lives in the worker (progCtx here is only used by the main-thread fallback path).
    // Keyed by surfaceId (see _decodeProgressiveSyncInner).
    delete this.progCtx[surfaceId];
    if (this._worker) this._worker.postMessage({ cmd: "destroy-surface", surfaceId: surfaceId });
    // In-flight decodes for this surface still reply and settle (paint skipped via the !surf check in
    // _onWorkerMessage), so the ordered queue keeps draining. Queued sync ops that reference this
    // surface no-op harmlessly in their handlers when they eventually run.
};

RdpGfx.prototype._onDeleteSurface = function (r) {
    const surfaceId = r.u16le();
    this._destroySurface(surfaceId);
    this._log("rdpgfx: DELETE_SURFACE id=" + surfaceId);
};

RdpGfx.prototype._onMapSurfaceToOutput = function (r) {
    const surfaceId = r.u16le();
    /* reserved */ r.u16le();
    const originX = r.u32le();
    const originY = r.u32le();
    this.outputMap[surfaceId] = { originX, originY, scaleW: 0, scaleH: 0 };
    this._log("rdpgfx: MAP_SURFACE_TO_OUTPUT id=" + surfaceId + " @" + originX + "," + originY);
    // The full surface is now visible at this origin — paint what we already have.
    this._paintSurface(surfaceId, 0, 0, null, null);
};

RdpGfx.prototype._onMapSurfaceToScaledOutput = function (r) {
    const surfaceId = r.u16le();
    /* reserved */ r.u16le();
    const originX = r.u32le();
    const originY = r.u32le();
    const targetWidth = r.u32le();
    const targetHeight = r.u32le();
    this.outputMap[surfaceId] = { originX, originY, scaleW: targetWidth, scaleH: targetHeight };
    this._log("rdpgfx: MAP_SURFACE_TO_SCALED_OUTPUT id=" + surfaceId + " @" + originX + "," +
        originY + " -> " + targetWidth + "x" + targetHeight);
    this._paintSurface(surfaceId, 0, 0, null, null);
};

RdpGfx.prototype._onStartFrame = function (r) {
    /* timestamp */ r.u32le();
    this._curFrameId = r.u32le();
    this._frameStartMs = Date.now();   // for QOE timeDiffSE (START->END)
    this._dirty = [];
};

// END_FRAME: flush this frame's dirty regions to the output, then acknowledge.
//
// THE STALL FIX (verified by a MITM capture of mstsc against this exact host): for EVERY frame mstsc
// sends BOTH a FRAME_ACKNOWLEDGE (cmdId 0x0d, queueDepth=0) AND a QOE_FRAME_ACKNOWLEDGE (cmdId 0x16).
// We previously sent only the FRAME_ACK; the host gates its GFX output on the QOE acks and stalls after
// the initial burst without them. So we now mirror mstsc: per-frame FRAME_ACK + QOE_FRAME_ACK, no early
// suspend (mstsc kept normal acks through frame ~19 and only later sent the SUSPEND sentinel).
RdpGfx.prototype._onEndFrame = function (r) {
    const frameId = r.u32le();
    this.framesDecoded++;
    // FLOW CONTROL ([MS-RDPEGFX] 3.2.5.13 + 3.2.1.2 Unacknowledged Frames). GROUND TRUTH from the working
    // mstsc MITM capture against THIS host (/tmp/rdpmitm, 3.6MB s2c, true wire order): mstsc sends a real
    // FRAME_ACKNOWLEDGE (cmdId 0x0d) with queueDepth=0 for EVERY frame (45 frame-acks, almost all
    // queueDepth=0x0, a few real buffered-byte counts like 0x55/0x15E/0x1778), PLUS a QOE ack per frame.
    // The host's GFX scheduler is DRIVEN by this per-frame FRAME_ACK feedback loop — it streams a burst,
    // waits for the ack, streams more. mstsc sends SUSPEND (0xFFFFFFFF) exactly ONCE, at the very END of
    // the session (frame ~45, during teardown) — never up front.
    //
    // A previous experiment sent SUSPEND on the FIRST END_FRAME (then stopped FRAME_ACKs). That is the
    // OPPOSITE of mstsc and it DETERMINISTICALLY stalls this host at frame 2-4: with no per-frame ack the
    // host has no queueDepth signal and simply stops scheduling GFX (spec says it MUST NOT *block*, but
    // "not block" ≠ "keep streaming" — this host throttles to nothing without the ack loop). So: mirror
    // mstsc exactly — FRAME_ACK queueDepth=0 every frame + QOE every frame. (RDP_GFX_SUSPEND=1 forces the
    // old up-front-SUSPEND behavior for A/B testing only.)
    const RDPGFX_SUSPEND_FRAME_ACK = 0xFFFFFFFF;
    const forceSuspend = (typeof window !== "undefined" && window.RDP_GFX_SUSPEND);
    if (forceSuspend) {
        if (!this._suspendSent) { this._sendFrameAck(frameId, RDPGFX_SUSPEND_FRAME_ACK); this._suspendSent = true; }
    } else {
        this._sendFrameAck(frameId, RDPGFX_QUEUE_DEPTH_UNAVAILABLE);
    }
    this._sendQoeFrameAck(frameId);
};

RdpGfx.prototype._sendFrameAck = function (frameId, queueDepth) {
    const body = new ByteWriter();
    body.u32le(queueDepth >>> 0);                // queueDepth (0 normal, 0xFFFFFFFF = suspend acks)
    body.u32le(frameId);                         // frameId
    body.u32le(this.framesDecoded);              // totalFramesDecoded
    if (this.cb.send) {
        this.cb.send(this._wrapPdu(RDPGFX_CMDID_FRAMEACKNOWLEDGE, body.toArray()));
    } else {
        this._log("rdpgfx: NO send callback — cannot ack frame " + frameId);
    }
};

// QOE_FRAME_ACKNOWLEDGE ([MS-RDPEGFX] 2.2.2.14): frameId(4) timestamp(4) timeDiffSE(2) timeDiffEDR(2).
// mstsc sends one per frame and the host requires them to keep the GFX video stream free-running. We
// report a monotonic timestamp (ms since first frame) and zero time-diffs (we don't measure E2E latency).
RdpGfx.prototype._sendQoeFrameAck = function (frameId) {
    if (!this.cb.send) return;
    // Match mstsc's QOE exactly (recovered from MITM): it sends a RAW wall-clock tick (GetTickCount,
    // a large 32-bit value like 0x17e43536) as the timestamp — NOT a since-first-frame delta starting
    // at 0. The spec calls QOE informational, but this host gated v3 and caps on "informational" fields
    // too, so we mirror mstsc precisely: raw Date.now() low-32 timestamp + small real timeDiffSE.
    const now = Date.now() >>> 0;
    if (this._qoeStartT == null) this._qoeStartT = now;
    const ts = now;                                  // raw tick, like mstsc
    const diffSE = Math.min(0xffff, (this._frameStartMs ? (now - this._frameStartMs) : 0)) & 0xffff;
    const body = new ByteWriter();
    body.u32le(frameId);   // frameId
    body.u32le(ts);        // timestamp (raw ms tick)
    body.u16le(diffSE);    // timeDiffSE (START->END decode, ms)
    body.u16le(0);         // timeDiffEDR
    this.cb.send(this._wrapPdu(RDPGFX_CMDID_QOEFRAMEACKNOWLEDGE, body.toArray()));
};

// WIRE_TO_SURFACE_1: a codec-encoded bitmap for a surface rect. We decode AVC420 (H.264) and, when
// supported, the trivial UNCOMPRESSED/PLANAR-as-raw fallbacks; anything else is logged and skipped.
RdpGfx.prototype._onWireToSurface1 = function (r) {
    const surfaceId = r.u16le();
    const codecId = r.u16le();
    const pixelFormat = r.u8();
    const destLeft = r.u16le(), destTop = r.u16le(), destRight = r.u16le(), destBottom = r.u16le();
    const bitmapDataLength = r.u32le();
    const bitmapData = r.bytes(bitmapDataLength);
    // The surface may be transiently absent (host DELETE_SURFACE → …WIRE… → RESET → CREATE_SURFACE reuses
    // the same id) or land before its CREATE on the very first paint. ClearCodec is a STATEFUL stream:
    // every tile carries a seqNumber and mutates the session-global glyph / VBar caches ([MS-RDPEGFX]
    // 3.3.8.2.1). Dropping the PDU skips that state — the seqNumber jumps ("seqNumber N != expected") and
    // the VBar entries this PDU would populate are never stored, so LATER tiles that VBAR_CACHE_HIT those
    // slots decode BLANK (black). That is the root cause of the persistent black content. So we lazily
    // materialise an ORPHAN surface for the id and decode+paint into it exactly as normal; when the real
    // CREATE_SURFACE arrives it ADOPTS the orphan's canvas (see _onCreateSurface), so both the codec state
    // AND the already-painted pixels survive. The dest rect bounds the size until CREATE gives the real one.
    let surf = this.surfaces[surfaceId];
    if (!surf) {
        surf = this._ensureOrphanSurface(surfaceId, destRight, destBottom, pixelFormat);
        this._log("rdpgfx: WIRE_TO_SURFACE_1 for absent surface " + surfaceId +
            " — painting into orphan surface (adopted on CREATE_SURFACE)");
    }

    const rect = { left: destLeft, top: destTop, right: destRight, bottom: destBottom };
    if (this._verbose() && (codecId === RDPGFX_CODECID_CLEARCODEC)) {
        this._log("rdpgfx: WIRE_TO_SURFACE_1 ClearCodec surface=" + surfaceId + " rect=[" +
            destLeft + "," + destTop + "," + destRight + "," + destBottom + "] " + bitmapDataLength + "B");
    }
    // RDP_GFX_DIAG>=2: dump the full inbound WIRE_TO_SURFACE_1 message (header fields + codec bitstream)
    // for ClearCodec, so the exact wire bytes are captured even if the paint later comes out non-black.
    if (this._diag() >= 2 && codecId === RDPGFX_CODECID_CLEARCODEC) {
        this._diagLog("rdpgfx: [DIAG] WIRE_TO_SURFACE_1 codecId=0x" + codecId.toString(16) + " fmt=0x" +
            pixelFormat.toString(16) + " surface=" + surfaceId + " rect=[" + destLeft + "," + destTop + "," +
            destRight + "," + destBottom + "] len=" + bitmapDataLength + "B bytes=[" + this._hexDump(bitmapData, 1024) + "]");
    }
    if (codecId === RDPGFX_CODECID_AVC420) {
        this._decodeAvc420(surfaceId, surf, rect, bitmapData);
    } else if (codecId === RDPGFX_CODECID_AVC444 || codecId === RDPGFX_CODECID_AVC444v2) {
        this._decodeAvc444(surfaceId, surf, rect, bitmapData);
    } else if (codecId === RDPGFX_CODECID_CLEARCODEC) {
        this._decodeClear(surfaceId, surf, rect, bitmapData);
    } else if (codecId === RDPGFX_CODECID_UNCOMPRESSED) {
        this._drawUncompressed(surf, rect, bitmapData, pixelFormat);
    } else {
        this._log("rdpgfx: WIRE_TO_SURFACE_1 unsupported codecId 0x" + codecId.toString(16) +
            " (" + bitmapDataLength + " bytes) — skipped");
    }
};

// WIRE_TO_SURFACE_2 ([MS-RDPEGFX] 2.2.2.2): surfaceId(2), codecId(2), codecContextId(4),
// pixelFormat(1), bitmapDataLength(4), then the codec bitstream. Used by the context/stream codecs —
// on Windows that's RemoteFX Progressive (CAPROGRESSIVE 0x0009). The host switches to this when it
// decides to stream the desktop progressively instead of as ClearCodec tiles.
RdpGfx.prototype._onWireToSurface2 = function (r) {
    const surfaceId = r.u16le();
    const codecId = r.u16le();
    const codecContextId = r.u32le();
    const pixelFormat = r.u8();
    // bitmapDataLength (4 bytes, [MS-RDPEGFX] 2.2.2.2) — the codec bitstream length. MUST be read (and
    // used to bound bitmapData) even though in practice it always covers the rest of the PDU: skipping
    // it silently shifted the whole bitstream 4 bytes early, corrupting every Progressive tile from
    // byte 0. findBlockStart's leading-offset scan in progressive.js occasionally papered over this by
    // finding a plausible-looking block header a few bytes in, which is why it didn't fail loudly.
    const bitmapDataLength = r.u32le();
    const bitmapData = r.bytes(Math.min(bitmapDataLength, r.remaining()));
    // Same orphan handling as WIRE_TO_SURFACE_1 (see there): Progressive is stateful too (per-tile
    // coefficient accumulation), so decoding into an orphan keeps that state alive and preserves the
    // pixels for CREATE_SURFACE to adopt. No dest rect here, so size the orphan to the known output size.
    let surf = this.surfaces[surfaceId];
    if (!surf) {
        surf = this._ensureOrphanSurface(surfaceId, this.outputWidth || 1, this.outputHeight || 1, pixelFormat);
        this._log("rdpgfx: WIRE_TO_SURFACE_2 for absent surface " + surfaceId +
            " — painting into orphan surface (adopted on CREATE_SURFACE)");
    }

    if (codecId === RDPGFX_CODECID_CAPROGRESSIVE || codecId === RDPGFX_CODECID_CAPROGRESSIVE_V2) {
        if (!this.progressive) { this._log("rdpgfx: Progressive module not loaded"); return; }
        if (this._verbose()) {
            this._log("rdpgfx: WIRE_TO_SURFACE_2 Progressive surface=" + surfaceId + " ctx=" + codecContextId +
                " " + bitmapData.length + "B");
        }
        // RDP_GFX_DIAG>=2: dump the full inbound Progressive bitstream at receipt time.
        if (this._diag() >= 2) {
            this._diagLog("rdpgfx: [DIAG] WIRE_TO_SURFACE_2 Progressive codecId=0x" + codecId.toString(16) +
                " fmt=0x" + pixelFormat.toString(16) + " surface=" + surfaceId + " ctx=" + codecContextId +
                " len=" + bitmapData.length + "B bytes=[" + this._hexDump(bitmapData, 1024) + "]");
        }
        this._decodeProgressive(surfaceId, surf, bitmapData, codecContextId);
    } else {
        this._log("rdpgfx: WIRE_TO_SURFACE_2 unsupported codecId 0x" + codecId.toString(16) +
            " ctx=" + codecContextId + " (" + bitmapData.length + " bytes) — skipped");
    }
};

// RemoteFX Progressive (CAPROGRESSIVE): decode the WIRE_TO_SURFACE_2 payload into 64x64 tiles.
// GNOME Remote Desktop streams the whole desktop this way, often as hundreds of tiles per PDU on
// slow links (more, smaller partial frames). Decode (RLGR → dequant → inverse DWT → YCbCr→RGB per
// tile) runs in decode-worker.js so a big frame can't block input/render; only the final composite +
// putImageData happens here, in _finishProgressive. Falls back to synchronous main-thread decode
// (_decodeProgressiveSync) if the worker isn't available.
RdpGfx.prototype._decodeProgressive = function (surfaceId, surf, bitmapData, codecContextId) {
    this._decodeSeq++;
    if (!this._worker) {
        const diagBytes = this._diag() ? bitmapData.slice() : null;
        this._decodeProgressiveSync(surfaceId, surf, bitmapData, codecContextId, diagBytes);
        return;
    }
    const reqId = ++this._workerReqId;
    // Stash the raw codec bytes for black-frame diagnostics (only when RDP_GFX_DIAG is on — the copy
    // below is transferred to the worker, so keep an independent slice here for the reply-side dump).
    this._workerPending[reqId] = { surfaceId: surfaceId, kind: "progressive",
        diagBytes: this._diag() ? bitmapData.slice() : null };
    // bitmapData is a view into the (about-to-be-reused) ZGFX inflate buffer, so copy it before the
    // transfer — postMessage with a transfer list detaches the buffer, and we don't own the original.
    const copy = bitmapData.slice();
    this._worker.postMessage(
        { cmd: "progressive", reqId: reqId, surfaceId: surfaceId, codecContextId: codecContextId,
            surfWidth: surf.width, surfHeight: surf.height, frameId: this._curFrameId,
            bitmapData: copy.buffer, verbose: this._verbose() },
        [copy.buffer]
    );
};

// Composite+paint half of progressive decode, run when decode-worker.js posts back a result (or
// inline from the main-thread fallback path with the same-shaped msg).
RdpGfx.prototype._finishProgressive = function (surfaceId, surf, msg) {
    if (!msg.ok) { this._log("rdpgfx: " + msg.error); return; }
    if (msg.empty) return;
    try {
        if (msg.sparse) {
            // The changed tiles don't form a solid rectangle (a normal, common shape for a real desktop
            // dirty region) — paint each tile individually instead of one bounding-box putImageData, so
            // we never touch pixels for 64x64 cells that weren't actually part of this PDU. Painting the
            // full bounding box in that case punches black holes into whatever content (from a previous
            // frame or a different codec) was legitimately sitting in the gaps.
            const regions = [];
            for (const t of msg.tiles) {
                const rgba = new Uint8ClampedArray(t.buffer);
                surf.ctx.putImageData(new ImageData(rgba, t.w, t.h), t.x, t.y);
                regions.push({ left: t.x, top: t.y, right: t.x + t.w, bottom: t.y + t.h });
            }
            if (this._verbose()) {
                this._log("rdpgfx: progressive PAINT(sparse) surface=" + surfaceId + " " + msg.tiles.length + " tiles");
            }
            if (this._diag() && regions.length) {
                // Diagnose against the bounding box of all painted tiles.
                let l = Infinity, t = Infinity, rr = -Infinity, bb = -Infinity;
                for (const rc of regions) { if (rc.left < l) l = rc.left; if (rc.top < t) t = rc.top; if (rc.right > rr) rr = rc.right; if (rc.bottom > bb) bb = rc.bottom; }
                this._diagPaint("Progressive(sparse)", surfaceId, surf, { left: l, top: t, right: rr, bottom: bb }, msg.diagBytes);
            }
            this._afterSurfaceUpdate(surfaceId, surf, regions);
            return;
        }
        const frame = new Uint8ClampedArray(msg.buffer);
        surf.ctx.putImageData(new ImageData(frame, msg.bw, msg.bh), msg.minX, msg.minY);
        if (this._verbose()) {
            this._log("rdpgfx: progressive PAINT surface=" + surfaceId + " at " + msg.minX + "," + msg.minY +
                " " + msg.bw + "x" + msg.bh);
        }
        const progRect = { left: msg.minX, top: msg.minY, right: msg.minX + msg.bw, bottom: msg.minY + msg.bh };
        this._diagPaint("Progressive", surfaceId, surf, progRect, msg.diagBytes);
        this._afterSurfaceUpdate(surfaceId, surf, [progRect]);
    } catch (e) {
        this._log("rdpgfx: progressive paint exception: " + e);
    }
};

// Main-thread fallback (no Worker support / Worker construction failed): identical decode+composite
// logic to decode-worker.js's decodeProgressive, just called and painted synchronously in one pass.
RdpGfx.prototype._decodeProgressiveSync = function (surfaceId, surf, bitmapData, codecContextId, diagBytes) {
    try {
        this._decodeProgressiveSyncInner(surfaceId, surf, bitmapData, codecContextId, diagBytes);
    } finally {
        this._decodeSettled(surfaceId);
    }
};

RdpGfx.prototype._decodeProgressiveSyncInner = function (surfaceId, surf, bitmapData, codecContextId, diagBytes) {
    // Keyed by surfaceId ONLY, matching FreeRDP (progressive_create_surface_context takes just the
    // surfaceId; codecContextId is ignored for tile state). Windows 11 bumps codecContextId on EVERY
    // progressive PDU, yet still sends FIRST tiles with the diff flag — those coefficients are deltas
    // to be accumulated onto that tile cell's state from the PREVIOUS PDU. Keying by codecContextId
    // gave every PDU a fresh zeroed context, so diff tiles decoded as delta-only (washed-out grey),
    // which SURFACE_TO_CACHE then snapshotted and spread. Tile state is per (surface, xIdx, yIdx).
    let ctx = this.progCtx[surfaceId];
    if (!ctx) { ctx = new this.progressive.Context(); this.progCtx[surfaceId] = ctx; }
    const self = this;
    const tiles = [];
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    // NOTE: this whole body is guarded — a single bad frame must NEVER throw out of the GFX dispatch
    // loop (that would silently stop ALL further rendering and freeze the screen). On any error we log
    // and bail on just this PDU.
    let anyClipped = false;
    try {
        const verbose = this._verbose();
        const res = this.progressive.decode(ctx, bitmapData, function (xIdx, yIdx, rgba, rects) {
            const tx = xIdx * 64, ty = yIdx * 64;
            if (tx < 0 || ty < 0 || tx >= surf.width || ty >= surf.height) return;
            const tr = Math.min(tx + 64, surf.width), tb = Math.min(ty + 64, surf.height);
            // Clip to the REGION's tileRects — see the matching comment in decode-worker.js's
            // decodeProgressive: pixels outside them must not be touched or stale coefficient-state
            // content overwrites what other codecs painted there since ([MS-RDPEGFX] 2.2.4.2.1.5).
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
                if (x !== tx || y !== ty || clips[ci][2] !== tr || clips[ci][3] !== tb || clips.length > 1) anyClipped = true;
                tiles.push({ x, y, w, h, rgba: copy });
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x + w > maxX) maxX = x + w;
                if (y + h > maxY) maxY = y + h;
            }
        }, function (m) { self._log("rdpgfx: " + m); }, verbose, this._curFrameId);

        if (!res) { this._log("rdpgfx: progressive decode failed (" + bitmapData.length + " bytes)"); return; }
    } catch (e) {
        this._log("rdpgfx: progressive EXCEPTION (" + bitmapData.length + "B): " + (e && e.stack ? e.stack : e));
        return;
    }
    if (!tiles.length) return;
    // See decode-worker.js's decodeProgressive for why sparse tile sets can't be composited into one
    // bounding-box buffer: unwritten 64x64 gaps inside the box would come out zero (black) and stamp
    // over legitimate existing pixels there. Rect-clipped sub-tiles aren't 64-aligned, so they force
    // the sparse path too.
    const bw = maxX - minX, bh = maxY - minY;
    let holes = anyClipped;
    if (!holes) {
        const colTiles = Math.ceil(bw / 64), rowTiles = Math.ceil(bh / 64);
        const written = new Uint8Array(colTiles * rowTiles);
        for (const t of tiles) {
            const ox = t.x - minX, oy = t.y - minY;
            written[(oy / 64 | 0) * colTiles + (ox / 64 | 0)] = 1;
        }
        for (let i = 0; i < written.length; i++) if (!written[i]) { holes = true; break; }
    }
    if (holes) {
        this._finishProgressive(surfaceId, surf, {
            ok: true, sparse: true, diagBytes: diagBytes,
            tiles: tiles.map(function (t) { return { x: t.x, y: t.y, w: t.w, h: t.h, buffer: t.rgba.buffer }; }),
        });
        return;
    }
    const frame = new Uint8ClampedArray(bw * bh * 4);
    for (const t of tiles) {
        const ox = t.x - minX, oy = t.y - minY;
        for (let row = 0; row < t.h; row++) {
            const src = row * t.w * 4;
            const dst = ((oy + row) * bw + ox) * 4;
            frame.set(t.rgba.subarray(src, src + t.w * 4), dst);
        }
    }
    this._finishProgressive(surfaceId, surf, { ok: true, minX: minX, minY: minY, bw: bw, bh: bh, buffer: frame.buffer, diagBytes: diagBytes });
};

// AVC420 bitstream ([MS-RDPEGFX] 2.2.4.4 / 2.2.4.5): an RFX_AVC420_METABLOCK (region rects + quant
// quality) followed by the raw H.264 (Annex-B) bitstream. The metablock's region rects tell us which
// parts of the decoded frame changed; we draw the whole decoded frame into the surface at the rects'
// bounding origin (the frame the encoder produced covers exactly the union of the regions).
RdpGfx.prototype._decodeAvc420 = function (surfaceId, surf, destRect, data) {
    // Preferred path: decode in the worker (off the main thread). The whole AVC420 PDU (metablock +
    // Annex-B) is parsed and submitted to a worker-side VideoDecoder there; the finished VideoFrame is
    // transferred back and painted by _paintH264Frame. Submission is barriered like progressive so a
    // following order-sensitive op (SURFACE_TO_SURFACE etc.) waits for the PDU to be consumed.
    if (this._worker) {
        this._decodeSeq++;
        const reqId = ++this._workerReqId;
        this._workerPending[reqId] = { surfaceId: surfaceId, kind: "h264" };
        // data is a view into the ZGFX inflate buffer (about to be reused) — copy before transfer.
        const copy = data.slice();
        this._worker.postMessage(
            { cmd: "h264", reqId: reqId, surfaceId: surfaceId, bitmapData: copy.buffer },
            [copy.buffer]
        );
        return;
    }
    // Fallback: no worker (CSP / no Worker support) — decode synchronously on the main thread.
    const r = new ByteReader(data);
    const numRegionRects = r.u32le();
    const rects = [];
    for (let i = 0; i < numRegionRects; i++) {
        rects.push({ left: r.u16le(), top: r.u16le(), right: r.u16le(), bottom: r.u16le() });
    }
    // quantQualityVals: numRegionRects * 2 bytes (qpVal, qualityVal) — we don't need them for decode.
    r.skip(numRegionRects * 2);
    const h264 = r.bytes(r.remaining());
    let dec = this.decoders[surfaceId];
    if (!dec) {
        dec = new H264SurfaceDecoder(surf, this, surfaceId);
        this.decoders[surfaceId] = dec;
    }
    dec.decode(h264, rects, destRect);
};

// AVC444 bitstream ([MS-RDPEGFX] 2.2.4.6): a 4-byte field = cbAvc420EncodedBitstream1(30 bits) | LC(2
// bits), then AVC420 stream 1 (the YUV420 "main" view = luma + subsampled chroma), and when LC==0 a
// second AVC420 stream carrying the auxiliary chroma plane for full 4:4:4. We decode ONLY stream 1 via
// the AVC420 path — that yields a correct, sharp YUV420 image (the chroma-refinement second stream is
// dropped, so deep-color gradients are 4:2:0 rather than 4:4:4, which is visually fine for a desktop).
RdpGfx.prototype._decodeAvc444 = function (surfaceId, surf, destRect, data) {
    const r = new ByteReader(data);
    if (r.remaining() < 4) return;
    const w0 = r.u32le();
    const cbStream1 = w0 & 0x3fffffff;     // length of AVC420 stream 1 (incl. its metablock)
    const lc = (w0 >>> 30) & 0x3;          // 0 = both streams present, 1 = only stream 1, 2 = only aux
    if (lc === 3) { this._log("rdpgfx: AVC444 invalid LC=3"); return; }
    // Stream 1 (main view) is a full AVC420 bitstream (metablock + Annex-B). For LC==2 (aux only) there
    // is no main view to render — skip. Otherwise hand stream 1 to the AVC420 decoder.
    if (lc === 2) return;
    const stream1Len = (lc === 0) ? cbStream1 : r.remaining();
    if (stream1Len > r.remaining()) { this._log("rdpgfx: AVC444 bad stream1 length"); return; }
    const stream1 = r.bytes(stream1Len);
    this._decodeAvc420(surfaceId, surf, destRect, stream1);
};

// ClearCodec (0x8): decode the tile into an RGBA buffer and composite it onto the surface at the dest
// rect. Decodes SYNCHRONOUSLY on the main thread (not in decode-worker.js like progressive): FreeRDP
// caches a GLYPH_INDEX tile from the DESTINATION SURFACE after composing — uncovered pixels get the
// pre-existing surface content baked in — and only the main thread can read the surface canvas. The
// worker's isolated glyph cache stored the raw decode buffer (with alpha-0 holes) instead, so every
// GLYPH_HIT replay diverged from the host's pixel model, leaving thin permanently-stale strips.
// ClearCodec tiles are small (glyphs cap at 1024 pixels; residual/bands cover UI-sized rects), so the
// main-thread cost is negligible next to progressive, which stays on the worker. PDU ordering relative
// to in-flight worker decodes is enforced upstream: _dispatch treats ClearCodec WIRE_TO_SURFACE_1 as
// order-sensitive and queues it behind the decode barrier, same as uncompressed.
RdpGfx.prototype._decodeClear = function (surfaceId, surf, rect, data) {
    if (!this.clear) { this._log("rdpgfx: ClearCodec module not loaded"); return; }
    const w = rect.right - rect.left, h = rect.bottom - rect.top;
    if (w <= 0 || h <= 0) return;
    const self = this;
    let res = null;
    // Guarded like the progressive sync path: one malformed tile must never throw out of the GFX
    // dispatch loop (that would silently stop ALL rendering); log and drop just this PDU.
    try {
        res = this.clear.decode(data, w, h, function (m) { self._log("rdpgfx: " + m); }, this._verbose());
    } catch (e) {
        this._log("rdpgfx: ClearCodec EXCEPTION: " + (e && e.stack ? e.stack : e));
        return;
    }
    if (!res) {
        this._log("rdpgfx: ClearCodec decode failed (" + w + "x" + h + ", " + data.length + " bytes)");
        return;
    }
    // Keep the raw codec bytes for black-frame diagnostics (only when RDP_GFX_DIAG is on — copy so the
    // ZGFX inflate buffer this views into can be reused; a no-op alloc when diagnostics are off).
    const diagBytes = this._diag() ? data.slice() : null;
    this._finishClear(surfaceId, surf, { ok: true, rect: rect, buffer: res.rgba.buffer, glyphEntry: res.glyphEntry, diagBytes: diagBytes });
};

RdpGfx.prototype._finishClear = function (surfaceId, surf, msg) {
    if (!msg.ok) { this._log("rdpgfx: " + msg.error); return; }
    try {
        const rgba = new Uint8ClampedArray(msg.buffer);
        const w = msg.rect.right - msg.rect.left, h = msg.rect.bottom - msg.rect.top;
        // ClearCodec layers (residual/bands/subcodecs) need not cover the whole dest rect — FreeRDP
        // writes each layer's pixels straight onto the surface and leaves the rest untouched. Our
        // decoder leaves uncovered pixels transparent (alpha 0), so composite with drawImage
        // (source-over) via a scratch canvas instead of putImageData: putImageData REPLACES pixels,
        // stamping the uncovered ones out as opaque black. Those black holes then also got snapshotted
        // into the GFX bitmap cache by SURFACE_TO_CACHE and replayed on every CACHE_TO_SURFACE
        // (window drags / hover redraws), spreading the corruption far beyond the original rect.
        if (!this._clearScratch || this._clearScratch.width < w || this._clearScratch.height < h) {
            const cw = Math.max(w, this._clearScratch ? this._clearScratch.width : 0);
            const ch = Math.max(h, this._clearScratch ? this._clearScratch.height : 0);
            this._clearScratch = (typeof OffscreenCanvas !== "undefined")
                ? new OffscreenCanvas(cw, ch)
                : Object.assign(document.createElement("canvas"), { width: cw, height: ch });
            this._clearScratchCtx = this._clearScratch.getContext("2d"); // alpha:true — carries the coverage mask
        }
        this._clearScratchCtx.putImageData(new ImageData(rgba, w, h), 0, 0);
        surf.ctx.drawImage(this._clearScratch, 0, 0, w, h, msg.rect.left, msg.rect.top, w, h);
        // GLYPH_INDEX store: re-snapshot the glyph from the COMPOSED surface rect, exactly like
        // FreeRDP (which copies out of pDstData after all layers landed). The decode-buffer copy
        // clear.js stored is provisional — its uncovered pixels are alpha-0 holes, but the host's
        // model says the glyph holds the fully-composed rect, and a later GLYPH_HIT paints it as-is.
        if (msg.glyphEntry) {
            const snap = surf.ctx.getImageData(msg.rect.left, msg.rect.top, w, h);
            msg.glyphEntry.pixels.set(new Uint32Array(snap.data.buffer, 0, w * h));
        }
        if (this._verbose()) {
            this._log("rdpgfx: ClearCodec PAINT surface=" + surfaceId + " at " + msg.rect.left + "," + msg.rect.top +
                " " + w + "x" + h);
        }
        this._diagPaint("ClearCodec", surfaceId, surf, msg.rect, msg.diagBytes);
        this._afterSurfaceUpdate(surfaceId, surf, [msg.rect]);
    } catch (e) {
        this._log("rdpgfx: ClearCodec paint exception: " + e);
    }
};

RdpGfx.prototype.onDecodedFrame = function (surfaceId, frame, regions) {
    const surf = this.surfaces[surfaceId];
    if (!surf) {
        if (frame.close) frame.close();
        return;
    }

    const self = this;

    const cw = frame.codedWidth;
    const ch = frame.codedHeight;

    if (!cw || !ch) {
        if (frame.close) frame.close();
        return;
    }

    const paintFrame = function (bitmap) {
        try {
            // Draw full frame (NO RGBA extraction, NO stride risk)
            surf.ctx.drawImage(bitmap, 0, 0, cw, ch);

            self._afterSurfaceUpdate(surfaceId, surf, [
                { left: 0, top: 0, right: cw, bottom: ch }
            ]);

        } catch (e) {
            self._log("rdpgfx: bitmap paint failed: " + (e && e.message || e));
        } finally {
            if (bitmap && bitmap.close) bitmap.close();
            if (frame.close) frame.close();
        }
    };

    // Fast path: draw the VideoFrame straight onto the 2D context. Canvas2D accepts a VideoFrame as an
    // image source on every current engine (Chrome/Edge/Firefox, Safari 16.4+), so this needs no
    // intermediate ImageBitmap allocation and — crucially — no extra async event-loop turn per frame,
    // which matters now that we render at full video frame rate. On the rare engine that rejects a
    // VideoFrame source (older Safari/iOS), fall back to createImageBitmap.
    let drawn = false;
    try {
        surf.ctx.drawImage(frame, 0, 0, cw, ch);
        drawn = true;
    } catch (e) {
        // Fall through to the ImageBitmap path below.
    }
    if (drawn) {
        try {
            self._afterSurfaceUpdate(surfaceId, surf, [{ left: 0, top: 0, right: cw, bottom: ch }]);
        } finally {
            if (frame.close) frame.close();
        }
        return;
    }

    // Compatibility path (older Safari/iOS): decode the frame into an ImageBitmap first.
    if (typeof createImageBitmap === "function") {
        createImageBitmap(frame)
            .then(paintFrame)
            .catch(function (e) {
                self._log("rdpgfx: createImageBitmap failed: " + (e && e.message || e));
                if (frame.close) frame.close();
            });
        return;
    }

    if (frame.close) frame.close();
};

// Fallback render path: createImageBitmap(frame) → drawImage per region rect. Used only when copyTo is
// unavailable or rejects. Consumes (closes) the frame.
RdpGfx.prototype._paintViaBitmap = function (surfaceId, surf, frame, rects, cw, ch) {
    const self = this;
    createImageBitmap(frame).then(function (bmp) {
        try {
            for (const rc of rects) {
                const sl = Math.max(0, rc.left), st = Math.max(0, rc.top);
                const sr = Math.min(cw, rc.right), sb = Math.min(ch, rc.bottom);
                const w = sr - sl, h = sb - st;
                if (w <= 0 || h <= 0) continue;
                surf.ctx.drawImage(bmp, sl, st, w, h, sl, st, w, h);
            }
            bmp.close && bmp.close();
            self._afterSurfaceUpdate(surfaceId, surf, rects);
        } catch (e) {
            self._log("rdpgfx: H264 bitmap paint threw: " + (e && e.message || e));
        } finally {
            if (frame.close) frame.close();
        }
    }).catch(function (e) {
        self._log("rdpgfx: H264 createImageBitmap failed: " + (e && e.message || e));
        if (frame.close) frame.close();
    });
};

// Scan the whole surface canvas for any non-black pixel and report stats — to tell "decoded to black"
// from "decoded fine but the sampled point happens to be black".
// After a surface region updates, push it to the output if the surface is mapped. Each changed region
// is composited immediately (per-region) so partial updates appear without waiting for a full frame.
RdpGfx.prototype._afterSurfaceUpdate = function (surfaceId, surf, regions) {
    surf.touched = true; // has real content now — MAP-time full blits may paint it
    for (const rc of regions) this._paintSurface(surfaceId, 0, 0, surf, rc);
};

// Repaint every currently-mapped, touched surface onto the output canvas in full. Needed after the
// output canvas itself gets resized (client.js's _onGfxReset does `canvas.width = w`, which per the
// HTML spec clears the ENTIRE canvas to transparent black regardless of whether content changed) — the
// GFX surfaces still hold valid decoded pixels, but without this, only regions that happen to receive a
// fresh WIRE_TO_SURFACE PDU after the resize get re-blitted, leaving every static/unchanging area of the
// desktop permanently black until the host redraws it (which may be never, for idle UI chrome).
RdpGfx.prototype.repaintAll = function () {
    for (const surfaceId in this.surfaces) this._paintSurface(surfaceId | 0, 0, 0, this.surfaces[surfaceId], null);
};

// Blit a surface region to the output via onPaint. `region` null => whole surface.
RdpGfx.prototype._paintSurface = function (surfaceId, _x, _y, surf, region) {
    surf = surf || this.surfaces[surfaceId];
    const map = this.outputMap[surfaceId];
    if (!surf || !map || !this.cb.onPaint) return;
    // Never blit a surface that has not received any content yet: MAP_SURFACE_TO_OUTPUT arrives for
    // freshly-created (empty) surfaces during a host-side reconfigure, and painting the empty surface
    // would black out the last good frame on the output canvas. The desktop is unchanged server-side,
    // so keeping the previous pixels is exactly right until the first real update lands.
    if (!surf.touched) return;
    const sx = region ? region.left : 0;
    const sy = region ? region.top : 0;
    const sw = region ? (region.right - region.left) : surf.width;
    const sh = region ? (region.bottom - region.top) : surf.height;
    this.cb.onPaint(surf.canvas, sx, sy, sw, sh, map.originX + sx, map.originY + sy);
};

// UNCOMPRESSED bitmap: raw 32bpp BGRX/BGRA, top-down, width*height*4 bytes for the dest rect.
RdpGfx.prototype._drawUncompressed = function (surf, rect, data, pixelFormat) {
    const w = rect.right - rect.left, h = rect.bottom - rect.top;
    if (w <= 0 || h <= 0 || data.length < w * h * 4) return;
    const rgba = new Uint8ClampedArray(w * h * 4);
    // Source bytes are in memory order B,G,R,X (FreeRDP PIXEL_FORMAT_BGRX32) → canvas wants R,G,B,A.
    for (let i = 0, n = w * h; i < n; i++) {
        const s = i * 4;
        rgba[s] = data[s + 2];     // R
        rgba[s + 1] = data[s + 1]; // G
        rgba[s + 2] = data[s];     // B
        rgba[s + 3] = (pixelFormat === GFX_PIXEL_FORMAT_ARGB_8888) ? data[s + 3] : 255;
    }
    surf.ctx.putImageData(new ImageData(rgba, w, h), rect.left, rect.top);
    const surfId = this._surfaceIdOf(surf);
    if (surfId != null) this._afterSurfaceUpdate(surfId, surf, [rect]);
};

RdpGfx.prototype._surfaceIdOf = function (surf) {
    for (const id in this.surfaces) if (this.surfaces[id] === surf) return id | 0;
    return null;
};

// SOLIDFILL: fill one or more rects of a surface with a solid color (used for clears/letterboxing).
RdpGfx.prototype._onSolidFill = function (r) {
    const surfaceId = r.u16le();
    const b = r.u8(), g = r.u8(), rd = r.u8(), xa = r.u8(); // RDPGFX_COLOR32 B,G,R,XA
    const fillRectCount = r.u16le();
    const surf = this.surfaces[surfaceId];
    if (!surf) return;
    // XA is only meaningful on ARGB surfaces ([MS-RDPEGFX] 2.2.1.2); on XRGB surfaces Windows sends
    // 0x00 there, and honoring it makes the fill fully transparent — fillRect becomes a no-op and the
    // rect keeps its stale pixels (FreeRDP's gdi_SolidFill hardcodes 0xFF for the same reason).
    // [MS-RDPEGFX] 3.3.5.4 / FreeRDP gdi_SolidFill: the fill pixel's alpha is ALWAYS ignored — the fill is
    // opaque on EVERY surface, XRGB and ARGB alike (FreeRDP hardcodes `BYTE a = 0xff` and comments "the
    // alpha value is always ignored"). We previously honored `xa` on ARGB (0x21) surfaces; Windows sends
    // xa=0 there for an opaque fill, so honoring it made the fill transparent/black — an ARGB desktop
    // surface (fmt 0x21, which this host uses) then had SOLIDFILL rects blacked out, and SURFACE_TO_CACHE
    // snapshotted that black and CACHE_TO_SURFACE tiled it across the desktop (the persistent black areas).
    void xa; // parsed for completeness; deliberately unused (alpha ignored)
    const a = 255;
    surf.ctx.fillStyle = "rgba(" + rd + "," + g + "," + b + "," + (a / 255) + ")";
    const updated = [];
    for (let i = 0; i < fillRectCount; i++) {
        const left = r.u16le(), top = r.u16le(), right = r.u16le(), bottom = r.u16le();
        surf.ctx.fillRect(left, top, right - left, bottom - top);
        updated.push({ left, top, right, bottom });
    }
    // A SOLIDFILL with a black color over a large rect is itself a "black frame" source — surface the
    // fill color + rects when it lands black (RDP_GFX_DIAG). There is no codec bitstream, so the byte
    // dump carries the RGBA fill value instead.
    if (this._diag() && updated.length) {
        const colorBytes = new Uint8Array([rd, g, b, a]);
        for (const rc of updated) this._diagPaint("SOLIDFILL(rgba=" + rd + "," + g + "," + b + "," + a + ")", surfaceId, surf, rc, colorBytes);
    }
    this._afterSurfaceUpdate(surfaceId, surf, updated);
};

// SURFACE_TO_SURFACE: copy a rect from one surface to one or more destination points on another.
RdpGfx.prototype._onSurfaceToSurface = function (r) {
    const srcId = r.u16le();
    const dstId = r.u16le();
    const rectSrcLeft = r.u16le(), rectSrcTop = r.u16le(), rectSrcRight = r.u16le(), rectSrcBottom = r.u16le();
    const destPtCount = r.u16le();
    const src = this.surfaces[srcId], dst = this.surfaces[dstId];
    const w = rectSrcRight - rectSrcLeft, h = rectSrcBottom - rectSrcTop;
    if (!src || !dst || w <= 0 || h <= 0) {
        this._log("rdpgfx: SURFACE_TO_SURFACE src=" + srcId + " dst=" + dstId +
            " rect=[" + rectSrcLeft + "," + rectSrcTop + "," + rectSrcRight + "," + rectSrcBottom + "]" +
            " — SKIPPED (" + (!src ? "unknown src" : !dst ? "unknown dst" : "empty rect") + "), dest points will be BLACK/stale");
        return;
    }
    const updated = [];
    for (let i = 0; i < destPtCount; i++) {
        const dx = r.u16le(), dy = r.u16le();
        dst.ctx.drawImage(src.canvas, rectSrcLeft, rectSrcTop, w, h, dx, dy, w, h);
        updated.push({ left: dx, top: dy, right: dx + w, bottom: dy + h });
    }
    // If the source rect on `src` was itself black, this blit spreads black onto dst — surface it.
    if (this._diag()) {
        for (const rc of updated) this._diagPaint("SURFACE_TO_SURFACE(src=" + srcId + " srcRect=[" + rectSrcLeft + "," + rectSrcTop + "," + rectSrcRight + "," + rectSrcBottom + "])", dstId, dst, rc, null);
    }
    this._afterSurfaceUpdate(dstId, dst, updated);
};

// SURFACE_TO_CACHE ([MS-RDPEGFX] 2.2.2.6): snapshot a source rect of a surface into a cache slot, so a
// later CACHE_TO_SURFACE can re-blit it cheaply (used for scrolling, repeated UI, glyph runs).
RdpGfx.prototype._onSurfaceToCache = function (r) {
    const surfaceId = r.u16le();
    /* cacheKey (8 bytes) — we key purely on cacheSlot, like FreeRDP's slot table */ r.skip(8);
    const cacheSlot = r.u16le();
    const left = r.u16le(), top = r.u16le(), right = r.u16le(), bottom = r.u16le();
    const surf = this.surfaces[surfaceId];
    const w = right - left, h = bottom - top;
    if (!surf || w <= 0 || h <= 0) {
        this._log("rdpgfx: SURFACE_TO_CACHE slot=" + cacheSlot + " surface=" + surfaceId +
            " rect=[" + left + "," + top + "," + right + "," + bottom + "] — DROPPED (no surface or empty rect); " +
            "any later CACHE_TO_SURFACE for slot " + cacheSlot + " will be BLACK/stale");
        return;
    }
    if (this._verbose()) {
        this._log("rdpgfx: SURFACE_TO_CACHE slot=" + cacheSlot + " surface=" + surfaceId +
            " rect=[" + left + "," + top + "," + right + "," + bottom + "] " + w + "x" + h);
    }
    let slot = this.cache[cacheSlot];
    if (!slot || slot.w !== w || slot.h !== h) {
        const canvas = (typeof OffscreenCanvas !== "undefined")
            ? new OffscreenCanvas(w, h)
            : Object.assign(document.createElement("canvas"), { width: w, height: h });
        // alpha:false to match surfaces — cache slots hold opaque surface snapshots; keeping them opaque
        // stops any alpha from sneaking back onto a surface via CACHE_TO_SURFACE (same ghost mechanism).
        slot = this.cache[cacheSlot] = { canvas, ctx: canvas.getContext("2d", { alpha: false }), w, h };
    }
    slot.srcLeft = left; slot.srcTop = top; // diagnostic only: origin this snapshot was taken from
    slot.ctx.drawImage(surf.canvas, left, top, w, h, 0, 0, w, h);
    // Record whether the SOURCE surface rect was black AT SNAPSHOT TIME. This is the crux of the "black
    // returns from cache" bug — if the slot is snapshotted black, the content for that region never landed
    // on the surface before the host cached it (upstream drop / mis-order), and every later
    // CACHE_TO_SURFACE faithfully replays black. This check runs ALWAYS (even in prod, flag off): it's the
    // root-cause signal and cheap — one small readback per cache-populate, and SURFACE_TO_CACHE is far less
    // frequent than CACHE_TO_SURFACE. We skip the scan entirely for benign base-layer corners/edges (the
    // host's expected black background) via a coordinate test first, so only INTERIOR regions are scanned.
    {
        // Corner/edge tile (touches x==0, top, or the bottom/right edge) = host's black background — skip.
        const isEdge = (left <= 0) || (top <= 0) || (bottom >= (this.outputHeight || 1e9)) || (right >= (this.outputWidth || 1e9));
        slot.snapBlack = !isEdge && this._scanBlack(surf, { left: left, top: top, right: right, bottom: bottom }).black;
        if (slot.snapBlack) {
            // Distinguish an ORDERING bug from a MISSING-content bug: report whether an async decode was
            // still in flight at snapshot time (decodeSeq > settledSeq) or any op is queued behind the
            // barrier. If in-flight/queued, the barrier failed to hold this SURFACE_TO_CACHE and we cached
            // black too early (ordering). If NOT (fully settled, empty queue), the host cached a region
            // whose content we never painted at all (missing/dropped content upstream).
            const inflight = this._decodeSeq - this._decodeSettledSeq;
            const headBarrier = this._orderedQueue.length ? this._orderedQueue[0].barrier : null;
            this._diagLog("rdpgfx: [DIAG] SURFACE_TO_CACHE slot=" + cacheSlot + " snapshotted BLACK from surface=" +
                surfaceId + " rect=[" + left + "," + top + "," + right + "," + bottom + "]" +
                " inflightDecodes=" + inflight + " queuedOps=" + this._orderedQueue.length +
                " decodeSeq=" + this._decodeSeq + " settledSeq=" + this._decodeSettledSeq +
                " queueHeadBarrier=" + headBarrier +
                (inflight > 0 || this._orderedQueue.length
                    ? " — ORDERING: barrier let SURFACE_TO_CACHE snapshot before content painted"
                    : " — MISSING: no decode pending, host cached a region we never painted (dropped content)"));
        }
    }
};

// CACHE_TO_SURFACE ([MS-RDPEGFX] 2.2.2.7): blit a cached slot onto a surface at one or more points.
RdpGfx.prototype._onCacheToSurface = function (r) {
    const cacheSlot = r.u16le();
    const surfaceId = r.u16le();
    const destPtsCount = r.u16le();
    const surf = this.surfaces[surfaceId];
    const slot = this.cache[cacheSlot];
    if (!surf || !slot) {
        // Silent before this change: a missing surface or an empty/never-populated cache slot (e.g. a
        // dropped SURFACE_TO_CACHE, or the host referencing a slot id we never cached — see
        // _onSurfaceToCache's DROPPED log for the other half of this) meant every dest point below was
        // skipped with no trace, leaving those rects permanently black/stale on screen.
        this._log("rdpgfx: CACHE_TO_SURFACE slot=" + cacheSlot + " surface=" + surfaceId +
            " — " + (!surf ? "unknown surface" : "empty/missing cache slot") +
            " (" + destPtsCount + " dest points) — SKIPPED, dest rects will be BLACK/stale");
        return;
    }
    const verbose = this._verbose();
    const updated = [];
    for (let i = 0; i < destPtsCount; i++) {
        const dx = r.u16le(), dy = r.u16le();
        surf.ctx.drawImage(slot.canvas, dx, dy);
        if (verbose) {
            this._log("rdpgfx: CACHE_TO_SURFACE slot=" + cacheSlot + " (from rect=[" + slot.srcLeft + "," + slot.srcTop +
                "," + (slot.srcLeft + slot.w) + "," + (slot.srcTop + slot.h) + "]) surface=" + surfaceId +
                " " + slot.w + "x" + slot.h + " -> " + dx + "," + dy);
        }
        updated.push({ left: dx, top: dy, right: dx + slot.w, bottom: dy + slot.h });
    }
    // A cache slot that was snapshotted while black (or never really populated) paints black here. Rather
    // than logging every dest point (a full-desktop background tiles the same black slot HUNDREDS of times
    // and floods/truncates the console), coalesce: count how many dest rects came out black and emit ONE
    // line naming the slot, its snapshot origin, and — critically — whether the slot was black AT SNAPSHOT
    // TIME (slot.snapBlack, set in _onSurfaceToCache). snapBlack=true ⇒ upstream never painted the content;
    // snapBlack=false but replayed black ⇒ look at the blit. Level 2 still logs per-rect for fine tracing.
    // Benign base-layer tiling: the host fills the whole desktop with a black background by snapshotting a
    // black corner tile (slot from [0,0]/[…,0]/…) and CACHE_TO_SURFACE-ing it across the screen (many dest
    // points). That's CORRECT — real content paints OVER it later. Logging it floods/truncates the console
    // and hides the actual bug (content regions that stay black). So skip the black report for a large,
    // many-dest tiling from a screen-corner source; keep reporting every other black replay (e.g. a taskbar
    // icon region that should have content but replays black — the real desync).
    const isBaseLayerTiling = updated.length >= 4 &&
        (slot.srcLeft | 0) <= 0 && ((slot.srcTop | 0) <= 0 || slot.srcTop + slot.h >= (this.outputHeight || 1e9));
    if (this._diag() && !isBaseLayerTiling) {
        let blackCount = 0;
        const blackDests = [];
        for (const rc of updated) {
            if (this._scanBlack(surf, rc).black) { blackCount++; if (blackDests.length < 12) blackDests.push("[" + rc.left + "," + rc.top + "]"); }
            if (this._diag() >= 2) this._diagPaint("CACHE_TO_SURFACE(slot=" + cacheSlot + ")", surfaceId, surf, rc, null);
        }
        if (blackCount) {
            this._diagLog("rdpgfx: [DIAG] BLACK CACHE_TO_SURFACE slot=" + cacheSlot + " -> " + blackCount + "/" +
                updated.length + " dest rects black; snapshotted from surface rect=[" + slot.srcLeft + "," +
                slot.srcTop + "," + (slot.srcLeft + slot.w) + "," + (slot.srcTop + slot.h) + "] " +
                slot.w + "x" + slot.h + " snapBlack=" + (slot.snapBlack === true) +
                " destsAt=" + blackDests.join(",") +
                (slot.snapBlack ? " (ROOT CAUSE: slot cached black — content never landed upstream)"
                                : " (slot cached NON-black yet replays black — investigate the blit/surface state)"));
        }
    }
    if (updated.length) this._afterSurfaceUpdate(surfaceId, surf, updated);
};

// ================================================================================================
// H.264 (AVC420) decode via WebCodecs VideoDecoder
// ================================================================================================
// One decoder per surface. RDP AVC420 carries Annex-B NAL units (SPS/PPS in the first I-frame, then
// slices). WebCodecs VideoDecoder configured WITHOUT a `description` treats EncodedVideoChunk data as
// Annex-B, which is exactly what the host sends — so we forward the bitstream verbatim. We tag the
// first chunk after an SPS/IDR as a key frame and the rest as delta.
function H264SurfaceDecoder(surf, gfx, surfaceId) {
    this.surf = surf;
    this.gfx = gfx;
    this.surfaceId = surfaceId;
    this.decoder = null;
    this.configured = false;
    this.unsupported = false;
    this._pendingRegions = [];   // FIFO of region-rect lists, matched to output frames in order
    this._ts = 0;
}

// Scan Annex-B for an IDR (NAL type 5) or SPS (7) to mark a keyframe; VideoDecoder requires the first
// chunk (and any after a flush) to be a key frame.
H264SurfaceDecoder.prototype._isKeyFrame = function (data) {
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

// Find the SPS NAL (type 7) in an Annex-B stream and return {profile, constraints, level} from its
// first three bytes (profile_idc, constraint_set flags, level_idc) — used to build the exact codec
// string. Returns null if no SPS is present.
H264SurfaceDecoder.prototype._parseSps = function (data) {
    for (let i = 0; i + 5 < data.length; i++) {
        let hdr = -1, p = -1;
        if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1) { hdr = data[i + 3]; p = i + 4; }
        else if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 0 && data[i + 3] === 1) { hdr = data[i + 4]; p = i + 5; }
        if (hdr >= 0 && (hdr & 0x1f) === 7) {
            if (p + 2 < data.length) return { profile: data[p], constraints: data[p + 1], level: data[p + 2] };
        }
    }
    return null;
};

// Build the WebCodecs codec string "avc1.PPCCLL" from SPS bytes (each hex, 2 digits).
function avcCodecString(sps) {
    const h = function (b) { return (b & 0xff).toString(16).padStart(2, "0"); };
    return "avc1." + h(sps.profile) + h(sps.constraints) + h(sps.level);
}

// Split an Annex-B buffer into its NAL units (payloads WITHOUT the start code). Handles both 3- and
// 4-byte start codes.
function annexbSplit(data) {
    const nals = [];
    let i = 0;
    const n = data.length;
    // Find first start code.
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

// Build an AVCDecoderConfigurationRecord (avcC) from SPS + PPS NAL payloads ([ISO 14496-15]).
function buildAvcC(sps, ppsList) {
    const parts = [];
    parts.push(0x01, sps[1], sps[2], sps[3], 0xff); // version, profile, compat, level, lengthSizeMinusOne(=3)|reserved
    parts.push(0xe1);                                // reserved(111) | numSPS(1)
    parts.push((sps.length >> 8) & 0xff, sps.length & 0xff);
    for (let k = 0; k < sps.length; k++) parts.push(sps[k]);
    parts.push(ppsList.length & 0xff);               // numPPS
    for (const pps of ppsList) {
        parts.push((pps.length >> 8) & 0xff, pps.length & 0xff);
        for (let k = 0; k < pps.length; k++) parts.push(pps[k]);
    }
    return new Uint8Array(parts);
}

// Convert an Annex-B access unit to AVCC (4-byte length-prefixed NALs), dropping AUD/SPS/PPS/SEI NALs
// that are carried out-of-band via the avcC `description` (and AUD/SEI which can confuse some decoders).
function annexbToAvcc(data, dropParamSets) {
    const nals = annexbSplit(data);
    let total = 0;
    const keep = [];
    for (const nal of nals) {
        const t = nal[0] & 0x1f;
        if (dropParamSets && (t === 7 || t === 8 || t === 9)) continue; // SPS/PPS/AUD out-of-band
        keep.push(nal); total += 4 + nal.length;
    }
    const out = new Uint8Array(total);
    let o = 0;
    for (const nal of keep) {
        out[o] = (nal.length >>> 24) & 0xff; out[o + 1] = (nal.length >>> 16) & 0xff;
        out[o + 2] = (nal.length >>> 8) & 0xff; out[o + 3] = nal.length & 0xff;
        out.set(nal, o + 4); o += 4 + nal.length;
    }
    return out;
}

// Configure the decoder from the keyframe. We pass SPS/PPS out-of-band as an avcC `description` and feed
// AVCC (length-prefixed) frames thereafter. In-band Annex-B with multi-slice frames made Firefox decode
// to all-black; the avcC/AVCC path is the reliable WebCodecs H.264 configuration.
H264SurfaceDecoder.prototype._configureFrom = function (annexb) {
    if (this.configured || this.unsupported) return;
    if (typeof VideoDecoder === "undefined") {
        this.unsupported = true;
        this.gfx._log("rdpgfx: WebCodecs VideoDecoder unavailable — H.264 cannot be decoded");
        return;
    }
    const nals = annexbSplit(annexb);
    let sps = null; const ppsList = [];
    for (const nal of nals) {
        const t = nal[0] & 0x1f;
        if (t === 7 && !sps) sps = nal;
        else if (t === 8) ppsList.push(nal);
    }
    // Config mode. The spec ([MS-RDPEGFX] 2.2.4.4: avc420EncodedBitstream is "a single frame ...
    // conforming to ... Annex B") guarantees: in-band SPS/PPS on each keyframe, ONE frame per access
    // unit, and NO B-frames (a live desktop encoder never reorders). So decode order == output order
    // and nothing should sit in a reorder buffer. We therefore prefer the SPEC-NATIVE Annex-B in-band
    // config (no avcC `description`): the decoder parses the real SPS+VUI and honours its low-delay
    // signaling, emitting each frame immediately. The avcC path strips SPS/PPS out-of-band and made
    // some decoders apply a conservative multi-frame output delay (the ~1-2 frame lag observed — the
    // newest frame stays buffered, so on a static desktop the picture looks frozen one frame behind).
    // (The earlier "annexb → all black" note was the createImageBitmap RENDER bug, since fixed by the
    // copyTo-RGBA path — NOT a decode failure — so that objection no longer applies.)
    // Set window.RDP_GFX_AVCC=1 to force the old out-of-band avcC path for comparison.
    const forceAvcc = (typeof window !== "undefined" && window.RDP_GFX_AVCC);
    if (forceAvcc && sps && ppsList.length) {
        this._avcc = true;
        this._desc = buildAvcC(sps, ppsList);
    } else {
        this._avcc = false; // spec-native Annex-B in-band
    }
    const codec = sps ? avcCodecString({ profile: sps[1], constraints: sps[2], level: sps[3] }) : "avc1.4d402a";
    const self = this;
    if (!this.decoder) {
        this.decoder = new VideoDecoder({
            output: function (frame) { self._onFrame(frame); },
            // A WebCodecs decoder error closes the decoder. If we don't reset our state, every later
            // decode() silently throws and frames stop emitting while the host keeps streaming — the
            // picture freezes ("intermittent stall"). Mark ourselves un-configured so the NEXT keyframe
            // rebuilds a fresh decoder. Drop any pending region lists (they belong to lost frames).
            error: function (e) {
                self.gfx._log("rdpgfx: H264 decoder error: " + (e && e.message || e) + " — will rebuild on next keyframe");
                try { if (self.decoder && self.decoder.state !== "closed") self.decoder.close(); } catch (_) {}
                self.decoder = null;
                self.configured = false;
                self._gotKey = false;
                self._pendingRegions = [];
            },
        });
    }
    const cfg = { codec: codec, optimizeForLatency: true };
    if (this._avcc) cfg.description = this._desc;
    try {
        this.decoder.configure(cfg);
        this.configured = true;
        this.gfx._log("rdpgfx: H264 configured codec=" + codec + " mode=" + (this._avcc ? "avcC(" + this._desc.length + "B)" : "annexb"));
    } catch (e) {
        this.unsupported = true;
        this.gfx._log("rdpgfx: VideoDecoder.configure(" + codec + ") failed: " + (e && e.message || e));
    }
};

H264SurfaceDecoder.prototype.decode = function (annexb, regions, _destRect) {
    const key = this._isKeyFrame(annexb);
    // Configure lazily from the first keyframe so we can extract SPS/PPS for the avcC description.
    if (!this.configured && key) this._configureFrom(annexb);
    if (this.unsupported || !this.decoder || !this.configured) {
        if (!this._gotKey && !key) return; // still waiting for the first keyframe to configure
        this.gfx._log("rdpgfx: H264 not configured (unsupported=" + this.unsupported + ")");
        return;
    }
    // VideoDecoder must START on a key frame; drop deltas until the first keyframe arrives.
    if (!this._gotKey && !key) { this.gfx._log("rdpgfx: H264 dropping delta before first keyframe"); return; }
    if (key) this._gotKey = true;
    this._pendingRegions.push(regions);
    // In avcC mode, convert the Annex-B AU to AVCC and strip the in-band SPS/PPS/AUD (carried in the
    // description). In annexb fallback mode, feed the raw bytes.
    const payload = this._avcc ? annexbToAvcc(annexb, true) : annexb;
    try {
        this.decoder.decode(new EncodedVideoChunk({
            type: key ? "key" : "delta",
            timestamp: this._ts++,
            data: payload,
        }));
        this._decCount = (this._decCount || 0) + 1;
        if (this._decCount <= 8 || key)
            this.gfx._log("rdpgfx: H264 decode() #" + this._decCount + " type=" + (key ? "key" : "delta") +
                " bytes=" + payload.length + " state=" + this.decoder.state + " queue=" + this.decoder.decodeQueueSize);
    } catch (e) {
        // decode() throwing means the decoder is in a bad/closed state. Reset so the next keyframe
        // rebuilds it (and ask the gfx layer to nudge the host for a fresh keyframe).
        this.gfx._log("rdpgfx: H264 decode() threw: " + (e && e.message || e) + " — resetting decoder");
        try { if (this.decoder && this.decoder.state !== "closed") this.decoder.close(); } catch (_) {}
        this.decoder = null; this.configured = false; this._gotKey = false; this._pendingRegions = [];
        if (this.gfx.requestKeyframe) this.gfx.requestKeyframe(this.surfaceId);
    }
};

H264SurfaceDecoder.prototype._onFrame = function (frame) {
    const regions = this._pendingRegions.shift() || null;
    this.gfx.onDecodedFrame(this.surfaceId, frame, regions);
};

H264SurfaceDecoder.prototype.close = function () {
    if (this.decoder && this.decoder.state !== "closed") {
        try { this.decoder.close(); } catch (e) { /* ignore */ }
    }
    this.decoder = null;
    this._pendingRegions = [];
};

if (typeof window !== "undefined") { window.RdpGfx = RdpGfx; window.H264SurfaceDecoder = H264SurfaceDecoder; }
if (typeof module !== "undefined") module.exports = { RdpGfx, H264SurfaceDecoder };
