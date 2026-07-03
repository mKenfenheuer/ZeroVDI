function NewPointerUpdate() {
    this.xorBpp = 0;
    this.cacheIndex = 0;
    this.x = 0;
    this.y = 0;
    this.width = 0;
    this.height = 0;
    this.lengthAndMask = 0;
    this.lengthXorMask = 0;
    this.xorMaskData = 0;
    this.andMaskData = 0;
}

// In an RDP pointer PDU both masks are stored bottom-up, and every scan line is
// padded to a 2-byte (WORD) boundary. See [MS-RDPBCGR] 2.2.9.1.1.4.4 / .5.
function pointerScanlineStride(width, bpp) {
    return (((width * bpp + 15) >> 4) << 1);
}

NewPointerUpdate.prototype.getImageData = function (pointerCtx) {
    if (!this.width || !this.height) {
        return null;
    }

    if (this.xorBpp === 1) {
        return this.getImageData1Bpp(pointerCtx);
    }

    const imageData = pointerCtx.createImageData(this.width, this.height);

    const andStep = pointerScanlineStride(this.width, 1);
    const xorBytesPerPixel = this.xorBpp >> 3;
    const xorStep = pointerScanlineStride(this.width, this.xorBpp);

    if (xorStep * this.height > this.lengthXorMask) {
        return null;
    }

    if (this.andMaskData && andStep * this.height > this.lengthAndMask) {
        return null;
    }

    for (let y = 0; y < this.height; y++) {
        // Masks are stored bottom-up, one WORD-padded scan line per row.
        let andByte = andStep * (this.height - y - 1);
        let xorOffset = xorStep * (this.height - y - 1);
        let andBit = 0x80;

        for (let x = 0; x < this.width; x++) {
            let andPixel = 0;

            if (this.andMaskData)
            {
                andPixel = (this.andMaskData[andByte] & andBit) ? 1 : 0;

                if (!(andBit >>= 1))
                {
                    andBit = 0x80;
                    andByte++;
                }
            }

            let xorPixel = this.getPixel(xorOffset + xorBytesPerPixel * x, andPixel);

            // 32-bpp pointers carry their own per-pixel alpha in the XOR mask (this is
            // how the modern Windows text I-beam gets its soft drop shadow), so that
            // alpha must be preserved untouched. 24-bpp pointers have no source alpha and
            // instead use the AND mask for transparency/inversion, exactly like FreeRDP.
            // getPixel packs with `<<`, which yields a SIGNED 32-bit int in JS, so the
            // white sentinel is -1, not 0xFFFFFFFF. Normalise to unsigned before comparing.
            if (this.xorBpp !== 32 && andPixel)
            {
                const u = xorPixel >>> 0;
                if (u === 0x000000FF) /* black -> transparent */
                    xorPixel = 0x00000000;
                else if (u === 0xFFFFFFFF) /* white -> inverted */
                    xorPixel = invertedPointerColor(x, y);
            }

            putPixelToImageData(imageData, (y * this.width + x), xorPixel);
        }
    }

    return imageData;
};

// "Inverted" pointer pixels are meant to be XOR-combined with the screen behind them
// (this is how mstsc draws the text I-beam so it stays visible on any background). A CSS
// `cursor` image cannot XOR against the page, and FreeRDP's checkerboard fallback renders
// as a faint white smear that is nearly invisible on the light backgrounds where I-beams
// almost always appear. Solid opaque black is the most legible static approximation.
function invertedPointerColor(x, y) {
    return 0x000000FF; /* opaque black, packed [B][G][R][A] */
}

// Returns a pixel packed as [B:24][G:16][R:8][A:0] to match putPixelToImageData.
// The XOR mask stores pixels little-endian, so 32-bpp source bytes are B,G,R,A and
// 24-bpp source bytes are B,G,R (no alpha of their own).
//
// Mirrors FreeRDP freerdp_image_copy_from_pointer_data_xbpp():
//  - 32-bpp: alpha comes straight from the source pixel.
//  - 24-bpp: opaque, unless the AND mask marks the pixel and it is pure white
//    (0xFFFFFF), in which case it stays opaque white for the black/white/invert
//    post-processing; every other AND-masked pixel becomes transparent.
NewPointerUpdate.prototype.getPixel = function(i, andPixel) {
    const src = this.xorMaskData;

    if (this.xorBpp === 32) {
        return (src[i + 0] << 24) | (src[i + 1] << 16) | (src[i + 2] << 8) | src[i + 3];
    }

    let alpha = 0xFF;

    if (andPixel) {
        const isWhite = src[i + 0] === 0xFF && src[i + 1] === 0xFF && src[i + 2] === 0xFF;
        alpha = isWhite ? 0xFF : 0x00;
    }

    return (src[i + 0] << 24) | (src[i + 1] << 16) | (src[i + 2] << 8) | alpha;
};

