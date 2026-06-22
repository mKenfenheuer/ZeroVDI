// ZGFX (RDP8) bulk data decompression — used by MS-RDPEGFX. Every payload the host sends on the
// Graphics dynamic channel is wrapped in this scheme (a ZGFX descriptor, then one or more segments;
// each segment is either raw or LZ77+Huffman compressed against a 2.5 MB history ring buffer).
//
// Ported faithfully from FreeRDP 2.x / 3.x `libfreerdp/codec/zgfx.c` (Apache-2.0). A single ZgfxDecode
// instance MUST be reused across the whole session: the history ring buffer carries matches across
// PDUs, so a fresh context mid-stream would corrupt every subsequent decode.

// Descriptor byte ([MS-RDPEGFX] 2.2.5.2 / FreeRDP zgfx.h).
const ZGFX_SEGMENTED_SINGLE = 0xe0;    // one segment follows
const ZGFX_SEGMENTED_MULTIPART = 0xe1; // segmentCount(2) + uncompressedSize(4), then sized segments
// Per-segment header flag.
const ZGFX_PACKET_COMPRESSED = 0x20;   // PACKET_COMPRESSED — segment body is LZ77+Huffman, else raw

const ZGFX_HISTORY_SIZE = 2500000;     // 2.5 MB ring (FreeRDP HistoryBuffer)
const ZGFX_OUTPUT_MAX = 65536;         // max uncompressed bytes per segment

// Prefix-coded token table ([MS-RDPEGFX] 2.2.5.3.1; FreeRDP ZGFX_TOKEN_TABLE). Each entry:
//   prefixLength, prefixCode  — the variable-length prefix that selects this token
//   valueBits                 — extra bits read after the prefix
//   tokenType                 — 0 = literal (value = base + extra), 1 = match (distance = base + extra)
//   valueBase                 — base added to the extra bits
const ZGFX_TOKEN_TABLE = [
    [1, 0, 8, 0, 0],
    [5, 17, 5, 1, 0],
    [5, 18, 7, 1, 32],
    [5, 19, 9, 1, 160],
    [5, 20, 10, 1, 672],
    [5, 21, 12, 1, 1696],
    [5, 24, 0, 0, 0x00],
    [5, 25, 0, 0, 0x01],
    [6, 44, 14, 1, 5792],
    [6, 45, 15, 1, 22176],
    [6, 52, 0, 0, 0x02],
    [6, 53, 0, 0, 0x03],
    [6, 54, 0, 0, 0xff],
    [7, 92, 18, 1, 54944],
    [7, 93, 20, 1, 317088],
    [7, 110, 0, 0, 0x04],
    [7, 111, 0, 0, 0x05],
    [7, 112, 0, 0, 0x06],
    [7, 113, 0, 0, 0x07],
    [7, 114, 0, 0, 0x08],
    [7, 115, 0, 0, 0x09],
    [7, 116, 0, 0, 0x0a],
    [7, 117, 0, 0, 0x0b],
    [7, 118, 0, 0, 0x3a],
    [7, 119, 0, 0, 0x3b],
    [7, 120, 0, 0, 0x3c],
    [7, 121, 0, 0, 0x3d],
    [7, 122, 0, 0, 0x3e],
    [7, 123, 0, 0, 0x3f],
    [7, 124, 0, 0, 0x40],
    [7, 125, 0, 0, 0x80],
    [8, 188, 20, 1, 1365664],
    [8, 189, 21, 1, 2414240],
    [8, 252, 0, 0, 0x0c],
    [8, 253, 0, 0, 0x38],
    [8, 254, 0, 0, 0x39],
    [8, 255, 0, 0, 0x66],
    [9, 380, 22, 1, 4511392],
    [9, 381, 23, 1, 8705696],
    [9, 382, 24, 1, 17094304],
];

function ZgfxDecode() {
    this.history = new Uint8Array(ZGFX_HISTORY_SIZE);
    this.historyIndex = 0;
    // Bit reader state (FreeRDP: bits / cBitsRemaining / BitsCurrent / cBitsCurrent).
    this._in = null;        // current segment bytes
    this._pos = 0;          // read cursor in _in
    this._end = 0;          // last decodable byte index (exclusive of the trailing pad byte)
    this.bits = 0;
    this.cBitsRemaining = 0;
    this.bitsCurrent = 0;
    this.cBitsCurrent = 0;
    // Per-segment output scratch.
    this.output = new Uint8Array(ZGFX_OUTPUT_MAX);
    this.outputCount = 0;
}

