// audin.js — client side of the Audio Input (microphone) Virtual Channel (MS-RDPEAI / "AUDIO_INPUT").
//
// The microphone channel is a DYNAMIC virtual channel: modern Windows opens it via drdynvc once the
// host decides to redirect audio capture (gated on the same rdpsnd/rdpdr presence as audio output).
// protocol.js accepts the "AUDIO_INPUT" DVC Create and hands complete (reassembled) channel payloads
// to AudInput.onData(); AudInput implements the SNDIN negotiation handshake and, once the server
// opens the device, pulls raw PCM from a capture source (client.js, via getUserMedia) and ships it
// back as SNDIN_DATA PDUs so the captured microphone audio plays in the remote session.
//
// Scope (mirrors rdpsnd.js): we advertise ONLY uncompressed PCM (WAVE_FORMAT_PCM), so the server
// streams capture format negotiation around raw PCM that we can produce directly from Web Audio — no
// JS encoder needed. The server picks one of our PCM formats; we resample/convert capture to it.
//
// References: [MS-RDPEAI] 2.2.* and FreeRDP channels/audin/client/audin_main.c (ground truth for the
// message ids and PDU byte layouts).

// SNDIN message ids ([MS-RDPEAI] 2.2.1 / FreeRDP MSG_SNDIN_*).
const MSG_SNDIN_VERSION = 0x01;
const MSG_SNDIN_FORMATS = 0x02;
const MSG_SNDIN_OPEN = 0x03;
const MSG_SNDIN_OPEN_REPLY = 0x04;
const MSG_SNDIN_DATA_INCOMING = 0x05;
const MSG_SNDIN_DATA = 0x06;
const MSG_SNDIN_FORMATCHANGE = 0x07;

const AUDIN_WAVE_FORMAT_PCM = 0x0001;
// Client channel version we support (FreeRDP advertises 2; v1 also works). We echo min(server, ours).
const SNDIN_CLIENT_VERSION = 0x02;

// The PCM capture formats we advertise. The server intersects these with what it wants and OPENs one.
// Keep mono first (microphone is mono); 44100/16 is universally supported by Web Audio capture.
const CLIENT_CAPTURE_FORMATS = [
    { rate: 44100, bits: 16, channels: 1 },
    { rate: 22050, bits: 16, channels: 1 },
    { rate: 48000, bits: 16, channels: 1 },
    { rate: 16000, bits: 16, channels: 1 },
];

// send(payload Uint8Array) ships the payload on the AUDIO_INPUT DVC (protocol.js wraps it as a
// drdynvc DATA PDU + CHANNEL_PDU_HEADER). callbacks: { onLog(msg), onOpen(format), onClose() }.
//   onOpen(format) is called when the server has opened the device with the agreed PCM format
//   {rate, bits, channels}; client.js should start capturing and calling sendPcm() with that format.
//   onClose() is called when the server stops capture (format change away / channel close).
function AudInput(send, callbacks) {
    this.send = send;
    this.cb = callbacks || {};
    this.version = SNDIN_CLIENT_VERSION;
    this.formats = [];          // agreed formats (the PCM subset we sent back), indexed by format no
    this.openFormat = null;     // the format the server OPENed (capture target), or null when closed
    this.framesPerPacket = 0;
}

AudInput.prototype._log = function (m) { if (this.cb.onLog) this.cb.onLog(m); };

// ---- small writer ---------------------------------------------------------------------------------
function AinWriter() { this.b = []; }
AinWriter.prototype.u8 = function (v) { this.b.push(v & 0xff); return this; };
AinWriter.prototype.u16 = function (v) { this.b.push(v & 0xff, (v >> 8) & 0xff); return this; };
AinWriter.prototype.u32 = function (v) { this.b.push(v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >> 24) & 0xff); return this; };
AinWriter.prototype.bytes = function (a) { for (let i = 0; i < a.length; i++) this.b.push(a[i] & 0xff); return this; };
AinWriter.prototype.arr = function () { return new Uint8Array(this.b); };

