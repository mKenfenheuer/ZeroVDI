// rdpsnd.js — client side of the Audio Output Virtual Channel (MS-RDPEA).
//
// The gateway relays the decrypted RDP stream; protocol.js joins the static "rdpsnd" channel and
// hands complete (reassembled) channel payloads to RdpSnd.onData(). RdpSnd implements the audio
// negotiation and wave-streaming handshake and, for each decoded wave, calls onWave(format, pcm) so
// client.js can play it via the Web Audio API.
//
// Scope (deliberately minimal, per the chosen design): we advertise ONLY uncompressed PCM formats
// (WAVE_FORMAT_PCM) at common rates, so the server streams raw PCM we can feed straight to Web Audio
// — no JS codec needed. Both the legacy two-PDU SNDC_WAVE framing and the modern single-PDU
// SNDC_WAVE2 are handled.
//
// References: [MS-RDPEA] 2.2.* (Server/Client Audio Formats, Training, Wave, Wave2, Wave Confirm).

// SNDC message types ([MS-RDPEA] 2.2.1 SNDPROLOG.msgType).
const SNDC_CLOSE = 0x01;
const SNDC_WAVE = 0x02;
const SNDC_SET_VOLUME = 0x03;
const SNDC_SET_PITCH = 0x04;
const SNDC_WAVE_CONFIRM = 0x05;
const SNDC_TRAINING = 0x06;
const SNDC_FORMATS = 0x07;
const SNDC_CRYPTKEY = 0x08;
const SNDC_WAVEENCRYPT = 0x09;
const SNDC_UDPWAVE = 0x0A;
const SNDC_UDPWAVELAST = 0x0B;
const SNDC_QUALITYMODE = 0x0C;
const SNDC_WAVE2 = 0x0D;

const WAVE_FORMAT_PCM = 0x0001;

// TS_AUDIO_FORMAT values we advertise (uncompressed PCM only). The server picks from these and we
// receive raw little-endian PCM that Web Audio can play directly.
const CLIENT_PCM_FORMATS = [
    { rate: 44100, bits: 16, channels: 2 },
    { rate: 44100, bits: 16, channels: 1 },
    { rate: 22050, bits: 16, channels: 2 },
    { rate: 22050, bits: 16, channels: 1 },
];

// Quality mode ([MS-RDPEA] 2.2.2.10) — HIGH_QUALITY favours fidelity over bandwidth (fine for LAN).
const HIGH_QUALITY = 0x0002;

// transport.send(payload Uint8Array) ships the payload on the rdpsnd MCS channel (CHANNEL_PDU_HEADER
// is added by protocol.js). callbacks: { onLog(msg), onWave(format, pcmUint8Array), onFormats(list) }.
function RdpSnd(send, callbacks) {
    this.send = send;
    this.cb = callbacks || {};
    this.formats = [];          // negotiated (agreed) formats, indexed by wFormatNo
    this.lastFormatNo = 0;
    this.lastTimestamp = 0;
    this._pendingWave = null;    // legacy SNDC_WAVE: WaveInfo seen, awaiting the body PDU
}

RdpSnd.prototype._log = function (m) { if (this.cb.onLog) this.cb.onLog(m); };

// ---- small writer ---------------------------------------------------------------------------------
function SndWriter() { this.b = []; }
SndWriter.prototype.u8 = function (v) { this.b.push(v & 0xff); return this; };
SndWriter.prototype.u16 = function (v) { this.b.push(v & 0xff, (v >> 8) & 0xff); return this; };
SndWriter.prototype.u32 = function (v) { this.b.push(v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >> 24) & 0xff); return this; };
SndWriter.prototype.bytes = function (a) { for (let i = 0; i < a.length; i++) this.b.push(a[i] & 0xff); return this; };
SndWriter.prototype.arr = function () { return new Uint8Array(this.b); };

// Wrap a body in the SNDPROLOG header (msgType, bPad, BodySize) and send it.
RdpSnd.prototype._sendPdu = function (msgType, body) {
    const w = new SndWriter();
    w.u8(msgType);
    w.u8(0);                 // bPad
    w.u16(body ? body.length : 0); // BodySize
    if (body) w.bytes(body);
    this.send(w.arr());
};

