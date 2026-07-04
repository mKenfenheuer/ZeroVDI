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
    this.clear = (typeof ClearDecode !== "undefined") ? new ClearDecode() : null; // shared ClearCodec ctx
    // RemoteFX Progressive (GNOME Remote Desktop streams this over WIRE_TO_SURFACE_2). Per-surface
    // context (holds the persistent per-tile coefficient grids needed for RFX_TILE_DIFFERENCE).
    this.progressive = (typeof RfxProgressive !== "undefined") ? RfxProgressive : null;
    this.progCtx = {};           // surfaceId -> RfxProgressive.Context
    this.framesDecoded = 0;
    this._dirty = [];            // output rects touched in the current frame, flushed at END_FRAME
    this.outputWidth = 0;
    this.outputHeight = 0;
}

RdpGfx.prototype._log = function (m) { if (this.cb.onLog) this.cb.onLog(m); };

// Reset session state (reconnect). Keep no surfaces/decoders across sessions.
RdpGfx.prototype.reset = function () {
    this.zgfx.reset();
    for (const id in this.decoders) this.decoders[id].close();
    this.surfaces = {}; this.outputMap = {}; this.decoders = {}; this.cache = {};
    this.progCtx = {};
    this._dirty = []; this.confirmedVersion = 0; this.framesDecoded = 0;
    this._qoeT0 = null;
    if (this.clear) this.clear.reset();
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
        this._dispatch(cmdId, body);
        off += pduLength;
    }
};

