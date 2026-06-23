// cliprdr.js — client side of the Clipboard Virtual Channel Extension (MS-RDPECLIP).
//
// protocol.js joins the static "cliprdr" channel and hands complete (reassembled) channel payloads
// to ClipRdr.onData(). This implements two-way *Unicode text* clipboard sync:
//   - remote → browser: when the remote session copies text, we request it and deliver it via
//     onRemoteText(text); client.js writes it to the browser clipboard.
//   - browser → remote: client.js calls setLocalText(text) (e.g. from a paste/refresh action); we
//     advertise CF_UNICODETEXT and serve the text when the remote session pastes.
//
// Only CF_UNICODETEXT is negotiated — images and rich formats are out of scope for this pass.
//
// References: [MS-RDPECLIP] 2.2.* (Clipboard Capabilities, Monitor Ready, Format List, Format List
// Response, Format Data Request, Format Data Response).

// CLIPRDR_HEADER.msgType ([MS-RDPECLIP] 2.2.1).
const CB_MONITOR_READY = 0x0001;
const CB_FORMAT_LIST = 0x0002;
const CB_FORMAT_LIST_RESPONSE = 0x0003;
const CB_FORMAT_DATA_REQUEST = 0x0004;
const CB_FORMAT_DATA_RESPONSE = 0x0005;
const CB_TEMP_DIRECTORY = 0x0006;
const CB_CLIP_CAPS = 0x0007;

// msgFlags.
const CB_RESPONSE_OK = 0x0001;
const CB_RESPONSE_FAIL = 0x0002;

// Standard Windows clipboard format id for UTF-16LE text.
const CF_UNICODETEXT = 13;

// Capability set type / general flags ([MS-RDPECLIP] 2.2.2.1.1).
const CB_CAPSTYPE_GENERAL = 0x0001;
const CB_USE_LONG_FORMAT_NAMES = 0x00000002;

// transport.send(payload Uint8Array) ships the payload on the cliprdr MCS channel (the
// CHANNEL_PDU_HEADER is added by protocol.js). callbacks: { onLog(msg), onRemoteText(text) }.
function ClipRdr(send, callbacks) {
    this.send = send;
    this.cb = callbacks || {};
    this.localText = null;       // text we offer to the remote (browser → remote)
    this._haveOffered = false;   // whether we've sent a non-empty format list
    // Format-name variant for the Format List PDU. Per [MS-RDPECLIP] 2.2.2.1.1.1, the Long Format Name
    // variant is used ONLY when BOTH endpoints set CB_USE_LONG_FORMAT_NAMES; otherwise the Short Format
    // Name (fixed 32-byte name) variant MUST be used. We always advertise long names; this tracks what
    // the server advertised so we send AND parse the list in the form the server actually negotiated.
    // It defaults to false ("server caps not seen / no flag") so we degrade to short names safely.
    this._serverLongNames = false;
}

ClipRdr.prototype._log = function (m) { if (this.cb.onLog) this.cb.onLog(m); };

// ---- small writer ---------------------------------------------------------------------------------
function ClipWriter() { this.b = []; }
ClipWriter.prototype.u8 = function (v) { this.b.push(v & 0xff); return this; };
ClipWriter.prototype.u16 = function (v) { this.b.push(v & 0xff, (v >> 8) & 0xff); return this; };
ClipWriter.prototype.u32 = function (v) { this.b.push(v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >> 24) & 0xff); return this; };
ClipWriter.prototype.bytes = function (a) { for (let i = 0; i < a.length; i++) this.b.push(a[i] & 0xff); return this; };
ClipWriter.prototype.arr = function () { return new Uint8Array(this.b); };

function utf16leBytes(s) {
    const w = new ClipWriter();
    for (let i = 0; i < s.length; i++) { const c = s.charCodeAt(i); w.u16(c); }
    return w.arr();
}
function utf16leToString(u8) {
    let s = "";
    for (let i = 0; i + 1 < u8.length; i += 2) {
        const c = u8[i] | (u8[i + 1] << 8);
        if (c === 0) break; // stop at the terminating NUL
        s += String.fromCharCode(c);
    }
    return s;
}

// Wrap a body in CLIPRDR_HEADER (msgType, msgFlags, dataLen) and send.
ClipRdr.prototype._sendPdu = function (msgType, msgFlags, body) {
    const w = new ClipWriter();
    w.u16(msgType);
    w.u16(msgFlags || 0);
    w.u32(body ? body.length : 0);
    if (body) w.bytes(body);
    this.send(w.arr());
};