// Serialize one WAVEFORMATEX ([MS-RDPEA] 2.2.2.1.1 TS_AUDIO_FORMAT): PCM, no extra data.
function serializeFormat(f) {
    const blockAlign = f.channels * (f.bits / 8);
    const avgBytes = f.rate * blockAlign;
    const w = new SndWriter();
    w.u16(WAVE_FORMAT_PCM); // wFormatTag
    w.u16(f.channels);      // nChannels
    w.u32(f.rate);          // nSamplesPerSec
    w.u32(avgBytes);        // nAvgBytesPerSec
    w.u16(blockAlign);      // nBlockAlign
    w.u16(f.bits);          // wBitsPerSample
    w.u16(0);               // cbSize (no extra data for PCM)
    return w.arr();
}

// ---- inbound dispatch -----------------------------------------------------------------------------
RdpSnd.prototype.onData = function (payload) {
    if (!payload || payload.length < 4) return;
    // A pending legacy wave body arrives as a bare PDU whose first 4 bytes are bPad (0) before the
    // remaining audio — it has NO SNDPROLOG. Detect it by the pending-wave state.
    if (this._pendingWave) { this._onWaveBody(payload); return; }

    const msgType = payload[0];
    const bodySize = payload[2] | (payload[3] << 8);
    const body = payload.subarray(4, 4 + bodySize);

    switch (msgType) {
        case SNDC_FORMATS: return this._onServerFormats(body);
        case SNDC_TRAINING: return this._onTraining(body);
        case SNDC_WAVE: return this._onWaveInfo(body);
        case SNDC_WAVE2: return this._onWave2(body);
        case SNDC_CLOSE: this._log("server closed audio"); return;
        case SNDC_SET_VOLUME: case SNDC_SET_PITCH: case SNDC_CRYPTKEY: return; // ignored
        default: this._log("ignoring msgType 0x" + msgType.toString(16));
    }
};

// Server Audio Formats and Version PDU → reply with Client Audio Formats and Version + Quality Mode.
RdpSnd.prototype._onServerFormats = function (body) {
    // body: dwFlags(4) dwVolume(4) dwPitch(4) wDGramPort(2) wNumberOfFormats(2) cLastBlockConfirmed(1)
    //       wVersion(2) bPad(1) then wNumberOfFormats * TS_AUDIO_FORMAT.
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    let o = 0;
    o += 4; // dwFlags
    o += 4; // dwVolume
    o += 4; // dwPitch
    o += 2; // wDGramPort
    const numFormats = r.getUint16(o, true); o += 2;
    o += 1; // cLastBlockConfirmed
    const version = r.getUint16(o, true); o += 2;
    o += 1; // bPad

    // Parse the server's formats and keep only the PCM ones; the agreed-format index space is the
    // list we send back, so build `this.formats` from exactly what we advertise (the PCM subset).
    const serverFormats = [];
    for (let i = 0; i < numFormats && o + 18 <= body.length; i++) {
        const wFormatTag = r.getUint16(o, true);
        const nChannels = r.getUint16(o + 2, true);
        const nSamplesPerSec = r.getUint32(o + 4, true);
        const wBitsPerSample = r.getUint16(o + 14, true);
        const cbSize = r.getUint16(o + 16, true);
        o += 18 + cbSize;
        if (wFormatTag === WAVE_FORMAT_PCM) {
            serverFormats.push({ rate: nSamplesPerSec, bits: wBitsPerSample, channels: nChannels });
        }
    }

    // Agreed list = the server's PCM formats (so wFormatNo in WAVE PDUs indexes our list correctly).
    // If the server offered no PCM, fall back to our preferred PCM set so the channel still works.
    this.formats = serverFormats.length ? serverFormats : CLIENT_PCM_FORMATS.slice();
    this._log("server v" + version + ", " + this.formats.length + " PCM format(s) agreed");
    if (this.cb.onFormats) this.cb.onFormats(this.formats);

    // Client Audio Formats and Version PDU ([MS-RDPEA] 2.2.2.2).
    const fmtBytes = new SndWriter();
    for (const f of this.formats) fmtBytes.bytes(serializeFormat(f));
    const fmtArr = fmtBytes.arr();

    const w = new SndWriter();
    w.u32(0x00000000);       // dwFlags
    w.u32(0x00000000);       // dwVolume
    w.u32(0x00000000);       // dwPitch
    w.u16(0x0000);           // wDGramPort (TCP only — no UDP)
    w.u16(this.formats.length); // wNumberOfFormats
    w.u8(0);                 // cLastBlockConfirmed
    w.u16(0x0006);           // wVersion 6
    w.u8(0);                 // bPad
    w.bytes(fmtArr);
    this._sendPdu(SNDC_FORMATS, w.arr());

    // Quality Mode PDU ([MS-RDPEA] 2.2.2.10) — must follow the formats PDU.
    const q = new SndWriter();
    q.u16(HIGH_QUALITY);     // wQualityMode
    q.u16(0);                // Reserved
    this._sendPdu(SNDC_QUALITYMODE, q.arr());
};