// Serialize one WAVEFORMATEX ([MS-RDPEAI] AUDIO_FORMAT, identical to MS-RDPEA): PCM, no extra data.
function ainSerializeFormat(f) {
    const blockAlign = f.channels * (f.bits / 8);
    const avgBytes = f.rate * blockAlign;
    const w = new AinWriter();
    w.u16(AUDIN_WAVE_FORMAT_PCM); // wFormatTag
    w.u16(f.channels);            // nChannels
    w.u32(f.rate);                // nSamplesPerSec
    w.u32(avgBytes);              // nAvgBytesPerSec
    w.u16(blockAlign);            // nBlockAlign
    w.u16(f.bits);                // wBitsPerSample
    w.u16(0);                     // cbSize (no extra data for PCM)
    return w.arr();
}

// ---- inbound dispatch -----------------------------------------------------------------------------
AudInput.prototype.onData = function (payload) {
    if (!payload || payload.length < 1) return;
    const msgId = payload[0];
    const body = payload.subarray(1);
    switch (msgId) {
        case MSG_SNDIN_VERSION: return this._onVersion(body);
        case MSG_SNDIN_FORMATS: return this._onFormats(body);
        case MSG_SNDIN_OPEN: return this._onOpen(body);
        case MSG_SNDIN_FORMATCHANGE: return this._onFormatChange(body);
        default: this._log("ignoring msgId 0x" + msgId.toString(16));
    }
};

// Version PDU ([MS-RDPEAI] 2.2.2.1): server sends its version → echo the lower of (server, ours).
AudInput.prototype._onVersion = function (body) {
    if (body.length < 4) return;
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    const serverVersion = r.getUint32(0, true);
    // Don't answer if the server is newer than we support (mirrors FreeRDP); otherwise echo our version.
    if (serverVersion > SNDIN_CLIENT_VERSION) {
        this._log("server v" + serverVersion + " > client v" + SNDIN_CLIENT_VERSION + " — not answering");
        return;
    }
    this.version = serverVersion;
    const w = new AinWriter();
    w.u8(MSG_SNDIN_VERSION);
    w.u32(SNDIN_CLIENT_VERSION);
    this.send(w.arr());
    this._log("version: server v" + serverVersion + ", client v" + SNDIN_CLIENT_VERSION);
};

// Sound Formats PDU ([MS-RDPEAI] 2.2.2.2): NumFormats(4), cbSizeFormatsPacket(4), then NumFormats
// AUDIO_FORMAT records. We reply with DATA_INCOMING then our supported-format subset (the PCM ones we
// also advertise). The agreed-format index space is the list we send back.
AudInput.prototype._onFormats = function (body) {
    if (body.length < 8) return;
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    const numFormats = r.getUint32(0, true);
    // body[4..7] = cbSizeFormatsPacket (ignored on read).
    let o = 8;
    const serverFormats = [];
    for (let i = 0; i < numFormats && o + 18 <= body.length; i++) {
        const wFormatTag = r.getUint16(o, true);
        const nChannels = r.getUint16(o + 2, true);
        const nSamplesPerSec = r.getUint32(o + 4, true);
        const wBitsPerSample = r.getUint16(o + 14, true);
        const cbSize = r.getUint16(o + 16, true);
        o += 18 + cbSize;
        if (wFormatTag === AUDIN_WAVE_FORMAT_PCM) {
            serverFormats.push({ rate: nSamplesPerSec, bits: wBitsPerSample, channels: nChannels });
        }
    }

    // Keep the server's PCM formats we can capture (any PCM 8/16-bit — Web Audio resamples). If the
    // server offered no PCM, fall back to our preferred capture set so the channel still works.
    this.formats = serverFormats.length ? serverFormats : CLIENT_CAPTURE_FORMATS.slice();
    this._log(this.formats.length + " PCM capture format(s) agreed");

    // Acknowledge with Incoming Data PDU ([MS-RDPEAI] 2.2.2.3) — a bare 1-byte header, no body.
    this.send(new AinWriter().u8(MSG_SNDIN_DATA_INCOMING).arr());

    // Client Sound Formats PDU: Header(1) NumFormats(4) cbSizeFormatsPacket(4) then the AUDIO_FORMATs.
    // cbSizeFormatsPacket is the byte length of the SoundFormats array (FreeRDP computes it the same).
    const fmtBytes = new AinWriter();
    for (const f of this.formats) fmtBytes.bytes(ainSerializeFormat(f));
    const fmtArr = fmtBytes.arr();

    const w = new AinWriter();
    w.u8(MSG_SNDIN_FORMATS);
    w.u32(this.formats.length);     // NumFormats
    w.u32(fmtArr.length);           // cbSizeFormatsPacket
    w.bytes(fmtArr);
    this.send(w.arr());
};