RdpGfx.prototype._dispatch = function (cmdId, body) {
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

RdpGfx.prototype._onCreateSurface = function (r) {
    const surfaceId = r.u16le();
    const width = r.u16le();
    const height = r.u16le();
    const pixelFormat = r.u8();
    // Reuse of a surfaceId implies the old one is gone — drop it first (FreeRDP does the same).
    this._destroySurface(surfaceId);
    const canvas = (typeof OffscreenCanvas !== "undefined")
        ? new OffscreenCanvas(width, height)
        : Object.assign(document.createElement("canvas"), { width: width, height: height });
    const ctx = canvas.getContext("2d");
    // `touched` flips true on the first content write (_afterSurfaceUpdate). Untouched surfaces are
    // never blitted to the output (see _paintSurface) — painting a brand-new empty surface would wipe
    // the last good frame during a host-side reconfigure (GNOME RD deletes+recreates its surface and
    // re-maps it on every DISPLAYCONTROL_MONITOR_LAYOUT, then streams nothing until damage occurs).
    this.surfaces[surfaceId] = { width, height, canvas, ctx, pixelFormat, touched: false };
    this._log("rdpgfx: CREATE_SURFACE id=" + surfaceId + " " + width + "x" + height +
        " fmt=0x" + pixelFormat.toString(16));
};

RdpGfx.prototype._destroySurface = function (surfaceId) {
    if (this.decoders[surfaceId]) { this.decoders[surfaceId].close(); delete this.decoders[surfaceId]; }
    delete this.surfaces[surfaceId];
    delete this.outputMap[surfaceId];
    // Drop the progressive per-tile cache too — a recreated surfaceId is a brand-new surface; keeping the
    // old cache would mis-accumulate (and leak). The next frame must repaint from scratch.
    delete this.progCtx[surfaceId];
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
    const surf = this.surfaces[surfaceId];
    if (!surf) { this._log("rdpgfx: WIRE_TO_SURFACE_1 for unknown surface " + surfaceId); return; }

    const rect = { left: destLeft, top: destTop, right: destRight, bottom: destBottom };
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
// pixelFormat(1), then the codec bitstream to end of PDU (no explicit length). Used by the
// context/stream codecs — on Windows that's RemoteFX Progressive (CAPROGRESSIVE 0x0009). The host
// switches to this when it decides to stream the desktop progressively instead of as ClearCodec tiles.
RdpGfx.prototype._onWireToSurface2 = function (r) {
    const surfaceId = r.u16le();
    const codecId = r.u16le();
    const codecContextId = r.u32le();
    const pixelFormat = r.u8();
    const bitmapData = r.bytes(r.remaining());
    const surf = this.surfaces[surfaceId];
    if (!surf) { this._log("rdpgfx: WIRE_TO_SURFACE_2 for unknown surface " + surfaceId); return; }

    if (codecId === RDPGFX_CODECID_CAPROGRESSIVE || codecId === RDPGFX_CODECID_CAPROGRESSIVE_V2) {
        if (!this.progressive) { this._log("rdpgfx: Progressive module not loaded"); return; }
        this._decodeProgressive(surfaceId, surf, bitmapData);
    } else {
        this._log("rdpgfx: WIRE_TO_SURFACE_2 unsupported codecId 0x" + codecId.toString(16) +
            " ctx=" + codecContextId + " (" + bitmapData.length + " bytes) — skipped");
    }
};

// RemoteFX Progressive (CAPROGRESSIVE): decode the WIRE_TO_SURFACE_2 payload into 64x64 tiles and
// putImageData each onto the surface at (xIdx*64, yIdx*64). GNOME Remote Desktop streams the whole
// desktop this way. Each tile reconstruction (RLGR → dequant → inverse DWT → YCbCr→RGB) lives in
// progressive.js; here we just place tiles and mark them dirty so they paint at END_FRAME flush.
RdpGfx.prototype._decodeProgressive = function (surfaceId, surf, bitmapData) {
    let ctx = this.progCtx[surfaceId];
    if (!ctx) { ctx = new this.progressive.Context(); this.progCtx[surfaceId] = ctx; }
    const self = this;
    const dirty = [];
    // NOTE: this whole body is guarded — a single bad frame must NEVER throw out of the GFX dispatch
    // loop (that would silently stop ALL further rendering and freeze the screen). On any error we log
    // and bail on just this PDU.
    try {
        const res = this.progressive.decode(ctx, bitmapData, function (xIdx, yIdx, rgba) {
            const x = xIdx * 64, y = yIdx * 64;
            // Clip the 64x64 tile to the surface bounds (edge tiles may overhang). Guard against a
            // corrupt/garbage tile index that would place the tile entirely outside the surface.
            if (x < 0 || y < 0 || x >= surf.width || y >= surf.height) return;
            const w = Math.min(64, surf.width - x);
            const h = Math.min(64, surf.height - y);
            if (w <= 0 || h <= 0) return;
            if (w === 64 && h === 64) {
                surf.ctx.putImageData(new ImageData(rgba.slice(0), 64, 64), x, y);
            } else {
                const sub = new Uint8ClampedArray(w * h * 4);
                for (let row = 0; row < h; row++) {
                    const src = row * 64 * 4;
                    sub.set(rgba.subarray(src, src + w * 4), row * w * 4);
                }
                surf.ctx.putImageData(new ImageData(sub, w, h), x, y);
            }
            dirty.push({ left: x, top: y, right: x + w, bottom: y + h });
        }, function (m) { self._log("rdpgfx: " + m); });

        if (!res) { this._log("rdpgfx: progressive decode failed (" + bitmapData.length + " bytes)"); }
    } catch (e) {
        this._log("rdpgfx: progressive EXCEPTION (" + bitmapData.length + "B): " + (e && e.stack ? e.stack : e));
    }
    if (dirty.length) { try { this._afterSurfaceUpdate(surfaceId, surf, dirty); } catch (e) { this._log("rdpgfx: progressive paint exception: " + e); } }
};

// AVC420 bitstream ([MS-RDPEGFX] 2.2.4.4 / 2.2.4.5): an RFX_AVC420_METABLOCK (region rects + quant
// quality) followed by the raw H.264 (Annex-B) bitstream. The metablock's region rects tell us which
// parts of the decoded frame changed; we draw the whole decoded frame into the surface at the rects'
// bounding origin (the frame the encoder produced covers exactly the union of the regions).
RdpGfx.prototype._decodeAvc420 = function (surfaceId, surf, destRect, data) {
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

// ClearCodec (0x8): decode the tile into an RGBA buffer and putImageData it onto the surface at the
// dest rect. ClearCodec carries the full dest-rect pixels (no separate metablock); the dirty region is
// the dest rect itself.
RdpGfx.prototype._decodeClear = function (surfaceId, surf, rect, data) {
    if (!this.clear) { this._log("rdpgfx: ClearCodec module not loaded"); return; }
    const w = rect.right - rect.left, h = rect.bottom - rect.top;
    if (w <= 0 || h <= 0) return;
    const self = this;
    const res = this.clear.decode(data, w, h, function (m) { self._log("rdpgfx: " + m); });
    if (!res) { this._log("rdpgfx: ClearCodec decode failed (" + w + "x" + h + ", " + data.length + " bytes)"); return; }
    surf.ctx.putImageData(new ImageData(res.rgba, w, h), rect.left, rect.top);
    this._afterSurfaceUpdate(surfaceId, surf, [rect]);
};

// Called by the H264 decoder when a VideoFrame is ready. The decoded frame is the FULL surface picture.
//
// IMPORTANT: do NOT use ctx.drawImage(videoFrame) — in some browsers (observed: Firefox) drawing a
// decoded H.264 VideoFrame straight to a 2D canvas yields ALL BLACK (the YUV→RGB / color-space step is
// skipped). Instead we copyTo() the frame's pixels into an RGBA buffer (the browser does the conversion)
// and putImageData them onto the surface canvas. copyTo is async, so the surface update + output paint
// happen in the promise continuation.
RdpGfx.prototype.onDecodedFrame = function (surfaceId, frame, regions) {
    const surf = this.surfaces[surfaceId];
    if (!surf) { if (frame.close) frame.close(); return; }
    const self = this;
    // copyTo() produces the frame's VISIBLE rect (coded size minus H.264 crop/16-alignment), so the
    // RGBA buffer stride is visibleRect.width, NOT codedWidth. Region rects from the metablock are in
    // visible-frame coordinates. Fall back to coded size if visibleRect is absent.
    const vr = frame.visibleRect || { x: 0, y: 0, width: frame.codedWidth, height: frame.codedHeight };
    const fw = frame.codedWidth;
    const fh = frame.codedHeight;
    const cw = Math.min(fw, surf.width), ch = Math.min(fh, surf.height);

    // DEBUG: draw the decoded VideoFrame STRAIGHT to the visible output canvas, bypassing the offscreen
    // surface + bitmap machinery, so we can see with our own eyes whether the frame has content.
    // Enable with window.RDP_GFX_DEBUG_DIRECT=1.
    if (typeof window !== "undefined" && window.RDP_GFX_DEBUG_DIRECT && this.cb.onDirectFrame) {
        this.cb.onDirectFrame(frame, surfaceId, this.outputMap[surfaceId]);
        return;
    }

    // Render the decoded frame via VideoFrame.copyTo() into an explicit RGBA CPU buffer, then
    // putImageData per region rect. WHY copyTo and not drawImage/createImageBitmap: in Firefox a
    // GPU-backed decoded H.264 VideoFrame composited to a 2D canvas yields ALL BLACK (observed here:
    // fmt=BGRX, nonBlack=0/4111360 via the createImageBitmap path). copyTo with an EXPLICIT
    // { format: "RGBA" } forces the browser to read the frame back to CPU and do the BGRX/YUV→RGBA
    // conversion into a buffer we own — the reliable path. (An earlier copyTo attempt got "collapsed"
    // pixels because it copied in the frame's NATIVE layout and misread it as RGBA; specifying the
    // output format is the fix.)
    //
    // CRITICAL ([MS-RDPEGFX] AVC420 / FreeRDP yuv420_context_decode): the decoder emits a FULL-surface
    // frame, but only the metablock region rects carry valid pixels for THIS update; a P-frame leaves
    // non-dirty areas black/skip. So we putImageData ONLY the region rects onto the persistent surface
    // (whole frame when there are no regions, e.g. a covering keyframe).
    const rects = (regions && regions.length)
        ? regions
        : [{ left: 0, top: 0, right: cw, bottom: ch }];
    const paintRgba = function (rgba) {
        try {
            for (const rc of rects) {
                const sl = Math.max(0, rc.left), st = Math.max(0, rc.top);
                const sr = Math.min(cw, rc.right), sb = Math.min(ch, rc.bottom);
                const w = sr - sl, h = sb - st;
                if (w <= 0 || h <= 0) continue;
                // Extract this rect's rows from the full-frame RGBA buffer (stride = fw*4).
                const sub = new Uint8ClampedArray(w * h * 4);
                for (let row = 0; row < h; row++) {
                    const srcOff = ((st + row) * fw + sl) * 4;
                    sub.set(rgba.subarray(srcOff, srcOff + w * 4), row * w * 4);
                }
                surf.ctx.putImageData(new ImageData(sub, w, h), sl, st);
            }
            self._afterSurfaceUpdate(surfaceId, surf, rects);
        } catch (e) {
            self._log("rdpgfx: H264 paint threw: " + (e && e.message || e));
        } finally {
            if (frame.close) frame.close();
        }
    };
    // copyTo into RGBA. allocationSize/copyTo with {format:"RGBA"} are widely supported; if copyTo
    // rejects (older impls), fall back to the createImageBitmap path.
    let rgba;
    try {
        rgba = new Uint8ClampedArray(fw * fh * 4);
    } catch (e) { rgba = null; }
    if (rgba && frame.copyTo) {
        frame.copyTo(rgba, { format: "RGBA" }).then(function () {
            paintRgba(rgba);
        }).catch(function (e) {
            self._log("rdpgfx: H264 copyTo(RGBA) failed: " + (e && e.message || e) + " — trying bitmap");
            self._paintViaBitmap(surfaceId, surf, frame, rects, cw, ch);
        });
        return;
    }
    self._paintViaBitmap(surfaceId, surf, frame, rects, cw, ch);
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
    const b = r.u8(), g = r.u8(), rd = r.u8(), a = r.u8(); // RDPGFX_COLOR32 B,G,R,XA
    const fillRectCount = r.u16le();
    const surf = this.surfaces[surfaceId];
    if (!surf) return;
    surf.ctx.fillStyle = "rgba(" + rd + "," + g + "," + b + "," + (a / 255) + ")";
    const updated = [];
    for (let i = 0; i < fillRectCount; i++) {
        const left = r.u16le(), top = r.u16le(), right = r.u16le(), bottom = r.u16le();
        surf.ctx.fillRect(left, top, right - left, bottom - top);
        updated.push({ left, top, right, bottom });
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
    if (!src || !dst || w <= 0 || h <= 0) return;
    const updated = [];
    for (let i = 0; i < destPtCount; i++) {
        const dx = r.u16le(), dy = r.u16le();
        dst.ctx.drawImage(src.canvas, rectSrcLeft, rectSrcTop, w, h, dx, dy, w, h);
        updated.push({ left: dx, top: dy, right: dx + w, bottom: dy + h });
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
    if (!surf || w <= 0 || h <= 0) return;
    let slot = this.cache[cacheSlot];
    if (!slot || slot.w !== w || slot.h !== h) {
        const canvas = (typeof OffscreenCanvas !== "undefined")
            ? new OffscreenCanvas(w, h)
            : Object.assign(document.createElement("canvas"), { width: w, height: h });
        slot = this.cache[cacheSlot] = { canvas, ctx: canvas.getContext("2d"), w, h };
    }
    slot.ctx.drawImage(surf.canvas, left, top, w, h, 0, 0, w, h);
};

// CACHE_TO_SURFACE ([MS-RDPEGFX] 2.2.2.7): blit a cached slot onto a surface at one or more points.
RdpGfx.prototype._onCacheToSurface = function (r) {
    const cacheSlot = r.u16le();
    const surfaceId = r.u16le();
    const destPtsCount = r.u16le();
    const surf = this.surfaces[surfaceId];
    const slot = this.cache[cacheSlot];
    const updated = [];
    for (let i = 0; i < destPtsCount; i++) {
        const dx = r.u16le(), dy = r.u16le();
        if (!surf || !slot) continue;
        surf.ctx.drawImage(slot.canvas, dx, dy);
        updated.push({ left: dx, top: dy, right: dx + slot.w, bottom: dy + slot.h });
    }
    if (surf && updated.length) this._afterSurfaceUpdate(surfaceId, surf, updated);
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
