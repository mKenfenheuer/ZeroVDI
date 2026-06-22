// MPPC (RDP4/RDP5 bulk) compression — faithful port of FreeRDP libfreerdp/codec/mppc.c +
// winpr/bitstream.h. Used so the browser client matches the working macOS Remote Desktop app's wire
// form 1:1: with INFO_COMPRESSION set, the host RDP-bulk-compresses slow-path channel data TO us (we
// must decompress), and we send our GFX CAPS_ADVERTISE bulk-compressed (CHANNEL flag COMPRESSED).
//
// Decompress: full FreeRDP port (literals + RDP4/RDP5 copy-offset/length tokens), shared 8K/64K history.
// Compress: we emit a LITERAL-ONLY stream (every input byte as a literal token). This is always a valid
// MPPC packet the host decompresses to the exact same bytes — we don't replicate the macOS app's match
// output (compression is implementation-specific and can never be byte-identical), but the host sees
// identical decompressed content with the COMPRESSED framing, which is the functional 1:1 that matters.
//
// CHANNEL_PDU_HEADER flags (MS-RDPBCGR 2.2.6.1.1) for virtual channels:
//   CHANNEL_PACKET_COMPRESSED 0x00200000, AT_FRONT 0x00400000, FLUSHED 0x00800000,
//   compression-type in the 0x000F0000 mask. Client Info infoFlags compression-type is the 0x1E00 mask.
(function () {
    "use strict";
    var PACKET_COMPRESSED = 0x20, PACKET_AT_FRONT = 0x40, PACKET_FLUSHED = 0x80;

    function Mppc(level /*0=RDP4/8K, 1=RDP5/64K*/) {
        this.level = level ? 1 : 0;
        this.size = this.level ? 65536 : 8192;
        this.history = new Uint8Array(65536);
        this.historyOffset = 0;
    }
    Mppc.prototype.reset = function () { this.historyOffset = 0; this.history.fill(0); };

    // ---- bit reader (decompress), faithful to winpr BitStream ----
    function BitStream(buf) {
        this.buffer = buf; this.capacity = buf.length;
        this.ptr = 0; this.position = 0; this.length = buf.length * 8;
        this.offset = 0; this.mask = 0; this.prefetch = 0; this.accumulator = 0;
        this._fetch();
    }
    BitStream.prototype._prefetch = function () {
        this.prefetch = 0; var d = this.ptr;
        if (d + 4 < this.capacity) this.prefetch = (this.prefetch | (this.buffer[d + 4] << 24)) >>> 0;
        if (d + 5 < this.capacity) this.prefetch = (this.prefetch | (this.buffer[d + 5] << 16)) >>> 0;
        if (d + 6 < this.capacity) this.prefetch = (this.prefetch | (this.buffer[d + 6] << 8)) >>> 0;
        if (d + 7 < this.capacity) this.prefetch = (this.prefetch | (this.buffer[d + 7] << 0)) >>> 0;
    };
    BitStream.prototype._fetch = function () {
        this.accumulator = 0; var d = this.ptr;
        if (d + 0 < this.capacity) this.accumulator = (this.accumulator | (this.buffer[d + 0] << 24)) >>> 0;
        if (d + 1 < this.capacity) this.accumulator = (this.accumulator | (this.buffer[d + 1] << 16)) >>> 0;
        if (d + 2 < this.capacity) this.accumulator = (this.accumulator | (this.buffer[d + 2] << 8)) >>> 0;
        if (d + 3 < this.capacity) this.accumulator = (this.accumulator | (this.buffer[d + 3] << 0)) >>> 0;
        this._prefetch();
    };
    BitStream.prototype.shift = function (n) {
        if (n === 0) return;
        if (n > 0 && n < 32) {
            this.accumulator = (this.accumulator << n) >>> 0;
            this.position += n; this.offset += n;
            if (this.offset < 32) {
                this.mask = ((1 << n) - 1) >>> 0;
                this.accumulator = (this.accumulator | ((this.prefetch >>> (32 - n)) & this.mask)) >>> 0;
                this.prefetch = (this.prefetch << n) >>> 0;
            } else {
                this.mask = ((1 << n) - 1) >>> 0;
                this.accumulator = (this.accumulator | ((this.prefetch >>> (32 - n)) & this.mask)) >>> 0;
                this.prefetch = (this.prefetch << n) >>> 0;
                this.offset -= 32; this.ptr += 4; this._prefetch();
                if (this.offset) {
                    this.mask = ((1 << this.offset) - 1) >>> 0;
                    this.accumulator = (this.accumulator | ((this.prefetch >>> (32 - this.offset)) & this.mask)) >>> 0;
                    this.prefetch = (this.prefetch << this.offset) >>> 0;
                }
            }
        }
    };

    // Decompress one bulk packet. `flags` uses the FreeRDP low-byte form (PACKET_COMPRESSED 0x20, etc.).
    // Returns a Uint8Array (copy) or null on error.
    Mppc.prototype.decompress = function (src, flags) {
        var bs = new BitStream(src);
        if (flags & PACKET_AT_FRONT) this.historyOffset = 0;
        if (flags & PACKET_FLUSHED) { this.historyOffset = 0; this.history.fill(0); }
        if (!(flags & PACKET_COMPRESSED)) return Uint8Array.from(src);

        var H = this.history;
        var hp = this.historyOffset;
        var startHp = hp;
        var lvl = this.level;
        var histMask = lvl ? 0xFFFF : 0x1FFF;

        while ((bs.length - bs.position) >= 8) {
            var acc = bs.accumulator >>> 0;
            if (((acc & 0x80000000) >>> 0) === 0) { H[hp++] = (acc & 0x7F000000) >>> 24; bs.shift(8); continue; }
            else if (((acc & 0xC0000000) >>> 0) === 0x80000000) { H[hp++] = (((acc & 0x3F800000) >>> 23) + 0x80) & 0xff; bs.shift(9); continue; }
            var copyOffset;
            if (lvl) {
                if (((acc & 0xF8000000) >>> 0) === 0xF8000000) { copyOffset = (acc >>> 21) & 0x3F; bs.shift(11); }
                else if (((acc & 0xF8000000) >>> 0) === 0xF0000000) { copyOffset = ((acc >>> 19) & 0xFF) + 64; bs.shift(13); }
                else if (((acc & 0xF0000000) >>> 0) === 0xE0000000) { copyOffset = ((acc >>> 17) & 0x7FF) + 320; bs.shift(15); }
                else if (((acc & 0xE0000000) >>> 0) === 0xC0000000) { copyOffset = ((acc >>> 13) & 0xFFFF) + 2368; bs.shift(19); }
                else return null;
            } else {
                if (((acc & 0xF0000000) >>> 0) === 0xF0000000) { copyOffset = (acc >>> 22) & 0x3F; bs.shift(10); }
                else if (((acc & 0xF0000000) >>> 0) === 0xE0000000) { copyOffset = ((acc >>> 20) & 0xFF) + 64; bs.shift(12); }
                else if (((acc & 0xE0000000) >>> 0) === 0xC0000000) { copyOffset = ((acc >>> 16) & 0x1FFF) + 320; bs.shift(16); }
                else return null;
            }
            acc = bs.accumulator >>> 0;
            var len;
            if (((acc & 0x80000000) >>> 0) === 0x00000000) { len = 3; bs.shift(1); }
            else if (((acc & 0xC0000000) >>> 0) === 0x80000000) { len = ((acc >>> 28) & 0x3) + 4; bs.shift(4); }
            else if (((acc & 0xE0000000) >>> 0) === 0xC0000000) { len = ((acc >>> 26) & 0x7) + 8; bs.shift(6); }
            else if (((acc & 0xF0000000) >>> 0) === 0xE0000000) { len = ((acc >>> 24) & 0xF) + 16; bs.shift(8); }
            else if (((acc & 0xF8000000) >>> 0) === 0xF0000000) { len = ((acc >>> 22) & 0x1F) + 32; bs.shift(10); }
            else if (((acc & 0xFC000000) >>> 0) === 0xF8000000) { len = ((acc >>> 20) & 0x3F) + 64; bs.shift(12); }
            else if (((acc & 0xFE000000) >>> 0) === 0xFC000000) { len = ((acc >>> 18) & 0x7F) + 128; bs.shift(14); }
            else if (((acc & 0xFF000000) >>> 0) === 0xFE000000) { len = ((acc >>> 16) & 0xFF) + 256; bs.shift(16); }
            else if (((acc & 0xFF800000) >>> 0) === 0xFF000000) { len = ((acc >>> 14) & 0x1FF) + 512; bs.shift(18); }
            else if (((acc & 0xFFC00000) >>> 0) === 0xFF800000) { len = ((acc >>> 12) & 0x3FF) + 1024; bs.shift(20); }
            else if (((acc & 0xFFE00000) >>> 0) === 0xFFC00000) { len = ((acc >>> 10) & 0x7FF) + 2048; bs.shift(22); }
            else if (((acc & 0xFFF00000) >>> 0) === 0xFFE00000) { len = ((acc >>> 8) & 0xFFF) + 4096; bs.shift(24); }
            else if ((((acc & 0xFFF80000) >>> 0) === 0xFFF00000) && lvl) { len = ((acc >>> 6) & 0x1FFF) + 8192; bs.shift(26); }
            else if ((((acc & 0xFFFC0000) >>> 0) === 0xFFF80000) && lvl) { len = ((acc >>> 4) & 0x3FFF) + 16384; bs.shift(28); }
            else if ((((acc & 0xFFFE0000) >>> 0) === 0xFFFC0000) && lvl) { len = ((acc >>> 2) & 0x7FFF) + 32768; bs.shift(30); }
            else return null;
            var srcIdx = (hp - copyOffset) & histMask;
            do { H[hp++] = H[srcIdx++]; } while (--len);
        }
        var out = Uint8Array.prototype.slice.call(H, startHp, hp);
        this.historyOffset = hp;
        return out;
    };

    // ---- bit writer (compress, literal-only) ----
    function BitWriter() { this.bytes = []; this.cur = 0; this.nbits = 0; }
    BitWriter.prototype.write = function (value, n) {
        // append n bits of value (MSB-first within the value's low n bits)
        for (var i = n - 1; i >= 0; i--) {
            this.cur = (this.cur << 1) | ((value >>> i) & 1);
            this.nbits++;
            if (this.nbits === 8) { this.bytes.push(this.cur & 0xff); this.cur = 0; this.nbits = 0; }
        }
    };
    BitWriter.prototype.finish = function () {
        if (this.nbits > 0) { this.cur = (this.cur << (8 - this.nbits)) & 0xff; this.bytes.push(this.cur); this.cur = 0; this.nbits = 0; }
        return Uint8Array.from(this.bytes);
    };

    // Compress as literal-only. Returns { data, flags } where flags is the FreeRDP low-byte form
    // (PACKET_COMPRESSED | PACKET_AT_FRONT | PACKET_FLUSHED). We always FLUSH+AT_FRONT so the host needs
    // no prior history (self-contained packet) — simplest correct framing for our small caps PDU.
    Mppc.prototype.compress = function (src) {
        var bw = new BitWriter();
        for (var i = 0; i < src.length; i++) {
            var c = src[i];
            if (c < 0x80) bw.write(c, 8);              // literal < 0x80: bit0=0 + 7 bits → 8 bits total
            else bw.write(0x100 | (c & 0x7F), 9);      // literal ≥ 0x80: bits 10 + lower 7 bits → 9 bits
        }
        return { data: bw.finish(), flags: PACKET_COMPRESSED | PACKET_AT_FRONT | PACKET_FLUSHED };
    };

    var api = { Mppc: Mppc, PACKET_COMPRESSED: PACKET_COMPRESSED, PACKET_AT_FRONT: PACKET_AT_FRONT, PACKET_FLUSHED: PACKET_FLUSHED };
    if (typeof window !== "undefined") window.RdpMppc = api;
    if (typeof module !== "undefined") module.exports = api;
})();
