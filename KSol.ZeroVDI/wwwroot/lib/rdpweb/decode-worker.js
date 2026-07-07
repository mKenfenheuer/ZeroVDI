// Dedicated Worker for the two JS (non-hardware) GFX codecs: RemoteFX Progressive (progressive.js)
// and ClearCodec (clear.js). Both are synchronous, CPU-bound, bit-exact ports of FreeRDP with no DOM
// dependency in their decode math — only the caller (rdpgfx.js) touches canvas/putImageData. Running
// them here means a large progressive frame (hundreds of tiles) or a big ClearCodec tile no longer
// blocks input/render on the main thread.
//
// Message ordering: the main thread posts one message per WIRE_TO_SURFACE_1/2 PDU, in PDU order, and
// this worker replies in the same order it receives them (no internal parallelism/reordering), so the
// main thread can paint replies as they arrive without extra sequencing — GFX PDUs for progressive and
// ClearCodec are never interleaved out of order by the host for the same surface, and painting via
// putImageData is inherently ordered by when we call it.
//
// H.264 (WebCodecs), audio, camera, and all input handling stay on the main thread: input requires DOM
// events (only fire on the main thread) and WebCodecs/Web Audio are already async and hardware-backed,
// so they don't block rendering the way these two JS decoders do.

// clear.js reads its PDU body through ByteReader, normally defined in protocol.js — but protocol.js is
// the whole (huge) RDP PDU stack with plenty of window/DOM-adjacent state we don't want or need in a
// Worker. ByteReader itself is a tiny, stable little-endian byte cursor with no external deps, so we
// keep a copy here rather than importScripts() all of protocol.js. Keep this in sync with the
// definition in protocol.js if it ever changes.
function ByteReader(u8) { this.b = u8; this.o = 0; }
ByteReader.prototype.remaining = function () { return this.b.length - this.o; };
ByteReader.prototype.u8 = function () { return this.b[this.o++]; };
ByteReader.prototype.u16le = function () { const v = this.b[this.o] | (this.b[this.o + 1] << 8); this.o += 2; return v; };
ByteReader.prototype.u16be = function () { const v = (this.b[this.o] << 8) | this.b[this.o + 1]; this.o += 2; return v; };
ByteReader.prototype.u32le = function () {
    const v = (this.b[this.o] | (this.b[this.o + 1] << 8) | (this.b[this.o + 2] << 16) | (this.b[this.o + 3] << 24)) >>> 0;
    this.o += 4; return v;
};
ByteReader.prototype.bytes = function (n) { const s = this.b.subarray(this.o, this.o + n); this.o += n; return s; };
ByteReader.prototype.skip = function (n) { this.o += n; };

importScripts("clear.js", "progressive.js");

const clear = new ClearDecode();
const progCtx = {}; // "surfaceId:codecContextId" -> RfxProgressive.Context

function log(m) { postMessage({ cmd: "log", message: m }); }

function decodeProgressive(msg) {
    const { reqId, surfaceId, codecContextId, surfWidth, surfHeight } = msg;
    // Keyed by surfaceId+codecContextId — see the matching comment in rdpgfx.js's
    // _decodeProgressiveSync: the host can multiplex multiple independent progressive streams onto one
    // surface, and sharing one Context between them corrupts tile coefficient state and dirty-rect
    // tracking across contexts, leaving stale/black regions where one context's paint doesn't reach.
    const key = surfaceId + ":" + codecContextId;
    let ctx = progCtx[key];
    if (!ctx) { ctx = new RfxProgressive.Context(); progCtx[key] = ctx; }

    const tiles = [];
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    let res = null, error = null;
    try {
        res = RfxProgressive.decode(ctx, new Uint8Array(msg.bitmapData), function (xIdx, yIdx, rgba) {
            const x = xIdx * 64, y = yIdx * 64;
            if (x < 0 || y < 0 || x >= surfWidth || y >= surfHeight) return;
            const w = Math.min(64, surfWidth - x);
            const h = Math.min(64, surfHeight - y);
            if (w <= 0 || h <= 0) return;
            let copy;
            if (w === 64 && h === 64) {
                copy = rgba.slice(0);
            } else {
                copy = new Uint8ClampedArray(w * h * 4);
                for (let row = 0; row < h; row++) {
                    const src = row * 64 * 4;
                    copy.set(rgba.subarray(src, src + w * 4), row * w * 4);
                }
            }
            tiles.push({ x, y, w, h, rgba: copy });
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x + w > maxX) maxX = x + w;
            if (y + h > maxY) maxY = y + h;
        }, log, msg.verbose);
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
    const bw = maxX - minX, bh = maxY - minY;
    const frame = new Uint8ClampedArray(bw * bh * 4);
    const colTiles = Math.ceil(bw / 64), rowTiles = Math.ceil(bh / 64);
    const written = new Uint8Array(colTiles * rowTiles);
    for (const t of tiles) {
        const ox = t.x - minX, oy = t.y - minY;
        written[(oy / 64 | 0) * colTiles + (ox / 64 | 0)] = 1;
        for (let row = 0; row < t.h; row++) {
            const src = row * t.w * 4;
            const dst = ((oy + row) * bw + ox) * 4;
            frame.set(t.rgba.subarray(src, src + t.w * 4), dst);
        }
    }
    let holes = false;
    for (let i = 0; i < written.length; i++) if (!written[i]) { holes = true; break; }
    postMessage(
        holes
            ? { cmd: "progressive-result", reqId, surfaceId, ok: true, sparse: true,
                tiles: tiles.map(function (t) { return { x: t.x, y: t.y, w: t.w, h: t.h, buffer: t.rgba.buffer }; }) }
            : { cmd: "progressive-result", reqId, surfaceId, ok: true, minX, minY, bw, bh, buffer: frame.buffer },
        holes ? tiles.map(function (t) { return t.rgba.buffer; }) : [frame.buffer]
    );
}

function decodeClear(msg) {
    const { reqId, surfaceId, rect } = msg;
    const w = rect.right - rect.left, h = rect.bottom - rect.top;
    if (w <= 0 || h <= 0) {
        postMessage({ cmd: "clear-result", reqId, surfaceId, ok: false, error: "empty rect" });
        return;
    }
    let res = null, error = null;
    try {
        res = clear.decode(new Uint8Array(msg.data), w, h, log, msg.verbose);
    } catch (e) {
        error = "ClearCodec EXCEPTION: " + (e && e.stack ? e.stack : e);
    }
    if (!res) {
        postMessage({ cmd: "clear-result", reqId, surfaceId, ok: false,
            error: error || ("ClearCodec decode failed (" + w + "x" + h + ", " + msg.data.byteLength + " bytes)") });
        return;
    }
    const buf = res.rgba.buffer;
    postMessage({ cmd: "clear-result", reqId, surfaceId, ok: true, rect, buffer: buf }, [buf]);
}

self.onmessage = function (e) {
    const msg = e.data;
    switch (msg.cmd) {
        case "progressive": decodeProgressive(msg); break;
        case "clear": decodeClear(msg); break;
        case "reset":
            for (const k in progCtx) delete progCtx[k];
            clear.reset();
            break;
        case "destroy-surface": {
            const prefix = msg.surfaceId + ":";
            for (const k in progCtx) if (k.indexOf(prefix) === 0) delete progCtx[k];
            break;
        }
        default:
            log("decode-worker: unknown cmd " + msg.cmd);
    }
};