NewPointerUpdate.prototype.getImageData1Bpp = function (pointerCtx) {
    if (!this.width || !this.height) {
        return null;
    }

    if (this.xorBpp !== 1) {
        return null;
    }

    const imageData = pointerCtx.createImageData(this.width, this.height);
    const andStep = pointerScanlineStride(this.width, 1);
    const xorStep = pointerScanlineStride(this.width, 1);

    if (xorStep * this.height > this.lengthXorMask) {
        return null;
    }

    if (andStep * this.height > this.lengthAndMask) {
        return null;
    }

    for (let y = 0; y < this.height; y++) {
        // 1-bpp masks are stored top-down (unlike the color masks), one WORD-padded scan
        // line per row. Mirrors FreeRDP's vFlip == false path for xorBpp == 1.
        let xorIndex = xorStep * y;
        let andIndex = andStep * y;
        let xorBits = this.xorMaskData[xorIndex];
        let andBits = this.andMaskData[andIndex];
        let bit = 0x80;

        for (let x = 0; x < this.width; x++) {
            let xorPixel = (xorBits & bit) ? 1 : 0;
            let andPixel = (andBits & bit) ? 1 : 0;
            bit >>= 1;

            if (!bit) {
                xorIndex++;
                andIndex++;
                bit = 0x80;
                xorBits = this.xorMaskData[xorIndex];
                andBits = this.andMaskData[andIndex];
            }

            let color = 0;

            if (!andPixel && !xorPixel)
                color = 0xFF000000; /* black */
            else if (!andPixel && xorPixel)
                color = 0xFFFFFFFF; /* white */
            else if (andPixel && !xorPixel)
                color = 0x00000000 /* transparent */
            else if (andPixel && xorPixel)
                color = invertedPointerColor(x, y); /* inverted */

            putPixelToImageData(imageData, (y * this.width + x), color);
        }
    }

    return imageData;
};

function putPixelToImageData(imageData, i, pixel) {
    const b = (pixel >> 24) & 0xFF;
    const g = (pixel >> 16) & 0xFF;
    const r = (pixel >> 8) & 0xFF;
    const alpha = pixel & 0xFF;

    i *= 4;
    imageData = imageData.data;

    imageData[i] = r;
    imageData[i + 1] = g;
    imageData[i + 2] = b;
    imageData[i + 3] = alpha;
}

function parseNewPointerUpdate(r) {
    const u = new NewPointerUpdate();

    u.xorBpp = r.uint16(true);
    u.cacheIndex = r.uint16(true);
    u.x = r.uint16(true);
    u.y = r.uint16(true);
    u.width = r.uint16(true);
    u.height = r.uint16(true);
    u.lengthAndMask = r.uint16(true);
    u.lengthXorMask = r.uint16(true);

    if (u.lengthXorMask > 0) {
        u.xorMaskData = new Uint8ClampedArray(r.blob(u.lengthXorMask));
    }

    if (u.lengthAndMask > 0) {
        u.andMaskData = new Uint8ClampedArray(r.blob(u.lengthAndMask));
    }

    return u;
}

// PTR_COLOR (TS_COLORPOINTERATTRIBUTE) is identical to PTR_NEW (TS_POINTERATTRIBUTE)
// except it has no leading xorBpp field — the XOR mask is implicitly 24-bpp. Hosts send
// the same cursor as either type, so both must populate the pointer cache; dropping
// PTR_COLOR left cache slots empty and later PTR_CACHED references to them fell back to
// the OS default cursor. See [MS-RDPBCGR] 2.2.9.1.1.4.4.
function parseColorPointerUpdate(r) {
    const u = new NewPointerUpdate();

    u.xorBpp = 24;
    u.cacheIndex = r.uint16(true);
    u.x = r.uint16(true);
    u.y = r.uint16(true);
    u.width = r.uint16(true);
    u.height = r.uint16(true);
    u.lengthAndMask = r.uint16(true);
    u.lengthXorMask = r.uint16(true);

    if (u.lengthXorMask > 0) {
        u.xorMaskData = new Uint8ClampedArray(r.blob(u.lengthXorMask));
    }

    if (u.lengthAndMask > 0) {
        u.andMaskData = new Uint8ClampedArray(r.blob(u.lengthAndMask));
    }

    return u;
}
