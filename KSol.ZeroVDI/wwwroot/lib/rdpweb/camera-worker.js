// camera-worker.js — webcam frame → NV12 conversion off the main thread.
//
// client.js grabs a frame from the <video> element as an ImageBitmap already scaled to the size the host
// asked for (createImageBitmap does that on the GPU) and transfers it here. We read the pixels back and
// convert them to NV12 (the raw format advertised to the host, so it needs no decoder), then transfer the
// result back. The per-pixel loop used to run on the main thread at up to 30 fps, competing with
// decoding and painting the remote desktop.
//
// In:  {id, bitmap, width, height}   Out: {id, nv12: Uint8Array} (buffer transferred) or {id, error}

var canvas = null, ctx = null;

onmessage = function (e) {
    var m = e.data;
    try {
        if (!canvas || canvas.width !== m.width || canvas.height !== m.height) {
            canvas = new OffscreenCanvas(m.width, m.height);
            ctx = canvas.getContext("2d", { willReadFrequently: true });
        }
        ctx.drawImage(m.bitmap, 0, 0, m.width, m.height);
        m.bitmap.close();
        var img = ctx.getImageData(0, 0, m.width, m.height);
        var nv12 = rgbaToNv12(img.data, m.width, m.height);
        postMessage({ id: m.id, nv12: nv12 }, [nv12.buffer]);
    } catch (err) {
        try { m.bitmap.close(); } catch (e2) { /* ignore */ }
        postMessage({ id: m.id, error: String(err) });
    }
};

// RGBA → NV12 (4:2:0): a full-resolution Y plane followed by an interleaved UV plane (one U and one V per
// 2x2 block). BT.601 limited range, as webcams/RDP expect. Mirrors Client.prototype._rgbaToNv12.
function rgbaToNv12(rgba, w, h) {
    var ySize = w * h;
    var out = new Uint8Array(ySize + (w * h) / 2);
    for (var p = 0, q = 0; q < ySize; p += 4, q++) {
        out[q] = (((66 * rgba[p] + 129 * rgba[p + 1] + 25 * rgba[p + 2] + 128) >> 8) + 16) & 0xff;
    }
    var uv = ySize;
    for (var j = 0; j < h; j += 2) {
        for (var i = 0; i < w; i += 2) {
            var idx = (j * w + i) * 4, idxR = idx + 4, idxD = idx + w * 4, idxDR = idxD + 4;
            var r = (rgba[idx] + rgba[idxR] + rgba[idxD] + rgba[idxDR]) >> 2;
            var g = (rgba[idx + 1] + rgba[idxR + 1] + rgba[idxD + 1] + rgba[idxDR + 1]) >> 2;
            var b = (rgba[idx + 2] + rgba[idxR + 2] + rgba[idxD + 2] + rgba[idxDR + 2]) >> 2;
            out[uv++] = (((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128) & 0xff;
            out[uv++] = (((112 * r - 94 * g - 18 * b + 128) >> 8) + 128) & 0xff;
        }
    }
    return out;
}