// ---- outbound: offer local text to the remote ----------------------------------------------------
// Called by client.js when the browser has new clipboard text the remote should be able to paste.
ClipRdr.prototype.setLocalText = function (text) {
    this.localText = (text == null) ? null : String(text);
    this._sendFormatList();
};

// Format List PDU ([MS-RDPECLIP] 2.2.3.1) advertising CF_UNICODETEXT. The entry layout depends on the
// negotiated form: Long Format Name = formatId(4) + NUL-terminated UTF-16 name (we use an empty name,
// i.e. a single u16 NUL); Short Format Name = formatId(4) + a fixed 32-byte name block (zeros, since
// CF_UNICODETEXT is a standard format with no name). CF_UNICODETEXT names are Unicode, so we never set
// CB_ASCII_NAMES.
ClipRdr.prototype._sendFormatList = function () {
    const body = new ClipWriter();
    if (this.localText != null) {
        body.u32(CF_UNICODETEXT); // formatId
        if (this._serverLongNames) {
            body.u16(0x0000);     // empty wszFormatName (just the terminating NUL)
        } else {
            for (let i = 0; i < 32; i++) body.u8(0); // 32-byte formatName block, all zeros
        }
        this._haveOffered = true;
    } // else: empty list = "clipboard cleared / nothing on offer"
    this._sendPdu(CB_FORMAT_LIST, 0, body.arr());
};

// ---- inbound dispatch -----------------------------------------------------------------------------
ClipRdr.prototype.onData = function (payload) {
    if (!payload || payload.length < 8) return;
    const r = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
    const msgType = r.getUint16(0, true);
    const msgFlags = r.getUint16(2, true);
    const dataLen = r.getUint32(4, true);
    const body = payload.subarray(8, 8 + Math.min(dataLen, payload.length - 8));

    switch (msgType) {
        case CB_MONITOR_READY: return this._onMonitorReady();
        case CB_CLIP_CAPS: return this._onServerCaps(body);
        case CB_FORMAT_LIST: return this._onFormatList(body);
        case CB_FORMAT_LIST_RESPONSE: return; // ack of our format list; nothing to do
        case CB_FORMAT_DATA_REQUEST: return this._onFormatDataRequest(body);
        case CB_FORMAT_DATA_RESPONSE: return this._onFormatDataResponse(msgFlags, body);
        default: this._log("ignoring msgType 0x" + msgType.toString(16));
    }
};

// Server Clipboard Capabilities ([MS-RDPECLIP] 2.2.2.1). Sent before Monitor Ready. We parse just the
// General capability set's generalFlags to learn whether the server supports long format names — the
// Format List form (long vs. short) MUST match what BOTH sides advertised (2.2.2.1.1.1).
ClipRdr.prototype._onServerCaps = function (body) {
    if (body.length < 4) return; // no caps → defaults (no flags) → short format names
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    const cCapsSets = r.getUint16(0, true);
    let o = 4; // skip cCapabilitiesSets(2) + pad1(2)
    for (let i = 0; i < cCapsSets && o + 4 <= body.length; i++) {
        const capType = r.getUint16(o, true);
        const capLen = r.getUint16(o + 2, true); // length of the whole CLIPRDR_CAPS_SET
        if (capType === CB_CAPSTYPE_GENERAL && o + 12 <= body.length) {
            const generalFlags = r.getUint32(o + 8, true); // after capType(2)+capLen(2)+version(4)
            this._serverLongNames = (generalFlags & CB_USE_LONG_FORMAT_NAMES) !== 0;
        }
        o += capLen >= 4 ? capLen : 4; // advance by the set length (guard against a bogus 0)
    }
    this._log("server caps: longFormatNames=" + this._serverLongNames);
};

// Monitor Ready ([MS-RDPECLIP] 2.2.2.2): the channel is up. Per the init sequence (1.3.2.1) send our
// capabilities, then the Temporary Directory PDU, then an initial (empty unless setLocalText was
// already called) format list to complete the handshake.
ClipRdr.prototype._onMonitorReady = function () {
    this._log("monitor ready");
    this._sendCapabilities();
    this._sendTempDirectory();
    this._sendFormatList();
};