// Reset the history ring (e.g. on reconnect). FreeRDP zgfx_context_reset only clears the index.
ZgfxDecode.prototype.reset = function () { this.historyIndex = 0; };

// Pull `nbits` MSB-first from the current segment into this.bits (FreeRDP zgfx_GetBits).
ZgfxDecode.prototype._getBits = function (nbits) {
    while (this.cBitsCurrent < nbits) {
        this.bitsCurrent <<= 8;
        if (this._pos < this._end) this.bitsCurrent += this._in[this._pos++];
        this.cBitsCurrent += 8;
    }
    this.cBitsRemaining -= nbits;
    this.cBitsCurrent -= nbits;
    this.bits = this.bitsCurrent >>> this.cBitsCurrent;
    this.bitsCurrent &= (1 << this.cBitsCurrent) - 1;
};

// Append `count` literal/match bytes into the history ring (FreeRDP zgfx_history_buffer_ring_write).
ZgfxDecode.prototype._historyWrite = function (src, srcOff, count) {
    if (count <= 0) return;
    if (count > ZGFX_HISTORY_SIZE) {
        const residue = count - ZGFX_HISTORY_SIZE;
        srcOff += residue;
        count = ZGFX_HISTORY_SIZE;
        this.historyIndex = (this.historyIndex + residue) % ZGFX_HISTORY_SIZE;
    }
    if (this.historyIndex + count <= ZGFX_HISTORY_SIZE) {
        this.history.set(src.subarray(srcOff, srcOff + count), this.historyIndex);
        this.historyIndex += count;
        if (this.historyIndex === ZGFX_HISTORY_SIZE) this.historyIndex = 0;
    } else {
        const front = ZGFX_HISTORY_SIZE - this.historyIndex;
        this.history.set(src.subarray(srcOff, srcOff + front), this.historyIndex);
        this.history.set(src.subarray(srcOff + front, srcOff + count), 0);
        this.historyIndex = count - front;
    }
};

// Copy a back-reference of `count` bytes at `offset` from the history ring into `dst` at `dstOff`.
// Mirrors FreeRDP zgfx_history_buffer_ring_read, including its self-overlapping doubling fill (the
// match can be longer than the distance — classic LZ77 run extension).
ZgfxDecode.prototype._historyRead = function (offset, dst, dstOff, count) {
    if (count <= 0) return;
    let bytesLeft = count;
    let index = (this.historyIndex + ZGFX_HISTORY_SIZE - offset) % ZGFX_HISTORY_SIZE;
    let bytes = Math.min(bytesLeft, offset);
    const origDstOff = dstOff;

    if (index + bytes <= ZGFX_HISTORY_SIZE) {
        dst.set(this.history.subarray(index, index + bytes), dstOff);
    } else {
        const front = ZGFX_HISTORY_SIZE - index;
        dst.set(this.history.subarray(index, index + front), dstOff);
        dst.set(this.history.subarray(0, bytes - front), dstOff + front);
    }

    bytesLeft -= bytes;
    if (bytesLeft === 0) return;

    let dptr = dstOff + bytes;
    let valid = bytes;
    do {
        bytes = valid;
        if (bytes > bytesLeft) bytes = bytesLeft;
        dst.copyWithin(dptr, origDstOff, origDstOff + bytes);
        dptr += bytes;
        valid <<= 1;
        bytesLeft -= bytes;
    } while (bytesLeft > 0);
};