// Training PDU → echo a Training Confirm with the same wTimeStamp and wPackSize ([MS-RDPEA] 2.2.3.2).
RdpSnd.prototype._onTraining = function (body) {
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    const wTimeStamp = body.length >= 2 ? r.getUint16(0, true) : 0;
    const wPackSize = body.length >= 4 ? r.getUint16(2, true) : 0;
    const w = new SndWriter();
    w.u16(wTimeStamp);
    w.u16(wPackSize);
    this._sendPdu(SNDC_TRAINING, w.arr());
};

// Legacy WaveInfo PDU ([MS-RDPEA] 2.2.3.3): header carries wTimeStamp(2), wFormatNo(2), cBlockNo(1),
// bPad(3), then the FIRST 4 bytes of the wave data. The remaining data arrives in the next PDU
// (SNDWAVE body) which begins with a 4-byte bPad. We stash state and the 4 leading bytes here.
RdpSnd.prototype._onWaveInfo = function (body) {
    if (body.length < 12) return;
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    const wTimeStamp = r.getUint16(0, true);
    const wFormatNo = r.getUint16(2, true);
    const cBlockNo = body[4];
    // body[5..7] = bPad(3); body[8..11] = first 4 bytes of the audio data.
    const head = body.subarray(8, 12).slice();
    this._pendingWave = { timestamp: wTimeStamp, formatNo: wFormatNo, blockNo: cBlockNo, head: head };
};

// The SNDWAVE body PDU that follows a WaveInfo: 4-byte bPad then the rest of the audio data.
RdpSnd.prototype._onWaveBody = function (payload) {
    const p = this._pendingWave;
    this._pendingWave = null;
    if (payload.length < 4) return;
    const rest = payload.subarray(4);
    const pcm = new Uint8Array(p.head.length + rest.length);
    pcm.set(p.head, 0);
    pcm.set(rest, p.head.length);
    this._deliverWave(p.formatNo, p.blockNo, p.timestamp, pcm);
};

// Modern single-PDU wave ([MS-RDPEA] 2.2.3.10 SNDWAVE2): wTimeStamp(2), wFormatNo(2), cBlockNo(1),
// bPad(3), dwAudioTimeStamp(4) then the complete audio data.
RdpSnd.prototype._onWave2 = function (body) {
    if (body.length < 12) return;
    const r = new DataView(body.buffer, body.byteOffset, body.byteLength);
    const wTimeStamp = r.getUint16(0, true);
    const wFormatNo = r.getUint16(2, true);
    const cBlockNo = body[4];
    const pcm = body.subarray(12).slice();
    this._deliverWave(wFormatNo, cBlockNo, wTimeStamp, pcm);
};

// Hand decoded PCM to the player and confirm the block so the server keeps streaming.
RdpSnd.prototype._deliverWave = function (formatNo, blockNo, timestamp, pcm) {
    const fmt = this.formats[formatNo] || this.formats[0];
    if (fmt && pcm.length && this.cb.onWave) this.cb.onWave(fmt, pcm);
    // Wave Confirm PDU ([MS-RDPEA] 2.2.3.8): wTimeStamp(2), cConfirmedBlockNo(1), bPad(1).
    const w = new SndWriter();
    w.u16(timestamp & 0xffff);
    w.u8(blockNo & 0xff);
    w.u8(0);
    this._sendPdu(SNDC_WAVE_CONFIRM, w.arr());
};