// Open PDU ([MS-RDPEAI] 2.2.2.4): FramesPerPacket(4), initialFormat(4), then the capture WAVEFORMAT.
// We reply FORMATCHANGE(initialFormat) + OPEN_REPLY(Result=0) and ask the capture source to start.
AudInput.prototype._onOpen = function (body) {
    if (body.length < 8) return;
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    this.framesPerPacket = r.getUint32(0, true);
    const initialFormat = r.getUint32(4, true);
    if (initialFormat >= this.formats.length) {
        this._log("open: invalid format index " + initialFormat);
        return;
    }
    this.openFormat = this.formats[initialFormat];
    this._log("open: format #" + initialFormat + " " + this.openFormat.rate + "Hz/" +
        this.openFormat.bits + "bit/" + this.openFormat.channels + "ch, framesPerPacket=" + this.framesPerPacket);

    // Format Change PDU ([MS-RDPEAI] 2.2.2.8) then Open Reply PDU ([MS-RDPEAI] 2.2.2.5), in that order
    // (matches FreeRDP audin_process_open).
    this.send(new AinWriter().u8(MSG_SNDIN_FORMATCHANGE).u32(initialFormat).arr());
    this.send(new AinWriter().u8(MSG_SNDIN_OPEN_REPLY).u32(0).arr());

    if (this.cb.onOpen) this.cb.onOpen(this.openFormat);
};

// Format Change PDU ([MS-RDPEAI] 2.2.2.8): the server switches the active capture format mid-stream.
AudInput.prototype._onFormatChange = function (body) {
    if (body.length < 4) return;
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    const newFormat = r.getUint32(0, true);
    if (newFormat >= this.formats.length) { this._log("formatchange: invalid index " + newFormat); return; }
    this.openFormat = this.formats[newFormat];
    this._log("format change → #" + newFormat);
    // Echo the format change back (FreeRDP re-sends it after re-opening the device).
    this.send(new AinWriter().u8(MSG_SNDIN_FORMATCHANGE).u32(newFormat).arr());
    if (this.cb.onOpen) this.cb.onOpen(this.openFormat);
};

// Ship one chunk of captured PCM as a SNDIN_DATA PDU. `pcm` is a Uint8Array of little-endian
// interleaved samples already in the OPENed format. We send DATA_INCOMING then DATA (mirrors
// FreeRDP audin_receive_wave_data → audin_send_incoming_data_pdu + MSG_SNDIN_DATA).
AudInput.prototype.sendPcm = function (pcm) {
    if (!this.openFormat || !pcm || pcm.length === 0) return;
    this.send(new AinWriter().u8(MSG_SNDIN_DATA_INCOMING).arr());
    const w = new AinWriter();
    w.u8(MSG_SNDIN_DATA);
    w.bytes(pcm);
    this.send(w.arr());
};

// True once the server has opened the capture device and is expecting PCM.
AudInput.prototype.isOpen = function () { return !!this.openFormat; };