// Decode one segment into this.output (FreeRDP zgfx_decompress_segment). `seg` is the segment bytes
// INCLUDING the leading 1-byte flags header; returns false on malformed input.
ZgfxDecode.prototype._decodeSegment = function (seg) {
    if (seg.length < 2) return false;
    const flags = seg[0];
    const body = seg.subarray(1);          // cbSegment bytes
    const cbSegment = body.length;
    this.outputCount = 0;

    if (!(flags & ZGFX_PACKET_COMPRESSED)) {
        // Raw segment: copy straight through, also feeding the history ring.
        if (cbSegment > ZGFX_OUTPUT_MAX) return false;
        this._historyWrite(body, 0, cbSegment);
        this.output.set(body, 0);
        this.outputCount = cbSegment;
        return true;
    }

    // Compressed: the last body byte holds the count of pad bits in the final data byte.
    this._in = body;
    this._pos = 0;
    this._end = cbSegment - 1;             // pbInputEnd = &body[cbSegment-1]
    const totalBits = 8 * (cbSegment - 1);
    const lastByte = body[cbSegment - 1];
    if (totalBits < lastByte) return false;
    this.cBitsRemaining = totalBits - lastByte;
    this.cBitsCurrent = 0;
    this.bitsCurrent = 0;

    while (this.cBitsRemaining > 0) {
        let haveBits = 0;
        let inPrefix = 0;
        let matched = false;

        for (let t = 0; t < ZGFX_TOKEN_TABLE.length; t++) {
            const tok = ZGFX_TOKEN_TABLE[t];
            const prefixLength = tok[0], prefixCode = tok[1], valueBits = tok[2],
                tokenType = tok[3], valueBase = tok[4];
            while (haveBits < prefixLength) {
                this._getBits(1);
                inPrefix = (inPrefix << 1) + this.bits;
                haveBits++;
            }
            if (inPrefix !== prefixCode) continue;
            matched = true;

            if (tokenType === 0) {
                // Literal.
                this._getBits(valueBits);
                const c = (valueBase + this.bits) & 0xff;
                this.history[this.historyIndex] = c;
                if (++this.historyIndex === ZGFX_HISTORY_SIZE) this.historyIndex = 0;
                if (this.outputCount >= ZGFX_OUTPUT_MAX) return false;
                this.output[this.outputCount++] = c;
            } else {
                this._getBits(valueBits);
                const distance = valueBase + this.bits;
                if (distance !== 0) {
                    // Match: read the run length (3, or 4..n via the unary-prefixed extra bits).
                    this._getBits(1);
                    let count, extra;
                    if (this.bits === 0) {
                        count = 3;
                    } else {
                        count = 4;
                        extra = 2;
                        this._getBits(1);
                        while (this.bits === 1) { count *= 2; extra++; this._getBits(1); }
                        this._getBits(extra);
                        count += this.bits;
                    }
                    if (count > ZGFX_OUTPUT_MAX - this.outputCount) return false;
                    this._historyRead(distance, this.output, this.outputCount, count);
                    this._historyWrite(this.output, this.outputCount, count);
                    this.outputCount += count;
                } else {
                    // Unencoded run: 15-bit length then raw bytes copied verbatim.
                    this._getBits(15);
                    const count = this.bits;
                    this.cBitsRemaining -= this.cBitsCurrent;
                    this.cBitsCurrent = 0;
                    this.bitsCurrent = 0;
                    if (count > ZGFX_OUTPUT_MAX - this.outputCount) return false;
                    if (count > (this.cBitsRemaining >> 3)) return false;
                    if (this._pos + count > this._end) return false;
                    this.output.set(this._in.subarray(this._pos, this._pos + count), this.outputCount);
                    this._historyWrite(this._in, this._pos, count);
                    this._pos += count;
                    this.cBitsRemaining -= 8 * count;
                    this.outputCount += count;
                }
            }
            break;
        }
        if (!matched) return false;        // no token matched the prefix — corrupt stream
    }
    return true;
};

// Decompress a complete ZGFX message (FreeRDP zgfx_decompress). Returns a Uint8Array of the
// concatenated, uncompressed payload, or null on error.
ZgfxDecode.prototype.decompress = function (data) {
    if (!data || data.length < 1) return null;
    const descriptor = data[0];

    if (descriptor === ZGFX_SEGMENTED_SINGLE) {
        if (!this._decodeSegment(data.subarray(1))) return null;
        return this.output.slice(0, this.outputCount);
    }

    if (descriptor === ZGFX_SEGMENTED_MULTIPART) {
        if (data.length < 7) return null;
        const r = new ByteReader(data);
        r.skip(1);                          // descriptor
        const segmentCount = r.u16le();
        const uncompressedSize = r.u32le();
        const out = new Uint8Array(uncompressedSize);
        let used = 0;
        for (let i = 0; i < segmentCount; i++) {
            if (r.remaining() < 4) return null;
            const segmentSize = r.u32le();
            if (r.remaining() < segmentSize) return null;
            const seg = r.bytes(segmentSize);
            if (!this._decodeSegment(seg)) return null;
            if (used + this.outputCount > uncompressedSize) return null;
            out.set(this.output.subarray(0, this.outputCount), used);
            used += this.outputCount;
        }
        if (used !== uncompressedSize) return null;
        return out;
    }

    return null;
};

if (typeof window !== "undefined") window.ZgfxDecode = ZgfxDecode;
if (typeof module !== "undefined") module.exports = { ZgfxDecode };