// Temporary Directory PDU ([MS-RDPECLIP] 2.2.2.3): a fixed 520-byte NUL-padded UTF-16 path. We don't
// redirect files, so the path is unused, but some hosts expect this PDU during init — send an empty
// (all-NUL) path to keep the handshake well-formed.
ClipRdr.prototype._sendTempDirectory = function () {
    const body = new ClipWriter();
    for (let i = 0; i < 520; i++) body.u8(0); // wszTempDir: 520 bytes, all zeros
    this._sendPdu(CB_TEMP_DIRECTORY, 0, body.arr());
};

// Clipboard Capabilities PDU ([MS-RDPECLIP] 2.2.2.1) advertising long format names.
ClipRdr.prototype._sendCapabilities = function () {
    const body = new ClipWriter();
    body.u16(1);                 // cCapabilitiesSets
    body.u16(0);                 // pad1
    // CLIPRDR_GENERAL_CAPABILITY ([MS-RDPECLIP] 2.2.2.1.1).
    body.u16(CB_CAPSTYPE_GENERAL); // capabilitySetType
    body.u16(12);                  // lengthCapability (header 4 + version 4 + flags 4)
    body.u32(0x00000002);          // version 2
    body.u32(CB_USE_LONG_FORMAT_NAMES);
    this._sendPdu(CB_CLIP_CAPS, 0, body.arr());
};

// Remote announced new clipboard formats. Ack, then if it offers Unicode/ASCII text, request it.
ClipRdr.prototype._onFormatList = function (body) {
    // Ack the list ([MS-RDPECLIP] 2.2.3.2).
    this._sendPdu(CB_FORMAT_LIST_RESPONSE, CB_RESPONSE_OK, null);

    // Parse the format ids. The entry layout depends on the negotiated name form (2.2.3.1):
    //   Long  = formatId(4) + NUL-terminated UTF-16 name (skip up to & including the double-NUL).
    //   Short = formatId(4) + a fixed 32-byte name block.
    // Using the wrong form misreads every id and we'd never spot CF_UNICODETEXT — this is exactly why
    // remote→browser paste failed when the server negotiated short names.
    let wantId = null;
    let o = 0;
    while (o + 4 <= body.length) {
        const formatId = body[o] | (body[o + 1] << 8) | (body[o + 2] << 16) | (body[o + 3] << 24);
        o += 4;
        if (this._serverLongNames) {
            // Skip the UTF-16 name up to and including its double-NUL terminator.
            while (o + 1 < body.length && !(body[o] === 0 && body[o + 1] === 0)) o += 2;
            o += 2;
        } else {
            o += 32; // fixed 32-byte formatName block
        }
        if (formatId === CF_UNICODETEXT) { wantId = CF_UNICODETEXT; break; }
        if (formatId === 1 && wantId == null) wantId = 1; // CF_TEXT (ASCII) as a fallback
    }

    if (wantId != null) {
        this._requestId = wantId;
        const req = new ClipWriter();
        req.u32(wantId); // requestedFormatId
        this._sendPdu(CB_FORMAT_DATA_REQUEST, 0, req.arr());
    }
};

// The remote is pasting our offered text — serve it as the requested format ([MS-RDPECLIP] 2.2.5.1).
ClipRdr.prototype._onFormatDataRequest = function (body) {
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    const requestedFormatId = body.length >= 4 ? r.getUint32(0, true) : 0;
    if (requestedFormatId === CF_UNICODETEXT && this.localText != null) {
        // Format Data Response: UTF-16LE text + terminating NUL.
        const text = utf16leBytes(this.localText);
        const resp = new ClipWriter();
        resp.bytes(text);
        resp.u16(0); // terminating NUL
        this._sendPdu(CB_FORMAT_DATA_RESPONSE, CB_RESPONSE_OK, resp.arr());
    } else {
        this._sendPdu(CB_FORMAT_DATA_RESPONSE, CB_RESPONSE_FAIL, null);
    }
};

// The remote returned the clipboard contents we requested — deliver text to the browser.
ClipRdr.prototype._onFormatDataResponse = function (msgFlags, body) {
    if (!(msgFlags & CB_RESPONSE_OK)) return;
    let text;
    if (this._requestId === CF_UNICODETEXT) text = utf16leToString(body);
    else { // CF_TEXT (ASCII/ANSI)
        text = "";
        for (let i = 0; i < body.length; i++) { if (body[i] === 0) break; text += String.fromCharCode(body[i]); }
    }
    this._requestId = null;
    if (text && this.cb.onRemoteText) this.cb.onRemoteText(text);
};
