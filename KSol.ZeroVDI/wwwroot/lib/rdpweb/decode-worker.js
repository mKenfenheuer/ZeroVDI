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
// H.264 (WebCodecs), audio, camera, and all input handling stay on the main thread: input requires DOM
// events (only fire on the main thread) and WebCodecs/Web Audio are already async and hardware-backed,
// so they don't block rendering the way these two JS decoders do.

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

self.onmessage = function (e) {
    const msg = e.data;
    switch (msg.cmd) {
        case "progressive": decodeProgressive(msg); break;
        case "reset":
            for (const k in progCtx) delete progCtx[k];
            break;
        case "destroy-surface":
            delete progCtx[msg.surfaceId];
            break;
        default:
            log("decode-worker: unknown cmd " + msg.cmd);
    }
};
