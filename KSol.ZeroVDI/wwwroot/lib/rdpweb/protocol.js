// protocol.js — client-side RDP PDU stack driven over the gateway WebSocket relay.
//
// The .NET gateway performs X.224 PROTOCOL_HYBRID negotiation, TLS, and CredSSP/NLA, then relays the
// *decrypted* RDP byte stream verbatim. So this file owns everything from the MCS connect-initial PDU
// onward: MCS (connect / erect-domain / attach-user / channel-join), GCC conference-create with the
// client core/security/network data, the client info PDU, licensing, the capabilities exchange
// (confirm active), connection finalization (sync / control / font), and reassembly of the inbound
// TPKT/X.224 and fastpath streams.
//
// It is a faithful JS port of kulaginds/rdp-html5's Go server stack
// (internal/pkg/rdp/{connect,capabilities_exchange,connection_finalization}.go and the pdu/mcs/gcc/
// per/ber/headers/tpkt/x224/fastpath packages), but driven over the relay instead of a net.Conn.
//
// Wire model:
//  - send: this layer hands raw RDP bytes to `transport.send(Uint8Array)` (the WebSocket).
//  - recv: the WebSocket feeds inbound bytes to `feed(Uint8Array)`; this layer reassembles PDUs and,
//    once the handshake is done, emits fastpath update payloads via the `onUpdate(ArrayBuffer)`
//    callback (consumed by client.js for bitmap/pointer rendering).

const PROJECT_NAME = "rdpweb";

// GFX codec mode, configurable via `?gfx=<mode>` query param or `window.RDP_GFX_MODE`. Controls both
// the CS_CORE SUPPORT_DYNVC_GFX_PROTOCOL advertisement and which RDPGFX capsets we advertise (which in
// turn steers the host's codec choice). Modes:
//   "off"        — GFX disabled; screen renders via the legacy bitmap fastpath (default)
//   "clearcodec" — GFX on, advertise up to v8.1 only; on hosts without AVC the host streams ClearCodec
//   "progressive"— GFX on, advertise v10+ with AVC explicitly DISABLED. Per MS-RDPEGFX, a Windows host
//                  with GFX negotiated at v10+ but AVC unavailable falls back to RemoteFX Progressive
//                  (CAPROGRESSIVE) rather than ClearCodec — v8/v8.1-only capsets get ClearCodec instead,
//                  so this needs its own capset list. GNOME Remote Desktop streams Progressive over
//                  WIRE_TO_SURFACE_2 regardless of advertised caps, so it's unaffected by this mode.
//   "avc420"     — GFX on, prefer single-stream H.264 (AVC420). Adds v10 (AVC enabled) so AVC-capable
//                  hosts engage H.264; falls back to ClearCodec if the host still refuses AVC
//   "avc444"     — GFX on, allow AVC444 (dual luma+chroma H.264) in addition to AVC420
//   "auto"/"1"   — alias for "avc420" (try the best the host will give, decode whatever arrives)
// Returns the normalized mode string, or "off".
function rdpGfxMode() {
    if (typeof window === "undefined") return "off";
    let v = null;
    if (window.RDP_GFX_MODE != null) v = String(window.RDP_GFX_MODE);
    else {
        try { v = new URLSearchParams(window.location.search).get("gfx"); } catch (e) { v = null; }
    }
    if (v == null) return "off";
    v = v.toLowerCase();
    if (v === "0" || v === "false" || v === "off" || v === "") return "off";
    if (v === "1" || v === "true" || v === "auto") return "avc420";
    if (v === "clearcodec" || v === "clear") return "clearcodec";
    if (v === "progressive" || v === "rfx" || v === "remotefx") return "progressive";
    if (v === "avc420" || v === "h264" || v === "avc") return "avc420";
    if (v === "avc444") return "avc444";
    return "avc420"; // unknown but truthy → try AVC
}

// Whether the GFX path is enabled at all (any mode other than "off"). The CS_CORE GFX flag + extra GCC
// blocks ride on this.
function rdpTryGfx() { return rdpGfxMode() !== "off"; }

// ---- small byte writer (grows automatically; big-endian/little-endian helpers) -------------------
function ByteWriter() {
    this._bytes = [];
}
ByteWriter.prototype.u8 = function (v) { this._bytes.push(v & 0xff); return this; };
ByteWriter.prototype.u16le = function (v) { this._bytes.push(v & 0xff, (v >> 8) & 0xff); return this; };
ByteWriter.prototype.u16be = function (v) { this._bytes.push((v >> 8) & 0xff, v & 0xff); return this; };
ByteWriter.prototype.u32le = function (v) {
    this._bytes.push(v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >> 24) & 0xff); return this;
};
ByteWriter.prototype.u32be = function (v) {
    this._bytes.push((v >> 24) & 0xff, (v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff); return this;
};
ByteWriter.prototype.bytes = function (arr) {
    const a = (arr instanceof Uint8Array) ? arr : new Uint8Array(arr);
    for (let i = 0; i < a.length; i++) this._bytes.push(a[i]);
    return this;
};
ByteWriter.prototype.zeros = function (n) { for (let i = 0; i < n; i++) this._bytes.push(0); return this; };
ByteWriter.prototype.utf16le = function (s) {
    for (let i = 0; i < s.length; i++) { const c = s.charCodeAt(i); this._bytes.push(c & 0xff, (c >> 8) & 0xff); }
    return this;
};
ByteWriter.prototype.length = function () { return this._bytes.length; };
ByteWriter.prototype.toArray = function () { return new Uint8Array(this._bytes); };

// ---- byte reader over a Uint8Array (little-endian RDP fields) -------------------------------------
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

// ================================================================================================
// BER (used only for the MCS Connect-Initial / Connect-Response envelope)
// ================================================================================================
const Ber = {
    writeLength: function (w, size) {
        if (size > 0x7f) { w.u8(0x82); w.u16be(size); } else { w.u8(size); }
    },
    writeApplicationTag: function (w, tag, size) {
        if (tag > 30) { w.u8(0x7f); w.u8(tag); Ber.writeLength(w, size); }
        else { w.u8(tag); Ber.writeLength(w, size); }
    },
    writeBoolean: function (w, b) { w.u8(0x01); Ber.writeLength(w, 1); w.u8(b ? 0xff : 0x00); },
    writeInteger: function (w, n) {
        w.u8(0x02);
        if (n <= 0xff) { Ber.writeLength(w, 1); w.u8(n); }
        else if (n <= 0xffff) { Ber.writeLength(w, 2); w.u16be(n); }
        else { Ber.writeLength(w, 4); w.u32be(n); }
    },
    writeOctetString: function (w, arr) { w.u8(0x04); Ber.writeLength(w, arr.length); w.bytes(arr); },
    writeSequence: function (w, dataArr) { w.u8(0x30); Ber.writeLength(w, dataArr.length); w.bytes(dataArr); },

    readLength: function (r) {
        let size = r.u8();
        if (size & 0x80) {
            const n = size & 0x7f;
            if (n === 1) return r.u8();
            if (n === 2) return r.u16be();
            throw new Error("BER length must be 1 or 2 bytes");
        }
        return size;
    },
    readApplicationTag: function (r) {
        const identifier = r.u8();
        // ClassApplication(0x40) | PCConstruct(0x20) | TagMask(0x1f) == 0x7f
        if (identifier !== 0x7f) throw new Error("ReadApplicationTag: invalid data");
        return r.u8();
    },
    readUniversalTag: function (r, tag, pc) {
        const bb = r.u8();
        // ClassUniversal(0x00) | (pc?0x20:0x00) | (tag & 0x1f)
        return bb === (0x00 | (pc ? 0x20 : 0x00) | (tag & 0x1f));
    },
    readEnumerated: function (r) {
        if (!Ber.readUniversalTag(r, 0x0a, false)) throw new Error("bad BER enumerated tag");
        const len = Ber.readLength(r);
        if (len !== 1) throw new Error("BER enumerated size != 1");
        return r.u8();
    },
    readInteger: function (r) {
        if (!Ber.readUniversalTag(r, 0x02, false)) throw new Error("bad BER integer tag");
        const size = Ber.readLength(r);
        switch (size) {
            case 1: return r.u8();
            case 2: return r.u16be();
            case 3: { const a = r.u8(); const b = r.u16be(); return (a << 16) + b; }
            case 4: { const a = r.u8(), b = r.u8(), c = r.u8(), d = r.u8(); return ((a << 24) | (b << 16) | (c << 8) | d) >>> 0; }
            default: throw new Error("bad BER integer length");
        }
    },
};

// ================================================================================================
// PER (used for GCC conference-create and the MCS domain PDUs)
// ================================================================================================
const Per = {
    writeChoice: function (w, choice) { w.u8(choice); },
    writeSelection: function (w, sel) { w.u8(sel); },
    writeNumberOfSet: function (w, n) { w.u8(n); },
    writePadding: function (w, n) { w.zeros(n); },
    writeObjectIdentifier: function (w, oid) {
        Per.writeLength(w, 5);
        w.u8(((oid[0] << 4) & (oid[1] & 0x0f)));
        w.u8(oid[2]); w.u8(oid[3]); w.u8(oid[4]); w.u8(oid[5]);
    },
    writeLength: function (w, value) {
        if (value > 0x7f) { w.u16be(value | 0x8000); } else { w.u8(value); }
    },
    writeNumericString: function (w, nStr, minValue) {
        const length = nStr.length;
        let mLength = minValue;
        if (length - minValue >= 0) mLength = length - minValue;
        const result = [];
        for (let i = 0; i < length; i += 2) {
            let c1 = nStr.charCodeAt(i);
            let c2 = 0x30;
            if (i + 1 < length) c2 = nStr.charCodeAt(i + 1);
            c1 = (c1 - 0x30) % 10;
            c2 = (c2 - 0x30) % 10;
            result.push(((c1 << 4) | c2) & 0xff);
        }
        Per.writeLength(w, mLength);
        w.bytes(result);
    },
    writeOctetStream: function (w, str, minValue) {
        // str is a JS string of ASCII chars (e.g. "Duca") or a Uint8Array
        const arr = (typeof str === "string")
            ? Uint8Array.from(str, c => c.charCodeAt(0))
            : str;
        let mLength = minValue;
        if (arr.length - minValue >= 0) mLength = arr.length - minValue;
        Per.writeLength(w, mLength);
        w.bytes(arr);
    },
    writeInteger16: function (w, value, minimum) { w.u16be((value - minimum) & 0xffff); },
    writeInteger: function (w, value) {
        if (value <= 0xff) { Per.writeLength(w, 1); w.u8(value); }
        else if (value < 0xffff) { Per.writeLength(w, 2); w.u16be(value); }
        else { Per.writeLength(w, 4); w.u32be(value); }
    },

    readChoice: function (r) { return r.u8(); },
    readLength: function (r) {
        let octet = r.u8();
        if ((octet & 0x80) !== 0x80) return octet;
        octet &= 0x7f;
        let size = octet << 8;
        size += r.u8();
        return size;
    },
    readInteger16: function (r, minimum) { return (r.u16be() + minimum) & 0xffff; },
    readInteger: function (r) {
        const size = Per.readLength(r);
        switch (size) {
            case 1: return r.u8();
            case 2: return r.u16be();
            case 4: return r.u32le();
            default: throw new Error("bad PER integer length");
        }
    },
    readEnumerates: function (r) { return r.u8(); },
    readNumberOfSet: function (r) { return r.u8(); },
    readObjectIdentifier: function (r, oid) {
        const size = Per.readLength(r);
        if (size !== 5) return false;
        const t12 = r.u8();
        const a = [t12 >> 4, t12 & 0x0f, r.u8(), r.u8(), r.u8(), r.u8()];
        for (let i = 0; i < 6; i++) if (oid[i] !== a[i]) return false;
        return true;
    },
    readOctetStream: function (r, octetStream, minValue) {
        const length = Per.readLength(r);
        const size = length + minValue;
        if (size !== octetStream.length) return false;
        for (let i = 0; i < size; i++) if (octetStream[i] !== r.u8()) return false;
        return true;
    },
};

const T124_OID = [0, 0, 20, 124, 0, 1];
const H221_CS_KEY = "Duca";
const H221_SC_KEY = "McDn";

// ================================================================================================
// GCC conference create request (wraps the client user-data block)
// ================================================================================================
function gccConferenceCreateRequest(userData) {
    const w = new ByteWriter();
    Per.writeChoice(w, 0);
    Per.writeObjectIdentifier(w, T124_OID);
    Per.writeLength(w, 14 + userData.length);
    Per.writeChoice(w, 0);
    Per.writeSelection(w, 0x08);
    Per.writeNumericString(w, "1", 1);
    Per.writePadding(w, 1);
    Per.writeNumberOfSet(w, 1);
    Per.writeChoice(w, 0xc0);
    Per.writeOctetStream(w, H221_CS_KEY, 4);
    Per.writeOctetStream(w, userData, 0);
    return w.toArray();
}

// Skips the GCC conference-create-response wrapper, returning a reader positioned at the server
// user-data block (the concatenation of SC_CORE/SC_SECURITY/SC_NET data headers).
function gccParseConferenceCreateResponse(r) {
    Per.readChoice(r);
    if (!Per.readObjectIdentifier(r, T124_OID)) throw new Error("bad GCC t124 OID");
    Per.readLength(r);
    Per.readChoice(r);
    Per.readInteger16(r, 1001);
    Per.readInteger(r);
    Per.readEnumerates(r);
    Per.readNumberOfSet(r);
    Per.readChoice(r);
    if (!Per.readOctetStream(r, Uint8Array.from(H221_SC_KEY, c => c.charCodeAt(0)), 4))
        throw new Error("bad H221 SC_KEY");
    Per.readLength(r);
    return r; // now positioned at server user data
}

// ================================================================================================
// MCS connect-initial / connect-response
// ================================================================================================
function mcsDomainParametersSerialize(p) {
    const w = new ByteWriter();
    Ber.writeInteger(w, p.maxChannelIds);
    Ber.writeInteger(w, p.maxUserIds);
    Ber.writeInteger(w, p.maxTokenIds);
    Ber.writeInteger(w, p.numPriorities);
    Ber.writeInteger(w, p.minThroughput);
    Ber.writeInteger(w, p.maxHeight);
    Ber.writeInteger(w, p.maxMCSPDUsize);
    Ber.writeInteger(w, p.protocolVersion);
    return w.toArray();
}

function mcsConnectInitialSerialize(userData) {
    const target = { maxChannelIds: 34, maxUserIds: 2, maxTokenIds: 0, numPriorities: 1, minThroughput: 0, maxHeight: 1, maxMCSPDUsize: 65535, protocolVersion: 2 };
    const minimum = { maxChannelIds: 1, maxUserIds: 1, maxTokenIds: 1, numPriorities: 1, minThroughput: 0, maxHeight: 1, maxMCSPDUsize: 1056, protocolVersion: 2 };
    const maximum = { maxChannelIds: 65535, maxUserIds: 65535, maxTokenIds: 65535, numPriorities: 1, minThroughput: 0, maxHeight: 1, maxMCSPDUsize: 65535, protocolVersion: 2 };

    const inner = new ByteWriter();
    Ber.writeOctetString(inner, new Uint8Array([0x01])); // calledDomainSelector
    Ber.writeOctetString(inner, new Uint8Array([0x01])); // callingDomainSelector
    Ber.writeBoolean(inner, true);                       // upwardFlag
    Ber.writeSequence(inner, mcsDomainParametersSerialize(target));
    Ber.writeSequence(inner, mcsDomainParametersSerialize(minimum));
    Ber.writeSequence(inner, mcsDomainParametersSerialize(maximum));
    Ber.writeOctetString(inner, gccConferenceCreateRequest(userData));
    const innerArr = inner.toArray();

    // Connect-Initial application tag = 101
    const w = new ByteWriter();
    Ber.writeApplicationTag(w, 101, innerArr.length);
    w.bytes(innerArr);
    return w.toArray();
}

// Parse a Connect-Response, returning a reader positioned at the GCC user data.
function mcsParseConnectResponse(r) {
    const application = Ber.readApplicationTag(r);
    if (application !== 102) throw new Error("MCS: not a connect-response (app=" + application + ")");
    Ber.readLength(r);
    const result = Ber.readEnumerated(r);
    if (result !== 0) throw new Error("MCS connect-response result=" + result);
    Ber.readInteger(r); // calledConnectId
    if (!Ber.readUniversalTag(r, 0x10, true)) throw new Error("bad BER sequence tag");
    Ber.readLength(r);
    // domainParameters (8 integers)
    for (let i = 0; i < 8; i++) Ber.readInteger(r);
    if (!Ber.readUniversalTag(r, 0x04, false)) throw new Error("bad BER octet-string tag");
    Ber.readLength(r);
    return gccParseConferenceCreateResponse(r);
}

// MCS domain PDU application codes (T.125) — choice byte is (application << 2).
const MCS_ERECT_DOMAIN = 1;
const MCS_ATTACH_USER_REQUEST = 10;
const MCS_ATTACH_USER_CONFIRM = 11;
const MCS_CHANNEL_JOIN_REQUEST = 14;
const MCS_CHANNEL_JOIN_CONFIRM = 15;
const MCS_SEND_DATA_REQUEST = 25;
const MCS_SEND_DATA_INDICATION = 26;
const MCS_DISCONNECT_ULTIMATUM = 8;

function mcsErectDomainSerialize() {
    const w = new ByteWriter();
    Per.writeChoice(w, MCS_ERECT_DOMAIN << 2);
    Per.writeInteger(w, 0);
    Per.writeInteger(w, 0);
    return w.toArray();
}
function mcsAttachUserSerialize() {
    const w = new ByteWriter();
    Per.writeChoice(w, MCS_ATTACH_USER_REQUEST << 2);
    return w.toArray();
}
function mcsChannelJoinSerialize(initiator, channelId) {
    const w = new ByteWriter();
    Per.writeChoice(w, MCS_CHANNEL_JOIN_REQUEST << 2);
    Per.writeInteger16(w, initiator, 1001);
    Per.writeInteger16(w, channelId, 0);
    return w.toArray();
}
function mcsSendDataSerialize(initiator, channelId, data) {
    const w = new ByteWriter();
    Per.writeChoice(w, MCS_SEND_DATA_REQUEST << 2);
    Per.writeInteger16(w, initiator, 1001);
    Per.writeInteger16(w, channelId, 0);
    w.u8(0x70); // dataPriority + segmentation magic
    Per.writeLength(w, data.length);
    w.bytes(data);
    return w.toArray();
}

// ================================================================================================
// TPKT / X.224 framing
// ================================================================================================
function tpktX224Wrap(userData) {
    // X.224 Data TPDU header: LI=2, code=0xF0 (DT_DATA), EOT=0x80
    const total = 4 /*tpkt*/ + 3 /*x224 data*/ + userData.length;
    const w = new ByteWriter();
    w.u8(0x03).u8(0x00).u16be(total);   // TPKT
    w.u8(0x02).u8(0xf0).u8(0x80);       // X.224 data
    w.bytes(userData);
    return w.toArray();
}

// ================================================================================================
// Client user data (CS_CORE / CS_SECURITY / CS_NET) for the basic settings exchange
// ================================================================================================
function clientCoreData(selectedProtocol, width, height, desktopScaleFactor, keyboardLayout) {
    var gfx = rdpTryGfx();
    const w = new ByteWriter();
    w.u16le(0xC001); // CS_CORE
    w.u16le(gfx ? 234 : 216); // length (GFX path appends the 18-byte optional scale tail below)
    w.u32le(0x00080005); // version — match the working macOS Remote Desktop app's CS_CORE (was 0x00080011)
    w.u16le(width);
    w.u16le(height);
    w.u16le(0xCA01); // colorDepth RNS_UD_COLOR_8BPP
    w.u16le(0xAA03); // SASSequence
    w.u32le((keyboardLayout >>> 0) || 0x00000409); // keyboardLayout (Windows KLID; detected from the browser)
    w.u32le(18363);  // clientBuild (Windows 10 1909, matching modern clients)
    // clientName[32] (UTF-16LE, padded)
    const nameW = new ByteWriter().utf16le(PROJECT_NAME).toArray();
    const name32 = new Uint8Array(32);
    name32.set(nameW.subarray(0, Math.min(nameW.length, 32)));
    w.bytes(name32);
    w.u32le(0x00000004); // keyboardType
    w.u32le(0x00000000); // keyboardSubType
    w.u32le(12);         // keyboardFunctionKey
    w.zeros(64);         // imeFileName[64]
    w.u16le(0xCA01);     // postBeta2ColorDepth — match macOS app (was 0xCA03)
    w.u16le(0x0001);     // clientProductId
    w.u32le(0x00000000); // serialNumber
    // highColorDepth / supportedColorDepths: when advertising GFX we mirror FreeRDP's working values
    // (24bpp high color, all depths supported). The no-GFX baseline keeps the conservative 16bpp set.
    w.u16le(gfx ? 0x0018 : 0x0010);     // highColorDepth: HIGH_COLOR_24BPP vs 16BPP
    w.u16le(gfx ? 0x000F : 0x0002);     // supportedColorDepths: 15/16/24/32 vs 16BPP only
    // earlyCapabilityFlags ([MS-RDPBCGR] 2.2.1.3.2).
    // 0x7AF = ERRINFO_PDU(0x01) | WANT_32BPP(0x02) | STATUSINFO_PDU(0x04) | STRONG_ASYMMETRIC_KEYS(0x08)
    //       | VALID_CONNECTION_TYPE(0x20) | NETCHAR_AUTODETECT(0x80) | DYNVC_GFX(0x100)
    //       | DYNAMIC_TIME_ZONE(0x200) | HEARTBEAT(0x400).
    // NETCHAR_AUTODETECT(0x80) + HEARTBEAT(0x400) make a FreeRDP host (GNOME Remote Desktop) run the
    // connect-time Network Auto-Detect exchange over the MCS message channel. We support that: the
    // client requests CS_MCS_MSGCHANNEL, joins the granted channel, and answers auto-detect requests on
    // it (see _handleAutoDetect / _onAutoDetectRequest). We do NOT set SUPPORT_MONITOR_LAYOUT_PDU(0x40)
    // (suspected trigger for a 2nd RESET_GRAPHICS). DYNVC_GFX(0x100) + prereqs stay set.
    w.u16le(gfx ? 0x07AF : 0x0001);
    w.zeros(64);         // clientDigProductId[64]
    // connectionType: MUST be 0 unless VALID_CONNECTION_TYPE is set. The macOS app sends 0x07
    // (CONNECTION_TYPE_LAN); match it (was 0x06 AUTODETECT). VALID_CONNECTION_TYPE (0x20) is set.
    w.u8(gfx ? 0x07 : 0x00);
    w.u8(0x00);          // pad
    w.u32le(selectedProtocol >>> 0); // serverSelectedProtocol
    // Optional TS_UD_CS_CORE tail ([MS-RDPBCGR] 2.2.1.3.2), GFX path only (keeps the no-GFX Connect
    // Initial byte-identical to the known-good baseline). This is what makes the session START at the
    // display's HiDPI scale: GNOME Remote Desktop builds its INITIAL virtual-monitor config from these
    // CS_CORE fields (grd-rdp-monitor-config.c create_monitor_config_from_client_core_data reads
    // FreeRDP_DesktopScaleFactor), and Windows applies them as the connect-time session DPI. Without
    // the tail the session always comes up at 100% and the RDPEDISP initial-scale layout has to win a
    // race to fix it after the fact.
    if (gfx) {
        w.u32le(0);      // desktopPhysicalWidth (mm; 0 = unknown, host ignores)
        w.u32le(0);      // desktopPhysicalHeight
        w.u16le(0);      // desktopOrientation (ORIENTATION_LANDSCAPE)
        w.u32le(Math.max(100, Math.min(500, desktopScaleFactor || 100))); // desktopScaleFactor
        w.u32le(100);    // deviceScaleFactor: MUST be 100/140/180; all DPI rides in desktopScaleFactor
    }
    return w.toArray();
}
function clientSecurityData() {
    const w = new ByteWriter();
    w.u16le(0xC002); // CS_SECURITY
    w.u16le(12);
    w.u32le(0); // encryptionMethods
    w.u32le(0); // extEncryptionMethods
    return w.toArray();
}
// CHANNEL_OPTION_* ([MS-RDPBCGR] 2.2.1.3.4.1).
const CHANNEL_OPTION_INITIALIZED = 0x80000000;
const CHANNEL_OPTION_ENCRYPT_RDP = 0x40000000;
const CHANNEL_OPTION_COMPRESS_RDP = 0x00800000;
const CHANNEL_OPTION_SHOW_PROTOCOL = 0x00200000;

// The static virtual channels we request, in order. The server returns one MCS channel id per entry
// (positionally) in SC_NET. Each entry's `options` is the CHANNEL_OPTION_* flags for that channel.
//   - "drdynvc": dynamic virtual channels (MS-RDPEDYC) → Display Control (MS-RDPEDISP) live resize.
//   - "rdpsnd":  remote audio output (MS-RDPEA).
//   - "cliprdr": clipboard redirection (MS-RDPECLIP).
// The set is built per-connection (see RdpProtocol options audio/clipboard) so we only advertise the
// channels we actually service — advertising a channel we don't answer can stall the host.
// Channel options match the working macOS Remote Desktop app EXACTLY (decoded from its CS_NET):
//   rdpdr=0x80800000 (INITIALIZED|COMPRESS_RDP), rdpsnd=0xc0000000 (INITIALIZED|ENCRYPT_RDP),
//   cliprdr=0xc0a00000 (INITIALIZED|ENCRYPT_RDP|COMPRESS_RDP|SHOW_PROTOCOL),
//   drdynvc=0xc0800000 (INITIALIZED|ENCRYPT_RDP|COMPRESS_RDP).
const CHANNEL_DEFS = {
    drdynvc: { options: CHANNEL_OPTION_INITIALIZED | CHANNEL_OPTION_ENCRYPT_RDP | CHANNEL_OPTION_COMPRESS_RDP },
    rdpsnd: { options: CHANNEL_OPTION_INITIALIZED | CHANNEL_OPTION_ENCRYPT_RDP },
    cliprdr: { options: CHANNEL_OPTION_INITIALIZED | CHANNEL_OPTION_ENCRYPT_RDP | CHANNEL_OPTION_COMPRESS_RDP | CHANNEL_OPTION_SHOW_PROTOCOL },
    // rdpdr (device redirection, MS-RDPEFS): FreeRDP advertises this with /sound, and the host appears
    // to gate the AUDIO_PLAYBACK_DVC dynamic channel (where modern audio rides) on its presence.
    rdpdr: { options: CHANNEL_OPTION_INITIALIZED | CHANNEL_OPTION_COMPRESS_RDP },
};

function clientNetworkData(channels) {
    const w = new ByteWriter();
    const count = channels.length;
    w.u16le(0xC003);            // CS_NET
    w.u16le(8 + count * 12);    // header(4) + channelCount(4) + count*ChannelDef(12)
    w.u32le(count);             // channelCount
    for (const name of channels) {
        // ChannelDef: 7 ANSI chars + NUL (8 bytes) then options(4)
        const nameBytes = new Uint8Array(8);
        for (let i = 0; i < name.length && i < 7; i++) nameBytes[i] = name.charCodeAt(i) & 0xff;
        w.bytes(nameBytes);
        w.u32le((CHANNEL_DEFS[name] ? CHANNEL_DEFS[name].options : CHANNEL_OPTION_INITIALIZED) >>> 0);
    }
    return w.toArray();
}
// NOTE: we deliberately DO NOT send CS_MCS_MSGCHANNEL (0xC006) or CS_MULTITRANSPORT (0xC00A).
// A byte-diff of our Connect Initial vs a working mstsc session against the SAME host showed mstsc
// sends NEITHER block (it sets the GFX early-cap flag and streams 3.6MB fine) — so they are NOT
// required for the GFX path, contrary to an earlier note here. Worse, sending CS_MCS_MSGCHANNEL makes
// the host GRANT a message channel (SC_MCS_MSGCHANNEL → MCS channel 1007) that must then be JOINED
// ([MS-RDPBCGR] 3.2.5.3.2/3.2.5.3.3) and serviced for network auto-detect; a half-finished message
// channel made the host throttle GFX to ~nothing after ~3 frames (the long-standing stall). Matching
// mstsc — omit both blocks, don't set anything that opens a channel we won't service — is the fix.
// (CS_MULTITRANSPORT is only for the RDP-UDP multitransport layer [MS-RDPEMT], which the TCP-only
// relay cannot carry anyway.) Keep clientMcsMsgChannelData/clientMultitransportData defined but unused
// in case a future host genuinely requires them; gate behind a flag if that ever resurfaces.
// CS_CLUSTER (0xC004): FreeRDP sends this unconditionally with flags = 0x0D (verified by wire dump) =
// REDIRECTION_SUPPORTED(0x01) | (REDIRECTION_VERSION4 (3) << 2 = 0x0C). redirectedSessionId 0.
function clientClusterData() {
    const w = new ByteWriter();
    w.u16le(0xC004); // CS_CLUSTER
    w.u16le(12);     // length
    w.u32le(0x00000001 | (0x03 << 2)); // REDIRECTION_SUPPORTED | REDIRECTION_VERSION4<<2 = 0x0D
    w.u32le(0);      // redirectedSessionId
    return w.toArray();
}
function clientMcsMsgChannelData() {
    const w = new ByteWriter();
    w.u16le(0xC006); // CS_MCS_MSGCHANNEL
    w.u16le(8);      // length
    w.u32le(0);      // flags
    return w.toArray();
}
function clientMultitransportData() {
    const w = new ByteWriter();
    w.u16le(0xC00A); // CS_MULTITRANSPORT
    w.u16le(8);      // length
    // Match FreeRDP's default (TRANSPORT_TYPE_UDP_FECR = 0x01). We never actually establish a UDP
    // side-channel — so we advertise flags=0 (no UDP transports), exactly matching the verified
    // FreeRDP wire dump. Advertising a UDP transport we can't honor risks the host attempting UDP
    // setup over our TCP-only relay; flags=0 keeps the block structurally present without that.
    w.u32le(0x00000000);
    return w.toArray();
}

function clientUserData(selectedProtocol, width, height, channels, desktopScaleFactor, keyboardLayout) {
    const w = new ByteWriter();
    w.bytes(clientCoreData(selectedProtocol, width, height, desktopScaleFactor, keyboardLayout));
    // Extra GCC blocks for the GFX/extended-client-data path are gated behind the same test toggle so
    // the default (no-GFX) Connect Initial stays byte-identical to the known-good baseline.
    if (rdpTryGfx()) w.bytes(clientClusterData());
    w.bytes(clientSecurityData());
    w.bytes(clientNetworkData(channels));
    // CS_MCS_MSGCHANNEL: request the MCS message channel so the host's connect-time Network Auto-Detect
    // (we advertise NETCHAR_AUTODETECT) has a channel to run on. The granted channel is joined in
    // _onAttachUserConfirm and auto-detect requests are answered on it (_handleAutoDetect). Without it a
    // FreeRDP host sends auto-detect on the I/O channel and our reply can't be routed back, aborting the
    // session. Only sent on the GFX/extended path (the no-GFX baseline stays byte-identical to before).
    // (CS_MULTITRANSPORT stays omitted — it's RDP-UDP multitransport, which the TCP-only relay can't carry.)
    if (rdpTryGfx()) w.bytes(clientMcsMsgChannelData());
    return w.toArray();
}

// Parse the server user data (after GCC) to recover the global (I/O) MCS channel id and the
// SC_CORE earlyCapabilityFlags (for RNS_UD_SC_SKIP_CHANNELJOIN_SUPPORTED).
function parseServerUserData(r) {
    const out = { mcsChannelId: 1003, skipChannelJoin: false, channelIds: [], msgChannelId: 0 };
    while (r.remaining() >= 4) {
        const dataType = r.u16le();
        let dataLen = r.u16le();
        const body = dataLen - 4;
        const end = r.o + body;
        switch (dataType) {
            case 0x0C01: { // SC_CORE
                /* version */ r.u32le();
                if (body >= 8) {
                    /* clientRequestedProtocols */ r.u32le();
                }
                if (body >= 12) {
                    const earlyCaps = r.u32le();
                    out.skipChannelJoin = (earlyCaps & 0x8) === 0x8;
                }
                break;
            }
            case 0x0C03: { // SC_NET
                out.mcsChannelId = r.u16le();         // global (I/O) channel id
                const channelCount = r.u16le();
                // One id per requested static virtual channel, positionally matching staticChannels.
                for (let i = 0; i < channelCount; i++) out.channelIds.push(r.u16le());
                break;
            }
            case 0x0C04: { // SC_MCS_MSGCHANNEL ([MS-RDPBCGR] 2.2.1.4.5): MCSChannelId (2 bytes).
                // The server grants a message channel because we advertised CS_MCS_MSGCHANNEL. Per
                // 3.2.5.3.3 the client MUST join this channel during the channel-join phase — otherwise
                // the host's connection sequence never completes the message channel and it gates
                // network auto-detect (we set NETCHAR_AUTODETECT), throttling GFX bandwidth to ~nothing
                // after the first few frames. Capture it so _onAttachUserConfirm joins it.
                if (body >= 2) out.msgChannelId = r.u16le();
                break;
            }
            // SC_SECURITY (0x0C02), SC_MULTITRANSPORT (0x0C08): skipped.
            default: break;
        }
        r.o = end; // jump to the next user data header regardless of how much we consumed
    }
    return out;
}

// ================================================================================================
// Share headers, client info, finalization, capabilities
// ================================================================================================
const PDUTYPE_DEMANDACTIVE = 0x11;
const PDUTYPE_CONFIRMACTIVE = 0x13;
const PDUTYPE_DEACTIVATEALL = 0x16;
const PDUTYPE_DATAPDU = 0x17;

const PDUTYPE2_UPDATE = 0x02;
const PDUTYPE2_CONTROL = 0x14;
const PDUTYPE2_REFRESH_RECT = 0x21;
const PDUTYPE2_SYNCHRONIZE = 0x1F;
const PDUTYPE2_FONTLIST = 0x27;
const PDUTYPE2_FONTMAP = 0x28;
const PDUTYPE2_SET_ERROR_INFO_PDU = 0x2f;

// TS_SET_ERROR_INFO_PDU error codes ([MS-RDPBCGR] 2.2.5.1.1), mapped to a user-facing description.
// The low codes (< 0x10000) are graceful session-end reasons the host sends just before it tears the
// session down (user logoff, admin disconnect, timeout); the higher codes are genuine protocol/license
// errors. We surface a description for EVERY code — and always append the raw hex code — so an
// unrecognized value still reads sensibly (e.g. "Remote desktop error (0x1234).").
const ERROR_INFO_DESC = {
    0x00000001: "Disconnected by the server.",                 // RPC_INITIATED_DISCONNECT
    0x00000002: "Signed out by the server.",                   // RPC_INITIATED_LOGOFF
    0x00000003: "Idle timeout reached.",                       // IDLE_TIMEOUT
    0x00000004: "Session time limit reached.",                 // LOGON_TIMEOUT
    0x00000005: "Disconnected by another connection.",         // DISCONNECTED_BY_OTHERCONNECTION
    0x00000006: "The server ran out of memory.",               // OUT_OF_MEMORY
    0x00000007: "The server denied the connection.",           // SERVER_DENIED_CONNECTION
    0x00000009: "Insufficient access privileges.",             // SERVER_INSUFFICIENT_PRIVILEGES
    0x0000000A: "The server refused fresh credentials.",       // SERVER_FRESH_CREDENTIALS_REQUIRED
    0x0000000B: "You disconnected from the session.",          // RPC_INITIATED_DISCONNECT_BYUSER
    0x0000000C: "You were signed out.",                        // LOGOFF_BY_USER
    0x00000010: "The connection was replaced.",                // REPLACED_BY_OTHER_CONNECTION
    0x00000011: "The server changed graphics mode.",           // OUT_OF_MEMORY (gfx) — host-specific
    // Licensing failures (TS_SET_ERROR_INFO licensing range).
    0x00000100: "A licensing error occurred.",                 // LICENSE_INTERNAL
    0x00000101: "No license server is available.",             // LICENSE_NO_LICENSE_SERVER
    0x00000102: "No license is available.",                    // LICENSE_NO_LICENSE
    0x00000103: "An invalid licensing message was received.",  // LICENSE_BAD_CLIENT_MSG
    0x00000104: "The license store is full.",                  // LICENSE_HWID_DOESNT_MATCH_LICENSE
    0x00000105: "The client license is invalid.",              // LICENSE_BAD_CLIENT_LICENSE
    0x00000106: "Licensing could not complete.",               // LICENSE_CANT_FINISH_PROTOCOL
    0x00000107: "An unexpected licensing message was received.", // LICENSE_CLIENT_ENDED_PROTOCOL
    0x00000108: "An invalid client licensing message was received.", // LICENSE_BAD_CLIENT_ENCRYPTION
    0x00000109: "Licensing was not negotiated correctly.",     // LICENSE_CANT_UPGRADE_LICENSE
    0x0000010A: "Too many users are connected to the server.", // LICENSE_NO_REMOTE_CONNECTIONS
};

function errorInfoReason(code) {
    if (code === 0x00000000) return null; // ERRINFO_NONE — host clearing a prior error, not a disconnect
    const desc = ERROR_INFO_DESC[code] || "Remote desktop error";
    const hex = "0x" + code.toString(16).toUpperCase();
    // Codes below the protocol-error range are normal session-end reasons → graceful close (UI returns
    // to the login form, not a red error). Higher codes are genuine errors.
    const graceful = code < 0x00001000;
    return { graceful: graceful, message: desc + " (" + hex + ")" };
}

// Builds a TS_SHAREDATAHEADER + data body (PDUTYPE_DATAPDU) for finalization PDUs.
function shareDataPdu(shareID, userId, pduType2, body) {
    const totalLength = 18 + body.length;
    const w = new ByteWriter();
    // ShareControlHeader
    w.u16le(totalLength);
    w.u16le(PDUTYPE_DATAPDU);
    w.u16le(userId);
    // ShareDataHeader
    w.u32le(shareID >>> 0);
    w.u8(0);          // padding
    w.u8(0x01);       // streamID STREAM_LOW
    w.u16le(4 + body.length); // uncompressedLength
    w.u8(pduType2);
    w.u8(0);          // compressedType
    w.u16le(0);       // compressedLength
    w.bytes(body);
    return w.toArray();
}

function synchronizePdu(shareID, userId) {
    const b = new ByteWriter();
    b.u16le(1);     // MessageType = SYNCMSGTYPE_SYNC
    b.u16le(1002);  // targetUser = ServerChannelID
    return shareDataPdu(shareID, userId, PDUTYPE2_SYNCHRONIZE, b.toArray());
}
const CTRLACTION_REQUEST_CONTROL = 0x0001;
const CTRLACTION_GRANTED_CONTROL = 0x0002;
const CTRLACTION_COOPERATE = 0x0004;
function controlPdu(shareID, userId, action) {
    const b = new ByteWriter();
    b.u16le(action);
    b.u16le(0); // grantId
    b.u32le(0); // controlId
    return shareDataPdu(shareID, userId, PDUTYPE2_CONTROL, b.toArray());
}
function fontListPdu(shareID, userId) {
    const b = new ByteWriter();
    b.u16le(0x0000); // numberFonts
    b.u16le(0x0000); // totalNumFonts
    b.u16le(0x0003); // listFlags FONTLIST_FIRST|FONTLIST_LAST
    b.u16le(0x0032); // entrySize
    return shareDataPdu(shareID, userId, PDUTYPE2_FONTLIST, b.toArray());
}

// TS_REFRESH_RECT_PDU ([MS-RDPBCGR] 2.2.11.2): asks the server to resend graphics for the given
// rectangles. We send one full-desktop rect after an in-place resolution change so the server repaints
// the whole (cleared) canvas at the new size, instead of only streaming incremental deltas.
function refreshRectPdu(shareID, userId, width, height) {
    const b = new ByteWriter();
    b.u8(1);          // numberOfAreas
    b.zeros(3);       // pad3Octets
    // TS_RECTANGLE16 (inclusive coords): left, top, right, bottom.
    b.u16le(0);
    b.u16le(0);
    b.u16le(Math.max(0, width - 1));
    b.u16le(Math.max(0, height - 1));
    return shareDataPdu(shareID, userId, PDUTYPE2_REFRESH_RECT, b.toArray());
}

// ---- client info PDU (auto-logon credentials) ----------------------------------------------------
const INFO_MOUSE = 0x00000001;
const INFO_DISABLECTRLALTDEL = 0x00000002;
const INFO_AUTOLOGON = 0x00000008;
const INFO_UNICODE = 0x00000010;
const INFO_MAXIMIZESHELL = 0x00000020;
const INFO_LOGONNOTIFY = 0x00000040;
const INFO_ENABLEWINDOWSKEY = 0x00000100;
const INFO_MOUSE_HAS_WHEEL = 0x00020000;
// INFO_VIDEO_DISABLE ([MS-RDPBCGR] 2.2.1.11.1.1, 0x00400000): tells the host NOT to use the MS-RDPEVOR
// video-optimized-remoting pipeline (Video::Control / Video::Data DVCs). The working macOS Remote Desktop
// app SETS this; we did NOT. Without it the host offers the Video DVCs (which we REJECT, since we don't
// implement EVOR), and on this host that mismatch is the suspected cause of the 2nd RESET_GRAPHICS + GFX
// stall — the host keeps trying to route video through a pipeline we refuse, then re-inits the surface and
// stops. With VIDEO_DISABLE the host never offers video remoting and just floods H.264 over GFX (as it
// does for the macOS app). This is the closest match yet to "stream it like the macOS app".
const INFO_VIDEO_DISABLE = 0x00400000;
const INFO_COMPRESSION = 0x00000080;
const INFO_FORCE_ENCRYPTED_CS_PDU = 0x00004000;
const INFO_LOGONERRORS = 0x00010000;
const INFO_USING_SAVED_CREDS = 0x00100000;
const PACKET_COMPR_TYPE_RDP6 = 0x02; // goes in infoFlags bits 9-12 (the 0x1E00 compressionType mask)

// The macOS Remote Desktop app's ExtendedInfoPacket, captured byte-for-byte from a MITM c2s dump so our
// Client Info PDU matches the working client 1:1 (clientAddress 127.0.0.1, W. Europe Standard Time zone,
// clientSessionId 0xFFFE, performanceFlags 0xC6, and the trailing dynamic-time-zone/region blob).
const MACOS_CLIENT_EXTENDED_INFO = Uint8Array.from([
    0x04,0x00,0x14,0x00,0x31,0x00,0x32,0x00,0x37,0x00,0x2e,0x00,0x30,0x00,0x2e,0x00,
    0x30,0x00,0x2e,0x00,0x31,0x00,0x00,0x00,0x00,0x00,0xc4,0xff,0xff,0xff,0x57,0x00,
    0x2e,0x00,0x20,0x00,0x45,0x00,0x75,0x00,0x72,0x00,0x6f,0x00,0x70,0x00,0x65,0x00,
    0x20,0x00,0x53,0x00,0x74,0x00,0x61,0x00,0x6e,0x00,0x64,0x00,0x61,0x00,0x72,0x00,
    0x64,0x00,0x20,0x00,0x54,0x00,0x69,0x00,0x6d,0x00,0x65,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x0a,0x00,0x00,0x00,
    0x04,0x00,0x01,0x00,0x3b,0x00,0x3b,0x00,0x3b,0x00,0x00,0x00,0x00,0x00,0x57,0x00,
    0x2e,0x00,0x20,0x00,0x45,0x00,0x75,0x00,0x72,0x00,0x6f,0x00,0x70,0x00,0x65,0x00,
    0x20,0x00,0x44,0x00,0x61,0x00,0x79,0x00,0x6c,0x00,0x69,0x00,0x67,0x00,0x68,0x00,
    0x74,0x00,0x20,0x00,0x54,0x00,0x69,0x00,0x6d,0x00,0x65,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x03,0x00,0x00,0x00,
    0x05,0x00,0x02,0x00,0x3b,0x00,0x3b,0x00,0x3b,0x00,0xc4,0xff,0xff,0xff,0xfe,0xff,
    0x00,0x00,0xc6,0x00,0x00,0x00,0x00,0x00,0x64,0x00,0x00,0x00,0x30,0x00,0x57,0x00,
    0x2e,0x00,0x20,0x00,0x45,0x00,0x75,0x00,0x72,0x00,0x6f,0x00,0x70,0x00,0x65,0x00,
    0x20,0x00,0x53,0x00,0x74,0x00,0x61,0x00,0x6e,0x00,0x64,0x00,0x61,0x00,0x72,0x00,
    0x64,0x00,0x20,0x00,0x54,0x00,0x69,0x00,0x6d,0x00,0x65,0x00,0x00,0x00,0x00,0x00,
]);
// INFO_AUDIOCAPTURE ([MS-RDPBCGR] 2.2.1.11.1.1, flag 0x00200000): tells the server the CLIENT wants
// audio-input (microphone) redirection. The host only opens the AUDIO_INPUT dynamic channel when this
// bit is set in the Client Info PDU — without it the SNDIN handshake never starts (this is what the
// Windows mstsc client sets when mic redirection is enabled; it is the gate, NOT a host group policy).
const INFO_AUDIOCAPTURE = 0x00200000;

// Performance flags ([MS-RDPBCGR] 2.2.1.11.1.1.1, the ExtendedInfoPacket performanceFlags field).
// The DISABLE_* bits turn OFF the named eye-candy to save bandwidth; the ENABLE_* bits turn ON
// quality features. "Better visuals" = enable font smoothing (ClearType anti-aliasing) + desktop
// composition (Aero/DWM) and disable nothing. Exposed so the UI can offer them individually.
const PERF = {
    DISABLE_WALLPAPER: 0x00000001,
    DISABLE_FULLWINDOWDRAG: 0x00000002,
    DISABLE_MENUANIMATIONS: 0x00000004,
    DISABLE_THEMING: 0x00000008,
    DISABLE_CURSOR_SHADOW: 0x00000020,
    DISABLE_CURSORSETTINGS: 0x00000040,
    ENABLE_FONT_SMOOTHING: 0x00000080,
    ENABLE_DESKTOP_COMPOSITION: 0x00000100,
};
// Default: best visual fidelity (font anti-aliasing + Aero composition, full eye-candy).
const PERF_DEFAULT = PERF.ENABLE_FONT_SMOOTHING | PERF.ENABLE_DESKTOP_COMPOSITION;
// Surface the flag table to the page UI (which builds the performanceFlags value from checkboxes).
if (typeof window !== "undefined") { window.RDP_PERF = PERF; window.RDP_PERF_DEFAULT = PERF_DEFAULT; }

function clientInfoPdu(domain, username, password, performanceFlags, audioCapture) {
    function field(s) {
        if (s && s.length > 0) {
            const w = new ByteWriter().utf16le(s).toArray(); // no NUL
            return { bytes: w, cb: w.length };
        }
        return { bytes: new Uint8Array(0), cb: 0 };
    }
    const d = field(domain || "");
    const u = field(username || "");
    const p = field(password || "");

    const info = new ByteWriter();
    info.u32le(0); // codePage
    // ISOLATION (2026-06-16): the full macOS-matching flag set + macOS extended-info blob made THIS host
    // RESET right after Client Info. Reverted to the known-good baseline + ONLY INFO_VIDEO_DISABLE (the
    // one flag with a clear GFX-streaming rationale: keeps the host off the MS-RDPEVOR video pipeline whose
    // Video DVCs we reject). Re-add the others (MAXIMIZESHELL/LOGONNOTIFY/MOUSE_HAS_WHEEL/FORCE_ENCRYPTED/
    // LOGONERRORS/USING_SAVED_CREDS/COMPRESSION) ONE AT A TIME once this connects, to find which the host
    // rejects. The macOS extended-info blob is likewise reverted to the minimal baseline.
    // INFO_COMPRESSION tested: it CONNECTS and changes host behavior (host then compresses LEGACY fastpath
    // updates → "skipping fastpath PDU: offset outside bounds" because we don't decompress them) but the
    // GFX DVC stream is UNAFFECTED — same 2-frames → RESET → 2-frames → stall. So COMPRESSION drives the
    // legacy path, NOT the GFX frame format. Reverted (adds a fastpath-decompress burden without fixing the
    // stall). Keep baseline + VIDEO_DISABLE.
    let infoFlags = INFO_MOUSE | INFO_UNICODE | INFO_AUTOLOGON | INFO_DISABLECTRLALTDEL | INFO_ENABLEWINDOWSKEY
        | INFO_VIDEO_DISABLE;
    if (audioCapture) infoFlags |= INFO_AUDIOCAPTURE; // request microphone redirection (gates AUDIO_INPUT DVC)
    info.u32le(infoFlags);
    info.u16le(d.cb);
    info.u16le(u.cb);
    info.u16le(p.cb);
    info.u16le(0); // cbAlternateShell
    info.u16le(0); // cbWorkingDir
    // each string is written with a trailing UTF-16 NUL terminator
    info.bytes(d.bytes).u16le(0);
    info.bytes(u.bytes).u16le(0);
    info.bytes(p.bytes).u16le(0);
    info.u16le(0); // alternateShell NUL
    info.u16le(0); // workingDir NUL
    // ExtendedInfoPacket (baseline minimal form — reverted from the macOS blob during isolation).
    info.u16le(0x0002); // clientAddressFamily AF_INET
    info.u16le(2); info.u16le(0); // cbClientAddress + address
    info.u16le(2); info.u16le(0); // cbClientDir + dir
    info.zeros(172); // clientTimeZone
    info.u32le(0);   // clientSessionId
    info.u32le((performanceFlags >>> 0)); // performanceFlags

    // Security header: SEC_INFO_PKT (0x0040), flagsHi 0
    const w = new ByteWriter();
    w.u16le(0x0040);
    w.u16le(0x0000);
    w.bytes(info.toArray());
    return w.toArray();
}

// ---- capability sets for the Confirm Active PDU --------------------------------------------------
function capSet(type, data) {
    const w = new ByteWriter();
    w.u16le(type);
    w.u16le(4 + data.length);
    w.bytes(data);
    return w.toArray();
}
function capGeneral() {
    const d = new ByteWriter();
    d.u16le(0x0008); // osMajorType (Chrome OS placeholder)
    d.u16le(0x0000); // osMinorType
    d.u16le(0x0200); // protocolVersion
    d.u16le(0x0000); // padding
    d.u16le(0x0000); // compressionTypes
    d.u16le(0x0001 | 0x0004 | 0x0400); // extraFlags FASTPATH_OUTPUT|LONG_CREDENTIALS|NO_BITMAP_COMPRESSION_HDR
    d.u16le(0x0000); // updateCapabilityFlag
    d.u16le(0x0000); // remoteUnshareFlag
    d.u16le(0x0000); // compressionLevel
    d.u8(0);         // refreshRectSupport
    d.u8(0);         // suppressOutputSupport
    return capSet(0x0001, d.toArray());
}
function capBitmap(width, height) {
    const d = new ByteWriter();
    // 16bpp to match the client core HighColorDepth and the 16bpp (565) RLE decoder + color.js. A
    // mismatch here makes the server send a pixel format the wasm RLE decoder can't decode → black.
    d.u16le(0x0010); // preferredBitsPerPixel HIGH_COLOR_16BPP
    d.u16le(0x0001); d.u16le(0x0001); d.u16le(0x0001); // receive1/4/8 BitsPerPixel
    d.u16le(width); d.u16le(height);
    d.u16le(0);      // padding
    // desktopResizeFlag = 1: we DO support desktop resizes (the Deactivation-Reactivation Sequence,
    // handled in _beginReactivation / _onDemandActiveControl). FreeRDP and mstsc both advertise this.
    // With it 0, the server thinks the client can't be resized: it best-effort applies the FIRST
    // MONITOR_LAYOUT in place but silently DROPS every subsequent one (the second-resize-goes-black
    // bug) because it would otherwise have to reactivate, which we told it we can't handle.
    d.u16le(1);      // desktopResizeFlag
    d.u16le(0x0001); // bitmapCompressionFlag
    d.u8(0);         // highColorFlags
    d.u8(0);         // drawingFlags
    d.u16le(0x0001); // multipleRectangleSupport
    d.u16le(0);      // padding
    return capSet(0x0002, d.toArray());
}
function capOrder() {
    const d = new ByteWriter();
    d.zeros(16);        // terminalDescriptor
    d.u32le(0);         // padding
    d.u16le(1);         // desktopSaveXGranularity
    d.u16le(20);        // desktopSaveYGranularity
    d.u16le(0);         // padding
    d.u16le(1);         // maximumOrderLevel ORD_LEVEL_1_ORDERS
    d.u16le(0);         // numberFonts
    d.u16le(0x2 | 0x0008); // orderFlags NEGOTIATEORDERSUPPORT|ZEROBOUNDSDELTASSUPPORT
    d.zeros(32);        // orderSupport[32]
    d.u16le(0);         // textFlags
    d.u16le(0);         // orderSupportExFlags
    d.u32le(0);         // padding
    d.u32le(480 * 480); // desktopSaveSize
    d.u32le(0);         // padding
    d.u16le(0);         // textANSICodePage
    d.u16le(0);         // padding
    return capSet(0x0003, d.toArray());
}
function capBitmapCacheRev1() {
    const d = new ByteWriter();
    d.zeros(24);                  // padding
    for (let i = 0; i < 6; i++) d.u16le(0); // three cache entry/cellsize pairs
    return capSet(0x0004, d.toArray());
}
function capPointer() {
    const d = new ByteWriter();
    d.u16le(1);  // colorPointerFlag
    d.u16le(0);  // colorPointerCacheSize
    d.u16le(25); // pointerCacheSize
    return capSet(0x0008, d.toArray());
}
function capInput(keyboardLayout) {
    const d = new ByteWriter();
    d.u16le(0x0001 | 0x0004 | 0x0010 | 0x0020); // SCANCODES|MOUSEX|UNICODE|FASTPATH_INPUT2
    d.u16le(0);          // padding
    d.u32le((keyboardLayout >>> 0) || 0x00000409); // keyboardLayout (same KLID as CS_CORE)
    d.u32le(0x00000004); // keyboardType
    d.u32le(0);          // keyboardSubType
    d.u32le(12);         // keyboardFunctionKey
    d.zeros(64);         // imeFileName
    return capSet(0x000D, d.toArray());
}
function capBrush() { return capSet(0x000F, new ByteWriter().u32le(0).toArray()); }
function capGlyphCache() {
    const d = new ByteWriter();
    for (let i = 0; i < 10; i++) { d.u16le(0); d.u16le(0); } // glyphCache[10]
    d.u32le(0); // fragCache
    d.u16le(0); // glyphSupportLevel
    d.u16le(0); // padding
    return capSet(0x0010, d.toArray());
}
function capOffscreen() {
    const d = new ByteWriter();
    d.u32le(0); d.u16le(0); d.u16le(0);
    return capSet(0x0011, d.toArray());
}
function capVirtualChannel() {
    const d = new ByteWriter();
    d.u32le(0); d.u32le(0);
    return capSet(0x0014, d.toArray());
}
function capSound() {
    const d = new ByteWriter();
    d.u16le(0); d.u16le(0);
    return capSet(0x000C, d.toArray());
}
function capMultifragmentUpdate() {
    // MaxRequestSize — the macOS app sends 0x0009482B (~608 KB). We had 0, which tells the host we can't
    // receive large multi-fragment updates (i.e. continuous H.264 surface frames). Match the macOS value.
    return capSet(0x001A, new ByteWriter().u32le(0x0009482B).toArray());
}
// BUG FIX (found via MITM diff vs mstsc — see tools/rdpmitm + parse_caps.py): our GFX-gating capsets
// were each typed ONE NUMBER TOO HIGH. Per [MS-RDPBCGR] the correct types are LARGE_POINTER 0x1B,
// SURFACE_COMMANDS 0x1C, BITMAP_CODECS 0x1D, and 0x1E is CAPSSETTYPE_FRAME_ACKNOWLEDGE ([MS-RDPRFX]
// 2.2.1.3) — which we never sent. The host was reading our LargePointer body AS SurfaceCommands, our
// SurfaceCommands body AS BitmapCodecs, and our BitmapCodecs body AS FrameAcknowledge: it never saw a
// valid SURFACE_COMMANDS cap (the GFX surface-pipeline gate) and mis-parsed FRAME_ACKNOWLEDGE. That is
// why the host streamed a few GFX frames then stopped. Bodies below are byte-for-byte what mstsc sends.

// LARGE_POINTER ([MS-RDPBCGR] 2.2.7.2.7, type 0x001B): largePointerSupportFlags = 0x0003
// (LARGE_POINTER_FLAG_96x96 | LARGE_POINTER_FLAG_384x384).
function capLargePointer() {
    return capSet(0x001B, new ByteWriter().u16le(0x0003).toArray());
}
// SURFACE_COMMANDS ([MS-RDPBCGR] 2.2.7.2.9, type 0x001C) — REQUIRED for the GFX surface pipeline.
// cmdFlags = 0x12 (SETSURFACEBITS | FRAMEMARKER), reserved(4)=0.
function capSurfaceCommands() {
    return capSet(0x001C, new ByteWriter().u32le(0x00000012).u32le(0).toArray());
}
// BITMAP_CODECS ([MS-RDPBCGR] 2.2.7.2.10, type 0x001D) — mstsc's exact 23-byte body (one codec: the
// RemoteFX/NSCodec GUID + codec properties).
function capBitmapCodecs() {
    return capSet(0x001D, Uint8Array.from([
        0x01,0xb9,0x1b,0x8d,0xca,0x0f,0x00,0x4f,0x15,0x58,0x9f,0xae,0x2d,0x1a,0x87,0xe2,0xd6,
        0x01,0x03,0x00,0x01,0x01,0x03,
    ]));
}
// FRAME_ACKNOWLEDGE ([MS-RDPRFX] 2.2.1.3, type 0x001E) — maxUnacknowledgedFrameCount. mstsc sends 2.
// This was the cap whose SLOT our mis-typed BitmapCodecs body was occupying; we now send it properly.
function capFrameAcknowledge() {
    return capSet(0x001E, new ByteWriter().u32le(0x00000002).toArray());
}

// The capsets below mstsc includes that we were OMITTING (found via the MITM Confirm Active diff: mstsc
// sends 22 sets, we sent 16). Bodies are byte-for-byte mstsc's. Several are legacy/mandatory sets the
// host may require to consider the client fully provisioned before sustaining the GFX stream.
function capControl() {            // CONTROL 0x05
    return capSet(0x0005, Uint8Array.from([0x00,0x00,0x00,0x00,0x02,0x00,0x02,0x00]));
}
function capWindowActivation() {   // WINDOWACTIVATION 0x07
    return capSet(0x0007, Uint8Array.from([0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00]));
}
function capShare() {              // SHARE 0x09
    return capSet(0x0009, Uint8Array.from([0x00,0x00,0x00,0x00]));
}
function capColorCache() {         // COLORCACHE 0x0A
    return capSet(0x000A, Uint8Array.from([0x06,0x00,0x00,0x00]));
}
function capFont() {               // FONT 0x0E
    return capSet(0x000E, Uint8Array.from([0x01,0x00,0x00,0x00]));
}
function capWindow() {             // WINDOW 0x18 (RemoteApp/AERO; mstsc sends a 7-byte body)
    return capSet(0x0018, Uint8Array.from([0x02,0x00,0x00,0x00,0x00,0x00,0x00]));
}
// BITMAPCACHE_REV2 0x13 — mstsc sends this instead of REV1 (0x04). Exact 36-byte body.
function capBitmapCacheRev2() {
    return capSet(0x0013, Uint8Array.from([
        0x02,0x00,0x00,0x03,0x78,0x00,0x00,0x00,0x78,0x00,0x00,0x00,0x51,0x01,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,
    ]));
}

function confirmActivePdu(shareID, userId, width, height, keyboardLayout) {
    // Our own capsets (responses valid for OUR Demand Active — emitting the macOS app's verbatim got
    // ERRINFO_BAD_CAPABILITIES 0x10EA). Added the GFX-gating capsets the macOS app has that we were
    // missing: MULTIFRAGMENTUPDATE now with a REAL MaxRequestSize (was 0), SURFACE_COMMANDS, BITMAP_CODECS,
    // LARGE_POINTER. These tell the host we can receive large multi-fragment surface (H.264) updates.
    // Match mstsc's Confirm Active capset LIST and ORDER (recovered via MITM diff). The off-by-one type
    // fix alone did not lift the stall; this brings the full set (22) and order in line with mstsc, in
    // case the host gates sustained GFX on a complete capability advertisement. Toggle back to the
    // minimal set with window.RDP_MIN_CAPS=1 for comparison.
    const minimal = (typeof window !== "undefined" && window.RDP_MIN_CAPS);
    const caps = minimal ? [
        capGeneral(), capBitmap(width, height), capOrder(), capBitmapCacheRev1(),
        capPointer(), capInput(keyboardLayout), capBrush(), capGlyphCache(), capOffscreen(),
        capVirtualChannel(), capSound(), capMultifragmentUpdate(),
        capLargePointer(), capSurfaceCommands(), capBitmapCodecs(), capFrameAcknowledge(),
    ] : [
        capGeneral(), capBitmap(width, height), capOrder(), capBitmapCacheRev2(),
        capColorCache(), capWindowActivation(), capControl(), capPointer(), capShare(),
        capInput(keyboardLayout), capSound(), capFont(), capGlyphCache(), capBrush(), capOffscreen(),
        capVirtualChannel(), capMultifragmentUpdate(), capSurfaceCommands(), capLargePointer(),
        capFrameAcknowledge(), capWindow(), capBitmapCodecs(),
    ];
    const capBuf = new ByteWriter();
    for (const c of caps) capBuf.bytes(c);
    const capArr = capBuf.toArray();

    const source = Uint8Array.from(PROJECT_NAME, c => c.charCodeAt(0));
    const lengthCombinedCapabilities = 4 + capArr.length;
    const totalLength = 6 + 4 + 2 + 2 + 2 + source.length + lengthCombinedCapabilities;

    const w = new ByteWriter();
    // ShareControlHeader
    w.u16le(totalLength);
    w.u16le(PDUTYPE_CONFIRMACTIVE);
    w.u16le(userId);
    // body
    w.u32le(shareID >>> 0);
    w.u16le(0x03EA); // originatorID
    w.u16le(source.length);
    w.u16le(lengthCombinedCapabilities);
    w.bytes(source);
    w.u16le(caps.length);
    w.u16le(0); // padding
    w.bytes(capArr);
    return w.toArray();
}

// ================================================================================================
// Protocol state machine
// ================================================================================================
const ST = {
    BASIC_SETTINGS: "basic-settings",
    ATTACH_USER: "attach-user",
    JOIN_CHANNELS: "join-channels",
    LICENSING: "licensing",
    CAPABILITIES: "capabilities",
    FINALIZATION: "finalization",
    ACTIVE: "active",
    CLOSED: "closed", // host signalled session end (graceful logoff/disconnect or MCS ultimatum)
};

// transport: { send(Uint8Array) }
// opts: { username, password, domain, width, height, selectedProtocol }
// callbacks: { onUpdate(ArrayBuffer), onActive(), onError(msg), onLog(msg) }
function RdpProtocol(transport, opts, callbacks) {
    this.t = transport;
    this.opts = opts;
    this.cb = callbacks || {};
    this.selectedProtocol = opts.selectedProtocol || 2; // HYBRID
    this.width = opts.width;
    this.height = opts.height;
    // Performance flags for the client info PDU (font smoothing, desktop composition, eye-candy).
    // Defaults to best visual fidelity; the UI can override per-connection.
    this.performanceFlags = (opts.performanceFlags === undefined ? PERF_DEFAULT : opts.performanceFlags) >>> 0;

    // Static virtual channels to request, in positional order. CRITICAL: drdynvc is requested LAST,
    // matching the working macOS Remote Desktop app's CS_NET order [rdpdr, rdpsnd, cliprdr, drdynvc] —
    // this puts drdynvc on MCS channel 1007 (not 1004 as when it was first). The channel ORDER changes
    // when the host opens the dynamic GFX/DisplayControl channels relative to each other, which is the
    // suspected trigger for the host's 2nd RESET_GRAPHICS (the GFX stall): on 1004-first we get a
    // DELETE+RESET reinit after DisplayControl; the macOS app on 1007-last gets a clean single RESET.
    // The host gates its rich audio DVC set (AUDIO_PLAYBACK_DVC for output, AUDIO_INPUT for the mic)
    // behind the rdpdr device-redirection static channel, so advertise rdpdr (and rdpsnd) whenever audio
    // output, microphone, or camera is requested.
    this.staticChannels = [];
    if (opts.audio || opts.microphone || opts.camera) this.staticChannels.push("rdpdr");
    if (opts.audio) this.staticChannels.push("rdpsnd");
    if (opts.clipboard) this.staticChannels.push("cliprdr");
    this.staticChannels.push("drdynvc"); // ALWAYS last → MCS 1007, like the macOS app

    this.userId = 0;
    this.shareID = 0;
    this.mcsChannelId = 1003;
    this.skipChannelJoin = false;
    this.joinQueue = [];
    this.staticChannelIds = {};      // name -> MCS channel id
    this.staticChannelById = {};     // MCS channel id -> name (inbound routing)
    this.drdynvcChannelId = 0;
    this._lastChannelId = 0;
    // Per-static-channel inbound reassembly (CHANNEL_PDU_HEADER FIRST..LAST). Keyed by channel name.
    this._svcReasm = {};
    // RDP-bulk (MPPC) contexts. With INFO_COMPRESSION set in the Client Info PDU (to match the macOS app),
    // the host may compress slow-path channel data TO us (_mppcRecv decompresses), and we send our GFX
    // CAPS_ADVERTISE bulk-compressed (_mppcSend). 8K/RDP4 (level 0) — matches the macOS app's type 0.
    const MppcCtor = (typeof window !== "undefined" && window.RdpMppc) ? window.RdpMppc.Mppc
                   : (typeof RdpMppc !== "undefined" ? RdpMppc.Mppc : null);
    this._mppcSend = MppcCtor ? new MppcCtor(0) : null;
    this._mppcRecv = MppcCtor ? new MppcCtor(0) : null;

    // Optional virtual-channel handlers, constructed lazily once their channel is joined.
    this.rdpsnd = null;   // RdpSnd instance when audio is enabled and rdpsnd joined
    this.cliprdr = null;  // ClipRdr instance when clipboard is enabled and cliprdr joined

    // Dynamic virtual channels (MS-RDPEDYC). Maps DVC channelId -> {name}. We only act on the
    // Display Control channel (MS-RDPEDISP) for live resolution/scale changes.
    this.dvcByName = {};        // name -> channelId
    this.dvcById = {};          // channelId -> name
    this.displayControlChannelId = null;
    this._dvcFragBuf = null;    // reassembly for fragmented DVC data
    this._dvcFragName = null;

    // Audio output dynamic virtual channel (AUDIO_PLAYBACK_DVC). Modern Windows carries rdpsnd over
    // a DVC, not the static rdpsnd channel — so audio actually flows through here. Per-channel DVC
    // data reassembly (waves are large and arrive as DATA_FIRST + DATA*).
    this.audioDvcChannelId = null;
    this.audioDvcCbId = 0;
    this._dvcReasm = {};        // DVC channelId -> {parts, len, total}
    this._dvcZgfx = {};         // DVC channelId -> ZgfxDecode (v3 compressed-data inflate context)

    // Audio input dynamic virtual channel ("AUDIO_INPUT", MS-RDPEAI) — microphone redirection. Like
    // the audio OUTPUT DVC, the host offers this dynamically (gated on rdpsnd/rdpdr presence) once it
    // wants to capture the client mic. Accepted only when opts.microphone is set; bound to AudInput.
    this.audinDvcChannelId = null;
    this.audinDvcCbId = 0;
    this.audin = null;          // AudInput instance when microphone is enabled and the DVC is opened

    // Camera redirection (MS-RDPECAM). Two DVCs: the control/enumerator channel
    // "RDCamera_Device_Enumerator" (host-opened; we send SelectVersionRequest then advertise a virtual
    // camera) and a per-device channel the host opens by the name we advertise. Accepted only when
    // opts.camera. The device channel drives capture; frames come from client.js.
    this.camEnumChannelId = null;
    this.camEnumCbId = 0;
    this.camEnum = null;        // RdpCamEnum (control channel)
    this.camDeviceChannelName = null; // the VirtualChannelName we advertised; the host opens it next
    // The host opens MULTIPLE concurrent device channels (one per consuming pipeline), all with the
    // same name — and expects every one to keep working. So we keep a RdpCamDevice per channelId, not a
    // single instance (overwriting a single one made older channels go dead → the host looped opening
    // new ones). Keyed by MCS/DVC channelId.
    this.camDevices = {};       // channelId -> RdpCamDevice

    // RDPEGFX graphics DVC. When the GFX path is enabled (rdpTryGfx) we drive a full RdpGfx surface
    // compositor with H.264 (AVC420) decode over this channel; otherwise we only keep the channel
    // alive (caps advertise) to unlock the host's rich DVC set (audio) and render via legacy bitmaps.
    this.gfxDvcChannelId = null;
    this.gfxDvcCbId = 0;
    this.gfx = null;            // RdpGfx instance, created on channel accept when GFX rendering is on

    // Desktop scale factor (DPI) — tracks the scale currently in effect on the server. On the GFX
    // path CS_CORE carries opts.desktopScaleFactor (see clientCoreData's optional tail), so the
    // session already STARTS at that scale; the no-GFX baseline CS_CORE stays scale-less → 100.
    // The first MONITOR_LAYOUT is force-sent regardless of no-op detection (_monitorLayoutSent
    // below), so initializing this to the CS_CORE value cannot suppress it. ([MS-RDPEDISP] percent.)
    this.gfxEnabled = rdpTryGfx();  // session-wide GFX mode; the RdpGfx instance (this.gfx) is
                                    // created later, on the host's DVC create-request
    this.desktopScaleFactor = (this.gfxEnabled && opts.desktopScaleFactor) ? opts.desktopScaleFactor : 100;
    this.deviceScaleFactor = 100;  // 100, 140 or 180, current server scale
    // Windows keyboard layout id (KLID) advertised in CS_CORE and the Input capset. The host loads
    // this layout for the session, so the scancodes we send (physical-key e.code positions) produce
    // the characters the user's real keyboard is labelled with. Detected browser-side (client.js).
    this.keyboardLayout = (opts.keyboardLayout >>> 0) || 0x0409;
    this._monitorLayoutSent = false; // gate so the FIRST MONITOR_LAYOUT is never no-op'd (host needs it)

    this.state = null;
    this.rxBuf = new Uint8Array(0); // inbound reassembly buffer
    this.rxPos = 0;                 // read cursor into rxBuf (unconsumed data is rxBuf[rxPos..])
    this._bwStart = null;           // auto-detect bandwidth-measure window start (ms), null when idle
    this._bwBytes = 0;              // bytes received during the current bandwidth-measure window

    // finalization progress flags
    this.fin = { sync: false, coop: false, granted: false, fontmap: false };
}

RdpProtocol.prototype._log = function (m) { if (this.cb.onLog) this.cb.onLog(m); };
RdpProtocol.prototype._err = function (m) { if (this.cb.onError) this.cb.onError(m); };

// The session has ended cleanly from the protocol's point of view (the host sent a graceful
// disconnect/logoff via SET_ERROR_INFO_PDU, or an MCS Disconnect Provider Ultimatum). Fire onClose
// so the UI tears down IMMEDIATELY instead of waiting for the host to lazily close its TCP socket
// (a Windows host can sit in the "you have been disconnected" state for several seconds first). Fires
// at most once; after it the protocol stops driving the session.
RdpProtocol.prototype._close = function (graceful, message) {
    if (this._closed) return;
    this._closed = true;
    this.state = ST.CLOSED;
    if (this.cb.onClose) this.cb.onClose(graceful, message);
    else if (!graceful) this._err(message); // back-compat if the host didn't wire onClose
};

// Kick off the handshake: send MCS connect-initial. Called once the relay is "ready".
RdpProtocol.prototype.start = function () {
    this._log("MCS: Connect Initial");
    const userData = clientUserData(this.selectedProtocol, this.width, this.height, this.staticChannels,
        this.desktopScaleFactor, this.keyboardLayout);
    const connectInitial = mcsConnectInitialSerialize(userData);
    this.t.send(tpktX224Wrap(connectInitial));
    this.state = ST.BASIC_SETTINGS;
};

// Append inbound bytes and process every complete PDU available.
RdpProtocol.prototype.feed = function (chunk) {
    // Count bytes received during an in-progress auto-detect bandwidth measurement so the
    // Bandwidth Measure Results we report back to the host reflects the real payload size.
    if (this._bwStart != null) this._bwBytes = (this._bwBytes || 0) + chunk.length;

    // Append the new chunk to whatever unconsumed remainder is still buffered. A read cursor (rxPos)
    // lets us drain complete framed units WITHOUT recopying — we only allocate here to join the
    // leftover tail (typically empty, since a chunk usually contains whole PDUs) with the new bytes,
    // so the copy is proportional to the partial-frame remainder, not the whole receive history. This
    // avoids the O(n^2) full-buffer realloc-per-chunk churn under high-throughput GFX streaming.
    const remaining = this.rxBuf.length - this.rxPos;
    if (remaining === 0) {
        // Common case: nothing left over — the new chunk IS the buffer, no join copy.
        this.rxBuf = chunk;
    } else {
        const merged = new Uint8Array(remaining + chunk.length);
        merged.set(this.rxBuf.subarray(this.rxPos), 0);
        merged.set(chunk, remaining);
        this.rxBuf = merged;
    }
    this.rxPos = 0;

    // Process as many framed units as are complete, advancing the cursor instead of reslicing.
    for (;;) {
        const consumed = this._processOne();
        if (consumed <= 0) break;
        this.rxPos += consumed;
    }

    // If everything was consumed, drop the buffer so we don't retain the (now fully read) chunk.
    if (this.rxPos >= this.rxBuf.length) { this.rxBuf = EMPTY_RX; this.rxPos = 0; }
};
var EMPTY_RX = new Uint8Array(0);

// Determine the length of the next inbound unit (TPKT slow-path or fastpath), returning 0 if more
// bytes are needed. Then dispatch it. Returns the number of bytes consumed (0 if incomplete).
RdpProtocol.prototype._processOne = function () {
    // Operate on the unconsumed window rxBuf[rxPos..] as a view (no copy). Returned "consumed" counts
    // are relative to this view; feed() advances rxPos by that amount.
    const b = this.rxPos === 0 ? this.rxBuf : this.rxBuf.subarray(this.rxPos);
    if (b.length < 1) return 0;

    const first = b[0];
    if (first === 0x03) {
        // TPKT slow-path: version(1) reserved(1) length(2, big-endian)
        if (b.length < 4) return 0;
        const len = (b[2] << 8) | b[3];
        if (len < 4) { this._err("bad TPKT length"); return -1; }
        if (b.length < len) return 0;
        try {
            this._handleSlowPath(b.subarray(4, len));
        } catch (e) {
            // A single malformed/unhandled slow-path PDU must NOT wedge the stream — its full length
            // is known from TPKT, so skip exactly this PDU and keep processing the rest (fastpath
            // updates, the reactivation sequence, etc. continue to flow).
            this._log("skipping slow-path PDU: " + (e && e.message ? e.message : e));
        }
        return len;
    }

    // Fastpath output header: low 2 bits = action(0); bit layout per [MS-RDPBCGR] 2.2.9.1.2.
    // length is 1 or 2 bytes (PER-style: high bit of first byte set => 2-byte length).
    if (b.length < 2) return 0;
    let len1 = b[1];
    let headerLen = 2;
    let length = len1;
    if (len1 & 0x80) {
        if (b.length < 3) return 0;
        length = ((len1 & 0x7f) << 8) | b[2];
        headerLen = 3;
    }
    if (length < headerLen) { this._err("bad fastpath length"); return -1; }
    if (b.length < length) return 0;
    try {
        this._handleFastPath(b[0], b.subarray(headerLen, length));
    } catch (e) {
        // Skip this fastpath PDU but keep the stream alive (length is known from the header).
        this._log("skipping fastpath PDU: " + (e && e.message ? e.message : e));
    }
    return length;
};

// Slow-path: strip the X.224 Data header (LI=2, 0xF0, EOT) then dispatch by state.
RdpProtocol.prototype._handleSlowPath = function (x224) {
    // x224[0]=LI, x224[1]=0xF0, x224[2]=EOT ; MCS PDU follows
    const r = new ByteReader(x224.subarray(3));

    switch (this.state) {
        case ST.BASIC_SETTINGS:
            return this._onConnectResponse(r);
        case ST.ATTACH_USER:
            return this._onAttachUserConfirm(r);
        case ST.JOIN_CHANNELS:
            return this._onChannelJoinConfirm(r);
        case ST.LICENSING:
            this._mcsSendDataIndication(r);
            if (this._routeChannelData(r)) return;
            if (this._handleAutoDetect(r)) return;
            return this._onLicensing(r);
        case ST.CAPABILITIES:
            this._mcsSendDataIndication(r);
            if (this._routeChannelData(r)) return;
            if (this._handleAutoDetect(r)) return;
            return this._onDemandActive(r);
        case ST.FINALIZATION:
            this._mcsSendDataIndication(r);
            if (this._routeChannelData(r)) return;
            if (this._handleAutoDetect(r)) return;
            return this._onFinalization(r);
        case ST.ACTIVE:
            // Slow-path data during the active phase (e.g. error info / deactivate-all, or a
            // continuous-mode auto-detect request). Route virtual-channel data by source channel id
            // FIRST — _handleAutoDetect sniffs the first u16 as security flags, and a channel PDU
            // whose length happens to have the 0x1000/0x4000 bits set would be eaten as a bogus
            // autodetect/heartbeat PDU. Then handle autodetect, else parse the share control header
            // to detect deactivate-all & error info; otherwise ignore.
            this._mcsSendDataIndication(r);
            if (this._routeChannelData(r)) return;
            if (this._handleAutoDetect(r)) return;
            return this._onActiveSlowPath(r);
        default:
            return;
    }
};

// [MS-RDPBCGR] 2.2.1.4 security header flags relevant to the connection sequence.
const SEC_AUTODETECT_REQ = 0x1000;
const SEC_AUTODETECT_RSP = 0x2000;
const SEC_HEARTBEAT = 0x4000;

// If the SendDataIndication payload (r positioned at the RDP body / security header) is a Network
// Auto-Detect Request ([MS-RDPBCGR] 2.2.14) or a Heartbeat PDU, handle it and return true so the
// caller does NOT mis-route it as licensing/Demand-Active. FreeRDP-based hosts (GNOME Remote Desktop)
// send these immediately after Client Info because we advertise RNS_UD_CS_SUPPORT_NETCHAR_AUTODETECT;
// without a response the host stalls and the session never reaches the capabilities exchange.
RdpProtocol.prototype._handleAutoDetect = function (r) {
    const start = r.o;
    if (r.remaining() < 4) { r.o = start; return false; }
    const flags = r.u16le();
    r.u16le(); // flagsHi
    if (flags & SEC_HEARTBEAT) return true;      // server heartbeat: nothing to send, just absorb it
    if (!(flags & SEC_AUTODETECT_REQ)) { r.o = start; return false; }

    // RDP_AUTODETECT_REQUEST_PDU: headerLength(1) headerTypeId(1) sequenceNumber(2 LE) requestType(2 LE)
    if (r.remaining() < 6) return true;
    r.u8();                              // headerLength
    const headerTypeId = r.u8();         // 0x00 = AUTODETECT_REQUEST
    const seq = r.u16le();
    const requestType = r.u16le();
    if (headerTypeId !== 0x00) return true;
    this._onAutoDetectRequest(seq, requestType);
    return true;
};

// Respond to a single Network Auto-Detect Request. We implement the connect-time RTT + bandwidth
// measurement exchange ([MS-RDPBCGR] 2.2.14.1): RTT requests get an RTT response; a Bandwidth-Stop
// gets a Bandwidth-Measure-Results; Start/Payload/NetChar-Results need no reply.
RdpProtocol.prototype._onAutoDetectRequest = function (seq, requestType) {
    // requestType values per [MS-RDPBCGR] 2.2.14.1.* (and FreeRDP autodetect.c).
    const RTT_CONTINUOUS = 0x0001, RTT_CONNECTTIME = 0x1001;
    const BW_START_CONTINUOUS = 0x0014, BW_START_TUNNEL = 0x0114, BW_START_CONNECTTIME = 0x1014;
    const BW_PAYLOAD = 0x0002;
    const BW_STOP_CONNECTTIME = 0x002B, BW_STOP_CONTINUOUS = 0x0429, BW_STOP_TUNNEL = 0x0629;
    const RTT_RESPONSE_TYPE = 0x0000;
    const BW_RESULTS_CONNECTTIME = 0x0003, BW_RESULTS_CONTINUOUS = 0x000B;

    if (requestType === RTT_CONNECTTIME || requestType === RTT_CONTINUOUS) {
        //this._log("RDP: auto-detect RTT request (seq " + seq + ")");
        this._sendAutoDetectRtt(seq);
        return;
    }
    if (requestType === BW_START_CONNECTTIME || requestType === BW_START_CONTINUOUS || requestType === BW_START_TUNNEL) {
        this._bwStart = (typeof performance !== "undefined" ? performance.now() : Date.now());
        this._bwBytes = 0;
        //this._log("RDP: auto-detect bandwidth-measure start (seq " + seq + ")");
        return;
    }
    if (requestType === BW_PAYLOAD) {
        // The payload bytes are the measured traffic; their size already passed through feed().
        return;
    }
    if (requestType === BW_STOP_CONNECTTIME || requestType === BW_STOP_CONTINUOUS || requestType === BW_STOP_TUNNEL) {
        const now = (typeof performance !== "undefined" ? performance.now() : Date.now());
        const delta = Math.max(0, Math.round(now - (this._bwStart || now)));
        const respType = (requestType === BW_STOP_CONTINUOUS) ? BW_RESULTS_CONTINUOUS : BW_RESULTS_CONNECTTIME;
        //this._log("RDP: auto-detect bandwidth-measure stop (seq " + seq + ", " + delta + "ms, " + (this._bwBytes || 0) + "B)");
        this._sendAutoDetectBwResults(seq, respType, delta, this._bwBytes || 0);
        this._bwStart = null;
        return;
    }
    // NetChar Results (0x0840/0x0880/0x08C0) and anything else: informational, no response required.
    //this._log("RDP: auto-detect request type 0x" + requestType.toString(16) + " (no reply)");
};

// Auto-detect responses MUST be sent on the MCS message channel ([MS-RDPBCGR] 2.2.14.2): that is where
// a FreeRDP host reads them (it routes by messageChannelId). Fall back to the I/O channel only if no
// message channel was granted (shouldn't happen now that we request CS_MCS_MSGCHANNEL).
RdpProtocol.prototype._autoDetectChannel = function () {
    return this.msgChannelId || this.mcsChannelId;
};

// RTT Measure Response ([MS-RDPBCGR] 2.2.14.2.1): headerLength=0x06, typeId=0x01, seq, responseType=0.
RdpProtocol.prototype._sendAutoDetectRtt = function (seq) {
    const w = new ByteWriter();
    w.u16le(SEC_AUTODETECT_RSP).u16le(0); // security header
    w.u8(0x06).u8(0x01).u16le(seq).u16le(0x0000);
    this.t.send(tpktX224Wrap(mcsSendDataSerialize(this.userId, this._autoDetectChannel(), w.toArray())));
};

// Bandwidth Measure Results ([MS-RDPBCGR] 2.2.14.2.2): headerLength=0x0E, typeId=0x01, seq,
// responseType, timeDelta(4 LE), byteCount(4 LE).
RdpProtocol.prototype._sendAutoDetectBwResults = function (seq, responseType, timeDelta, byteCount) {
    const w = new ByteWriter();
    w.u16le(SEC_AUTODETECT_RSP).u16le(0); // security header
    w.u8(0x0E).u8(0x01).u16le(seq).u16le(responseType).u32le(timeDelta >>> 0).u32le(byteCount >>> 0);
    this.t.send(tpktX224Wrap(mcsSendDataSerialize(this.userId, this._autoDetectChannel(), w.toArray())));
};

// Reads the MCS Send-Data-Indication header. Records the source channel id on `this._lastChannelId`
// and returns a reader positioned at the embedded RDP/virtual-channel PDU.
RdpProtocol.prototype._mcsSendDataIndication = function (r) {
    const choice = Per.readChoice(r);
    const application = choice >> 2;
    if (application === MCS_DISCONNECT_ULTIMATUM) {
        // The host is tearing down the MCS domain — the session is over. Close NOW so the UI reacts
        // immediately; a SET_ERROR_INFO_PDU with the reason usually preceded this, but the ultimatum
        // alone (e.g. a hard server-side disconnect) is enough to end the session cleanly.
        this._close(true, "The remote desktop ended the session.");
        throw new Error("server disconnected (MCS ultimatum)");
    }
    if (application !== MCS_SEND_DATA_INDICATION) throw new Error("unexpected MCS application " + application);
    Per.readInteger16(r, 1001);               // initiator
    this._lastChannelId = Per.readInteger16(r, 0); // channelId (global I/O vs a virtual channel)
    Per.readEnumerates(r);                    // dataPriority/segmentation
    Per.readLength(r);                        // payload length
    return r;
};

RdpProtocol.prototype._onConnectResponse = function (r) {
    const ud = mcsParseConnectResponse(r);
    const parsed = parseServerUserData(ud);
    this.mcsChannelId = parsed.mcsChannelId;
    this.skipChannelJoin = parsed.skipChannelJoin;
    this.msgChannelId = parsed.msgChannelId; // SC_MCS_MSGCHANNEL grant (0 if not granted)
    // Map the positional SC_NET channel ids back to our static channel names (both directions).
    this.staticChannelIds = {};
    this.staticChannelById = {};
    for (let i = 0; i < this.staticChannels.length && i < parsed.channelIds.length; i++) {
        const name = this.staticChannels[i];
        const id = parsed.channelIds[i];
        this.staticChannelIds[name] = id;
        this.staticChannelById[id] = name;
    }
    this.drdynvcChannelId = this.staticChannelIds["drdynvc"] || 0;
    this._log("MCS: Connect Response (global " + this.mcsChannelId + ", drdynvc " + this.drdynvcChannelId + ")");

    // channelConnection: erect domain + attach user
    this.t.send(tpktX224Wrap(mcsErectDomainSerialize()));
    this._log("MCS: Erect Domain");
    this.t.send(tpktX224Wrap(mcsAttachUserSerialize()));
    this._log("MCS: Attach User Request");
    this.state = ST.ATTACH_USER;
};

RdpProtocol.prototype._onAttachUserConfirm = function (r) {
    const choice = Per.readChoice(r);
    if ((choice >> 2) !== MCS_ATTACH_USER_CONFIRM) throw new Error("expected attach-user-confirm");
    const result = Per.readEnumerates(r);
    if (result !== 0) throw new Error("MCS attach-user failed result=" + result);
    this.userId = Per.readInteger16(r, 1001);
    this._log("MCS: Attach User Confirm (user " + this.userId + ")");

    if (this.skipChannelJoin) {
        // No join round-trips — but the static channel handlers must still be constructed here,
        // exactly as at the end of the join sequence. Skipping this left this.cliprdr/this.rdpsnd
        // null on skip-channel-join hosts (modern Windows), silently dropping all clipboard traffic.
        this._initStaticChannelHandlers();
        this._sendClientInfo();
        return;
    }
    // Join the user channel, the global I/O channel, then each static virtual channel.
    this.joinQueue = [this.userId, this.mcsChannelId];
    for (const name of this.staticChannels) {
        const id = this.staticChannelIds[name];
        if (id) this.joinQueue.push(id);
    }
    // Join the MCS message channel if the server granted one (we advertised CS_MCS_MSGCHANNEL).
    // [MS-RDPBCGR] 3.2.5.3.3: the message channel MUST be joined like any other channel; skipping it
    // leaves the host's connection sequence waiting and it throttles GFX (network-autodetect gate).
    if (this.msgChannelId) this.joinQueue.push(this.msgChannelId);
    this.state = ST.JOIN_CHANNELS;
    this._sendNextChannelJoin();
};

RdpProtocol.prototype._sendNextChannelJoin = function () {
    const ch = this.joinQueue[0];
    this._log("MCS: Channel Join Request " + ch);
    this.t.send(tpktX224Wrap(mcsChannelJoinSerialize(this.userId, ch)));
};

RdpProtocol.prototype._onChannelJoinConfirm = function (r) {
    const choice = Per.readChoice(r);
    if ((choice >> 2) !== MCS_CHANNEL_JOIN_CONFIRM) throw new Error("expected channel-join-confirm");
    const result = Per.readEnumerates(r);
    if (result !== 0) throw new Error("MCS channel-join failed result=" + result);
    this.joinQueue.shift();
    if (this.joinQueue.length > 0) {
        this._sendNextChannelJoin();
        return;
    }
    this._initStaticChannelHandlers();
    this._sendClientInfo();
};

// Construct the rdpsnd/cliprdr handlers once their channels are joined. Each handler is given a
// `send(payload)` that wraps the payload in a CHANNEL_PDU_HEADER and ships it on its MCS channel.
RdpProtocol.prototype._initStaticChannelHandlers = function () {
    const self = this;
    if (this.opts.audio && this.staticChannelIds["rdpsnd"] && typeof RdpSnd !== "undefined") {
        const id = this.staticChannelIds["rdpsnd"];
        this.rdpsnd = new RdpSnd(
            function (payload) { self._sendOnChannel(id, payload); },
            { onLog: function (m) { self._log("rdpsnd: " + m); },
              onWave: function (fmt, pcm) { if (self.cb.onAudio) self.cb.onAudio(fmt, pcm); },
              onFormats: function (fmts) { if (self.cb.onAudioFormats) self.cb.onAudioFormats(fmts); } });
    }
    if (this.opts.clipboard && this.staticChannelIds["cliprdr"] && typeof ClipRdr !== "undefined") {
        const id = this.staticChannelIds["cliprdr"];
        this.cliprdr = new ClipRdr(
            function (payload) { self._sendOnChannel(id, payload); },
            { onLog: function (m) { self._log("cliprdr: " + m); },
              onRemoteText: function (text) { if (self.cb.onClipboardText) self.cb.onClipboardText(text); } });
    }
};

// ---- rdpdr: device redirection (MS-RDPEFS) -------------------------------------------------------
// We redirect NO devices (no drives/printers/smartcards). We implement only the minimal init
// handshake so the host completes device-redirection setup — which is what gates the
// AUDIO_PLAYBACK_DVC dynamic channel (remote sound). Flow ([MS-RDPEFS] 3.2.5.1 / 3.3.5.1):
//   server Announce Request → client Announce Reply (Client ID Confirm) → client Name Request →
//   server Core Capability → client Core Capability Response (General capset only) →
//   server Client ID Confirm → server User Logged On → client Device List Announce (empty, count 0).
// rdpdr PDU header: Component(2 LE) + PacketId(2 LE). Values from FreeRDP rdpdr.h.
const RDPDR_CTYP_CORE = 0x4472;
const PAKID_CORE_SERVER_ANNOUNCE = 0x496E;
const PAKID_CORE_CLIENTID_CONFIRM = 0x4343;
const PAKID_CORE_CLIENT_NAME = 0x434E;
const PAKID_CORE_DEVICELIST_ANNOUNCE = 0x4441;
const PAKID_CORE_SERVER_CAPABILITY = 0x5350;
const PAKID_CORE_CLIENT_CAPABILITY = 0x4350;
const PAKID_CORE_USER_LOGGEDON = 0x554C;
const RDPDR_CAP_GENERAL_TYPE = 0x0001;
const RDPDR_GENERAL_CAPABILITY_VERSION_02 = 0x00000002;
const RDPDR_VERSION_MAJOR = 0x0001;
const RDPDR_VERSION_MINOR_RDP10X = 0x000D;

RdpProtocol.prototype._sendRdpdr = function (payload) {
    const id = this.staticChannelIds["rdpdr"];
    if (id) this._sendOnChannel(id, payload);
};

RdpProtocol.prototype._onRdpdrData = function (payload) {
    if (!payload || payload.length < 4) return;
    const r = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
    const component = r.getUint16(0, true);
    const packetId = r.getUint16(2, true);
    if (component !== RDPDR_CTYP_CORE) { // ignore printer-cache (RDPDR_CTYP_PRN) etc.
        this._log("rdpdr: ignoring component 0x" + component.toString(16) + " packetId 0x" + packetId.toString(16));
        return;
    }

    switch (packetId) {
        case PAKID_CORE_SERVER_ANNOUNCE: {
            // body: VersionMajor(2) VersionMinor(2) ClientId(4)
            this._rdpdrClientId = (payload.length >= 8) ? r.getUint32(4, true) : 0;
            this._log("rdpdr: server announce (clientId=" + this._rdpdrClientId + ")");
            this._sendRdpdrAnnounceReply();
            this._sendRdpdrClientName();
            break;
        }
        case PAKID_CORE_SERVER_CAPABILITY: {
            this._log("rdpdr: server capability → responding (general only)");
            this._sendRdpdrCapabilityResponse();
            break;
        }
        case PAKID_CORE_CLIENTID_CONFIRM:
            this._log("rdpdr: client id confirm");
            break;
        case PAKID_CORE_USER_LOGGEDON:
            this._log("rdpdr: user logged on → announcing empty device list");
            this._sendRdpdrDeviceListAnnounce();
            break;
        default:
            // device IO requests etc. — we have no devices, so nothing to answer.
            this._log("rdpdr: ignoring packetId 0x" + (packetId >>> 0).toString(16));
            break;
    }
};

RdpProtocol.prototype._sendRdpdrAnnounceReply = function () {
    const w = new ByteWriter();
    w.u16le(RDPDR_CTYP_CORE);
    w.u16le(PAKID_CORE_CLIENTID_CONFIRM);
    w.u16le(RDPDR_VERSION_MAJOR);
    w.u16le(RDPDR_VERSION_MINOR_RDP10X);
    w.u32le(this._rdpdrClientId >>> 0);
    this._sendRdpdr(w.toArray());
};

RdpProtocol.prototype._sendRdpdrClientName = function () {
    const name = (PROJECT_NAME || "rdpweb");
    const nameW = new ByteWriter().utf16le(name).toArray(); // no NUL
    const cbName = nameW.length + 2; // include trailing UTF-16 NUL
    const w = new ByteWriter();
    w.u16le(RDPDR_CTYP_CORE);
    w.u16le(PAKID_CORE_CLIENT_NAME);
    w.u32le(1);        // unicodeFlag
    w.u32le(0);        // codePage
    w.u32le(cbName);   // computerNameLen (incl NUL)
    w.bytes(nameW).u16le(0);
    this._sendRdpdr(w.toArray());
};

RdpProtocol.prototype._sendRdpdrCapabilityResponse = function () {
    const w = new ByteWriter();
    w.u16le(RDPDR_CTYP_CORE);
    w.u16le(PAKID_CORE_CLIENT_CAPABILITY);
    w.u16le(1);  // numCapabilities (General only — we redirect no devices)
    w.u16le(0);  // pad
    // GENERAL_CAPS_SET: CAPABILITY_HEADER {type(2), length(2 = 8 header + 36 body), version(4)} + body.
    w.u16le(RDPDR_CAP_GENERAL_TYPE);
    w.u16le(8 + 36);
    w.u32le(RDPDR_GENERAL_CAPABILITY_VERSION_02);
    w.u32le(0);            // osType (ignored)
    w.u32le(0);            // osVersion (must be 0)
    w.u16le(RDPDR_VERSION_MAJOR); // protocolMajorVersion
    w.u16le(RDPDR_VERSION_MINOR_RDP10X); // protocolMinorVersion
    w.u32le(0x0000FFFF);   // ioCode1 (all IRP_MJ_*)
    w.u32le(0);            // ioCode2 (reserved, 0)
    w.u32le(0x00000007);   // extendedPDU = REMOVE | DISPLAY_NAME | USER_LOGGEDON
    w.u32le(0x00000001);   // extraFlags1 = ENABLE_ASYNCIO
    w.u32le(0);            // extraFlags2 (reserved, 0)
    w.u32le(0);            // SpecialTypeDeviceCap (no special devices)
    this._sendRdpdr(w.toArray());
};

RdpProtocol.prototype._sendRdpdrDeviceListAnnounce = function () {
    const w = new ByteWriter();
    w.u16le(RDPDR_CTYP_CORE);
    w.u16le(PAKID_CORE_DEVICELIST_ANNOUNCE);
    w.u32le(0); // deviceCount = 0 (we redirect nothing)
    this._sendRdpdr(w.toArray());
};

RdpProtocol.prototype._sendClientInfo = function () {
    this._log("RDP: Client Info (audioCapture=" + (this.opts.microphone ? "yes" : "no") + ")");
    const info = clientInfoPdu(this.opts.domain, this.opts.username, this.opts.password, this.performanceFlags, this.opts.microphone);
    this.t.send(tpktX224Wrap(mcsSendDataSerialize(this.userId, this.mcsChannelId, info)));
    this.state = ST.LICENSING;
};

RdpProtocol.prototype._onLicensing = function (r) {
    // Security header: flags(2) flagsHi(2). SEC_LICENSE_PKT = 0x0080.
    const flags = r.u16le();
    r.u16le(); // flagsHi
    if ((flags & 0x0080) !== 0x0080) {
        // Some servers send the demand-active directly without a license PDU; treat anything that
        // isn't a license packet as the start of the capabilities exchange.
        this.state = ST.CAPABILITIES;
        return this._onDemandActiveControl(r);
    }
    const msgType = r.u8();
    if (msgType === 0xFF || msgType === 0x03) {
        // ERROR_ALERT (STATUS_VALID_CLIENT) or NEW_LICENSE — both mean "no license needed".
        this._log("RDP: licensing complete");
        this.state = ST.CAPABILITIES;
        return;
    }
    // Other license message types are not expected against typical hosts; proceed anyway.
    this._log("RDP: licensing msgType=" + msgType + " (ignored)");
    this.state = ST.CAPABILITIES;
};

// r is positioned at the share control header (already past security flags if present).
RdpProtocol.prototype._onDemandActive = function (r) {
    // The capabilities-exchange slow path may carry a 4-byte security header on some hosts. Peek:
    // a ShareControlHeader starts with totalLength(2) then pduType(2). If the second u16 isn't a
    // known PDU type, assume a leading security header and skip it.
    return this._onDemandActiveControl(r);
};

RdpProtocol.prototype._onDemandActiveControl = function (r) {
    // The PDU type field packs the type in the low nibble and a protocol version in the high nibble
    // (e.g. DEMANDACTIVE 0x11, CONFIRMACTIVE 0x13, DEACTIVATEALL 0x16, DATAPDU 0x17). Compare on the
    // low nibble.
    const startO = r.o;
    let totalLength = r.u16le();
    let pduTypeRaw = r.u16le();
    let pduType = pduTypeRaw & 0xf;
    // Recognize the share-control header directly. DEACTIVATEALL(0x16), DEMANDACTIVE(0x11) and
    // DATAPDU(0x17) are the types we may see here; anything else means a 4-byte security header
    // precedes the share control header, so rewind and skip it.
    const known = function (t) {
        return t === (PDUTYPE_DEMANDACTIVE & 0xf) || t === (PDUTYPE_DATAPDU & 0xf)
            || t === (PDUTYPE_DEACTIVATEALL & 0xf);
    };
    if (!known(pduType)) {
        r.o = startO + 4;
        totalLength = r.u16le();
        pduTypeRaw = r.u16le();
        pduType = pduTypeRaw & 0xf;
    }
    if (pduType === (PDUTYPE_DEACTIVATEALL & 0xf)) {
        // GNOME Remote Desktop / FreeRDP sends a Deactivate-All at connect time (before the real
        // Demand Active) to reset the share. Absorb it and keep waiting in CAPABILITIES — replying
        // with a Confirm Active here (to a PDU that is NOT a Demand Active) makes the host abort.
        this._log("RDP: Deactivate-All (pre-capabilities); waiting for Demand Active");
        return;
    }
    if (pduType !== (PDUTYPE_DEMANDACTIVE & 0xf)) {
        // Not the demand-active yet (could be an early data PDU); ignore and keep waiting.
        return;
    }
    r.u16le(); // pduSource
    this.shareID = r.u32le();
    this._log("RDP: Demand Active (shareID " + this.shareID + ")");

    // Reply with Confirm Active, then run connection finalization.
    const confirm = confirmActivePdu(this.shareID, this.userId, this.width, this.height, this.keyboardLayout);
    this.t.send(tpktX224Wrap(mcsSendDataSerialize(this.userId, this.mcsChannelId, confirm)));

    this._sendFinalization();
};

RdpProtocol.prototype._sendFinalization = function () {
    this._log("RDP: connection finalization");
    const send = (pdu) => this.t.send(tpktX224Wrap(mcsSendDataSerialize(this.userId, this.mcsChannelId, pdu)));
    send(synchronizePdu(this.shareID, this.userId));
    send(controlPdu(this.shareID, this.userId, CTRLACTION_COOPERATE));
    send(controlPdu(this.shareID, this.userId, CTRLACTION_REQUEST_CONTROL));
    send(fontListPdu(this.shareID, this.userId));
    this.fin = { sync: false, coop: false, granted: false, fontmap: false };
    this.state = ST.FINALIZATION;

    // On a Deactivation-Reactivation (resolution/scale change) the session is already logged in and
    // the server resumes sending graphics immediately after our font list — it may not re-send all
    // four finalization PDUs in a way we'd block on. Go ACTIVE right away so fastpath output renders;
    // any finalization responses that do arrive are handled harmlessly while ACTIVE.
    if (this._reactivating) {
        this._reactivating = false;
        this.state = ST.ACTIVE;
        this._log("RDP: session active (reactivated)");
        if (this.cb.onActive) this.cb.onActive();
    }
};

// Parse a server data PDU (share data header) and advance the finalization handshake.
RdpProtocol.prototype._onFinalization = function (r) {
    const info = this._readShareDataHeader(r);
    if (info === null) return; // not a data PDU we understand here
    switch (info.pduType2) {
        case PDUTYPE2_SYNCHRONIZE: this.fin.sync = true; break;
        case PDUTYPE2_CONTROL: {
            const action = r.u16le();
            if (action === CTRLACTION_COOPERATE) this.fin.coop = true;
            else if (action === CTRLACTION_GRANTED_CONTROL) this.fin.granted = true;
            break;
        }
        case PDUTYPE2_FONTMAP: this.fin.fontmap = true; break;
        case PDUTYPE2_SET_ERROR_INFO_PDU: {
            const code = r.u32le();
            this._log("RDP: server error info 0x" + code.toString(16));
            const reason = errorInfoReason(code);
            if (reason) this._close(reason.graceful, reason.message);
            break;
        }
        default: break;
    }
    if (this.fin.sync && this.fin.coop && this.fin.granted && this.fin.fontmap) {
        this.state = ST.ACTIVE;
        this._log("RDP: session active");
        if (this.cb.onActive) this.cb.onActive();
    }
};

// Reads a ShareControlHeader + ShareDataHeader. Returns {pduType2} for data PDUs, or null otherwise.
// Returns {deactivateAll:true} on a DEACTIVATE_ALL PDU, {pduType2} for a data PDU, or null otherwise.
// r is left positioned at the data PDU body (for data PDUs).
RdpProtocol.prototype._readShareDataHeader = function (r) {
    /* totalLength */ r.u16le();
    const pduType = r.u16le() & 0xf;
    /* pduSource */ r.u16le();
    if (pduType === (PDUTYPE_DEACTIVATEALL & 0xf)) return { deactivateAll: true };
    if (pduType !== (PDUTYPE_DATAPDU & 0xf)) return null;
    /* shareID */ r.u32le();
    /* padding */ r.u8();
    /* streamID */ r.u8();
    /* uncompressedLength */ r.u16le();
    const pduType2 = r.u8();
    /* compressedType */ r.u8();
    /* compressedLength */ r.u16le();
    return { pduType2: pduType2 };
};

// Route slow-path MCS data arriving on a virtual channel to its handler, keyed by the source channel
// id (set by _mcsSendDataIndication): drdynvc feeds the DVC manager (MS-RDPEDYC); rdpsnd/cliprdr/rdpdr
// get their CHANNEL_PDU_HEADER stripped+reassembled, then the complete payload dispatched. Returns
// true when the data was on a virtual channel (consumed), false for global/message channel data.
// Called from EVERY post-join state, not just ACTIVE — hosts start static channels as soon as they
// exist (FreeRDP-based hosts right after Client Info), so e.g. the cliprdr caps + Monitor Ready PDUs
// can arrive during licensing/capabilities/finalization; dropping them killed the clipboard handshake.
RdpProtocol.prototype._routeChannelData = function (r) {
    if (this.drdynvcChannelId && this._lastChannelId === this.drdynvcChannelId) {
        this._onDrdynvcData(r);
        return true;
    }
    const svcName = this.staticChannelById[this._lastChannelId];
    if (svcName === "rdpsnd" || svcName === "cliprdr" || svcName === "rdpdr") {
        const payload = this._reassembleSvc(svcName, r);
        if (!payload) return true; // more fragments pending
        if (svcName === "rdpsnd" && this.rdpsnd) this.rdpsnd.onData(payload);
        else if (svcName === "cliprdr" && this.cliprdr) this.cliprdr.onData(payload);
        else if (svcName === "rdpdr") this._onRdpdrData(payload);
        return true;
    }
    // Data on a static channel we joined but have no handler for (named so it's diagnosable, not a
    // silent drop). The global I/O channel returns false to the caller's share-control parsing.
    if (svcName && this._lastChannelId !== this.mcsChannelId) {
        this._log("svc: DROP " + r.remaining() + "B on unhandled static channel '" + svcName +
            "' id=" + this._lastChannelId);
        return true;
    }
    return false;
};

// Active-phase slow-path MCS data on the global I/O channel: share-control PDUs (error info, and the
// DEACTIVATE_ALL that begins a Deactivation-Reactivation Sequence after a resolution/scale change).
// Virtual-channel data was already routed by _routeChannelData before this is called.
RdpProtocol.prototype._onActiveSlowPath = function (r) {
    try {

        // Peek the share-control PDU type (low nibble of the 2nd u16). Some hosts prefix a 4-byte
        // security header before the ShareControlHeader; if the type at offset+2 isn't a known PDU
        // type, retry at offset+6 (past a leading security header). Position `r` at the real
        // ShareControlHeader before dispatching.
        const startO = r.o;
        let bodyO = startO;
        r.u16le();                       // totalLength
        let pduType = r.u16le() & 0xf;   // PDU type
        const known = (t) => t === (PDUTYPE_DEMANDACTIVE & 0xf) || t === (PDUTYPE_DATAPDU & 0xf)
            || t === (PDUTYPE_DEACTIVATEALL & 0xf);
        if (!known(pduType) && (r.b.length - startO) >= 8) {
            bodyO = startO + 4;          // skip a leading security header
            r.o = bodyO;
            r.u16le();                   // totalLength
            pduType = r.u16le() & 0xf;   // PDU type
        }
        r.o = bodyO;

        if (pduType === (PDUTYPE_DEMANDACTIVE & 0xf)) {
            // Server-initiated reactivation WITHOUT a DEACTIVATE_ALL — this host re-demands active
            // after a MONITOR_LAYOUT change. Re-confirm active + re-finalize at the new size so the
            // server resumes streaming; otherwise it waits forever for our Confirm Active and the
            // screen freezes (black, no further updates).
            this._log("RDP: Demand Active while active — reactivating");
            this._reactivating = true;
            this.state = ST.CAPABILITIES;
            return this._onDemandActiveControl(r);
        }

        const info = this._readShareDataHeader(r);
        if (info && info.deactivateAll) {
            this._log("RDP: DEACTIVATE_ALL — reactivating");
            return this._beginReactivation();
        }
        if (info && info.pduType2 === PDUTYPE2_SET_ERROR_INFO_PDU) {
            const code = r.u32le();
            this._log("RDP: server error info 0x" + code.toString(16));
            const reason = errorInfoReason(code);
            // A non-zero error info during the ACTIVE phase means the host is ending the session (a
            // user logoff, admin disconnect, timeout, or a protocol error). The host then closes its
            // TCP socket — but lazily. Close now so the UI doesn't sit on a frozen screen meanwhile.
            if (reason) this._close(reason.graceful, reason.message);
        }
    } catch (e) {
        this._err(e && e.message ? e.message : String(e));
    }
};

// Fastpath output PDU (server → client) during the active phase: the payload is a sequence of
// fastpath update PDUs. We forward each update (header + body) to client.js for rendering. During
// the handshake, fastpath output is not expected, but if it arrives we ignore it.
RdpProtocol.prototype._handleFastPath = function (fpHeader, payload) {
    const flags = (fpHeader >> 6) & 0x3;
    if (flags & 0x1) { this._err("fastpath secure checksum not supported"); return; }
    if (flags & 0x2) { this._err("fastpath encryption not supported"); return; }
    // Render once we've sent Confirm Active (FINALIZATION) or are fully ACTIVE. The server begins
    // streaming graphics right after the capabilities exchange and may interleave them with the
    // finalization PDUs (especially during a Deactivation-Reactivation) — dropping them here is what
    // makes the screen freeze after a resize.
    if (this.state !== ST.ACTIVE && this.state !== ST.FINALIZATION) {
        this._log("fastpath: DROP " + payload.length + "B output update in state " + this.state +
            " (before FINALIZATION)");
        return;
    }
    // Hand the whole updates blob to the renderer; client.js walks the individual updates.
    const copy = payload.slice(); // detach from the reassembly buffer
    if (this.cb.onUpdate) this.cb.onUpdate(copy.buffer.slice(copy.byteOffset, copy.byteOffset + copy.byteLength));
};

// Send a fastpath input PDU carrying one already-serialized input event (the event includes its own
// per-event header). Wraps it with the fastpath input header + PER-style length.
RdpProtocol.prototype.sendInputEvent = function (eventBytes) {
    if (this.state !== ST.ACTIVE) return;
    const ev = (eventBytes instanceof Uint8Array) ? eventBytes : new Uint8Array(eventBytes);
    const numEvents = 1;
    const fpInputHeader = (0 & 0x3) | ((numEvents & 0xf) << 2) | ((0 & 0x3) << 6);
    const w = new ByteWriter();
    w.u8(fpInputHeader);
    // `length` = header byte + event data, excluding the length byte(s) themselves ([MS-RDPBCGR]
    // 2.2.8.1.2 fastpath input PDU length, matching fastpath/send.go). The length field then adds
    // its own size (1 or 2 bytes) on top.
    const length = 1 + ev.length;
    if (length > 0x7f) {
        w.u16be((length + 2) | 0x8000);
    } else {
        w.u8(length + 1);
    }
    w.bytes(ev);
    this.t.send(w.toArray());
};

// Send a fastpath INPUT SYNC event (FASTPATH_INPUT_EVENT_SYNC, eventCode 3) — toggle-key state sync.
// mstsc sends this right after the session activates (observed in a MITM capture: a 3-byte fastpath PDU,
// eventHeader 0x62) before the host begins flooding GFX frames. We send it on activation as a
// proactive "interactive client present" signal. The event is just the 1-byte eventHeader:
// (eventCode 3 << 5) | (toggleFlags = 0).
RdpProtocol.prototype.sendInputSync = function () {
    if (this.state !== ST.ACTIVE) return;
    this.sendInputEvent(new Uint8Array([(3 << 5) | 0]));
};

// Send a TS_REFRESH_RECT_PDU asking the host to repaint the given rect (default: whole desktop). Used
// as a watchdog nudge: this host stops streaming GFX once it thinks the surface is static, and a
// refresh-rect forces it to re-encode and resume sending WIRE_TO_SURFACE updates.
RdpProtocol.prototype.sendRefreshRect = function (width, height) {
    if (this.state !== ST.ACTIVE) return;
    const w = width || this.width || 1;
    const h = height || this.height || 1;
    this.t.send(tpktX224Wrap(mcsSendDataSerialize(this.userId, this.mcsChannelId,
        refreshRectPdu(this.shareID, this.userId, w, h))));
};

// ================================================================================================
// Static virtual channel send (CHANNEL_PDU_HEADER) + drdynvc dynamic virtual channels (MS-RDPEDYC)
// ================================================================================================
const CHANNEL_FLAG_FIRST = 0x00000001;
const CHANNEL_FLAG_LAST = 0x00000002;
const CHANNEL_FLAG_SHOW_PROTOCOL = 0x00000010;
// Max bytes of channel data per Virtual Channel PDU ([MS-RDPBCGR] 2.2.6.1 CHANNEL_CHUNK_LENGTH).
// Messages larger than this MUST be split into FIRST..LAST chunks or the host drops the channel.
const CHANNEL_CHUNK_LENGTH = 1600;
// CHANNEL_PDU_HEADER compression flags ([MS-RDPBCGR] 2.2.6.1.1).
const CHANNEL_PACKET_COMPRESSED = 0x00200000;
const CHANNEL_PACKET_AT_FRONT = 0x00400000;
const CHANNEL_PACKET_FLUSHED = 0x00800000;
const CHANNEL_COMPRESSION_TYPE_MASK = 0x000F0000;

// drdynvc command codes ([MS-RDPEDYC] 2.2).
const DVC_CMD_CREATE = 0x01;
const DVC_CMD_DATA_FIRST = 0x02;
const DVC_CMD_DATA = 0x03;
const DVC_CMD_CLOSE = 0x04;
const DVC_CMD_CAPABILITIES = 0x05;
// Version 3 commands we do NOT implement — we respond v1 in _dvcOnCapabilities so the host never uses
// them. Named only so the switch can log loudly (rather than silently drop) if a host ever sends one,
// which would otherwise present as a silent GFX freeze.
const DVC_CMD_DATA_FIRST_COMPRESSED = 0x06;
const DVC_CMD_DATA_COMPRESSED = 0x07;
const DVC_CMD_SOFT_SYNC_REQUEST = 0x08;
const DVC_CMD_SOFT_SYNC_RESPONSE = 0x09;

const DISPLAY_CONTROL_CHANNEL_NAME = "Microsoft::Windows::RDS::DisplayControl";
// Dynamic-channel name for remote audio output (MS-RDPEA over MS-RDPEDYC). Modern Windows streams
// rdpsnd through this DVC rather than the static "rdpsnd" channel.
const AUDIO_PLAYBACK_DVC_NAME = "AUDIO_PLAYBACK_DVC";
// Dynamic-channel name for microphone redirection (MS-RDPEAI). The host opens this to capture the
// client microphone; we accept it only when opts.microphone is set and bind it to AudInput.
const AUDIO_INPUT_DVC_NAME = "AUDIO_INPUT";
// Camera redirection (MS-RDPECAM): the host-opened control/enumerator DVC. After version negotiation
// we advertise a virtual camera; the host then opens a per-device DVC named by our VirtualChannelName.
const RDPECAM_CONTROL_DVC_NAME = "RDCamera_Device_Enumerator";
// RDPEGFX graphics pipeline DVC ([MS-RDPEGFX]). We accept it (and answer its capability exchange)
// only to unlock the host's rich DVC set (which carries audio). Surface graphics are not yet
// rendered — see _onGfxData.
const RDPGFX_DVC_NAME = "Microsoft::Windows::RDS::Graphics";
// MS-RDPEVOR (Video Optimized Remoting) + Geometry channels. A MITM capture of mstsc vs this host showed
// mstsc ACCEPTS these (and every other DVC the host offers — zero rejects), and the host then streams GFX
// H.264 CONTINUOUSLY. When we reject them, the host sets up Graphics but won't sustain the video stream
// (it stalls after the first frames). We accept them to keep the GFX video pipeline alive; we don't need
// to send data back (these are host→client coordination channels). Prefix match handles the v08.01 suffix.
const RDPEVOR_CONTROL_PREFIX = "Microsoft::Windows::RDS::Video::Control";
const RDPEVOR_DATA_PREFIX = "Microsoft::Windows::RDS::Video::Data";
const RDPEGT_GEOMETRY_PREFIX = "Microsoft::Windows::RDS::Geometry";

// Wrap a virtual-channel payload in a CHANNEL_PDU_HEADER and send it on `channelId` via MCS
// send-data-request, splitting into CHANNEL_CHUNK_LENGTH chunks when needed (clipboard text can
// exceed one chunk; each chunk's length field carries the TOTAL uncompressed message length).
RdpProtocol.prototype._sendOnChannel = function (channelId, payload, compress) {
    // Channels advertised with CHANNEL_OPTION_SHOW_PROTOCOL (cliprdr) MUST have SHOW_PROTOCOL set in
    // every Channel PDU ([MS-RDPBCGR] 3.1.5.2.2, FreeRDP channels.c) — rdpclip expects the header
    // visible; without it Windows hosts misparse every client→server clipboard PDU.
    const chName = this.staticChannelById ? this.staticChannelById[channelId] : null;
    const showProto = (chName && CHANNEL_DEFS[chName] &&
        (CHANNEL_DEFS[chName].options & CHANNEL_OPTION_SHOW_PROTOCOL)) ? CHANNEL_FLAG_SHOW_PROTOCOL : 0;

    // Optionally RDP-bulk (MPPC) compress, matching the macOS app's wire form (it sends GFX caps with
    // CHANNEL flags 0x600003 = FIRST|LAST|COMPRESSED|AT_FRONT, 8K/RDP4). The host decompresses to the
    // same bytes; the declared length stays the UNCOMPRESSED length. Only used where the macOS app
    // compresses (small single-chunk payloads), so the compressed path never needs chunking.
    if (compress && this._mppcSend) {
        const c = this._mppcSend.compress(payload);
        let flags = CHANNEL_FLAG_FIRST | CHANNEL_FLAG_LAST | showProto | CHANNEL_PACKET_COMPRESSED;
        if (c.flags & 0x40) flags |= CHANNEL_PACKET_AT_FRONT; // 0x00400000
        if (c.flags & 0x80) flags |= CHANNEL_PACKET_FLUSHED;  // 0x00800000
        // compressionType (0x000F0000 mask) = 0 → PACKET_COMPR_TYPE_8K, matching the macOS app.
        const w = new ByteWriter();
        w.u32le(payload.length);
        w.u32le(flags >>> 0);
        w.bytes(c.data);
        this.t.send(tpktX224Wrap(mcsSendDataSerialize(this.userId, channelId, w.toArray())));
        return;
    }

    for (let off = 0; off === 0 || off < payload.length; off += CHANNEL_CHUNK_LENGTH) {
        const chunk = payload.subarray(off, Math.min(off + CHANNEL_CHUNK_LENGTH, payload.length));
        let flags = showProto;
        if (off === 0) flags |= CHANNEL_FLAG_FIRST;
        if (off + chunk.length >= payload.length) flags |= CHANNEL_FLAG_LAST;
        const w = new ByteWriter();
        w.u32le(payload.length);                // CHANNEL_PDU_HEADER.length (whole-message total)
        w.u32le(flags >>> 0);
        w.bytes(chunk);
        this.t.send(tpktX224Wrap(mcsSendDataSerialize(this.userId, channelId, w.toArray())));
    }
};

// Reassemble a static virtual channel message ([MS-RDPBCGR] 2.2.6.1): each MCS send carries a
// CHANNEL_PDU_HEADER {length(4), flags(4)} then a chunk of the message; FIRST..LAST flags delimit a
// message split across chunks. Returns the complete payload Uint8Array when LAST arrives, else null.
// `r` is positioned at the CHANNEL_PDU_HEADER.
RdpProtocol.prototype._reassembleSvc = function (name, r) {
    const totalLen = r.u32le();          // CHANNEL_PDU_HEADER.length (whole message)
    const flags = r.u32le();             // CHANNEL_FLAG_FIRST / _LAST (+ compression flags)
    let chunk = r.bytes(r.remaining()).slice(); // detach from the rx buffer

    // RDP-bulk decompress if the host compressed this chunk (we advertised INFO_COMPRESSION to match the
    // macOS app, so the host may MPPC-compress slow-path data to us). Map the 32-bit channel flags to the
    // MPPC low-byte form (COMPRESSED 0x20, AT_FRONT 0x40, FLUSHED 0x80).
    if ((flags & CHANNEL_PACKET_COMPRESSED) && this._mppcRecv) {
        let mflags = 0x20;
        if (flags & CHANNEL_PACKET_AT_FRONT) mflags |= 0x40;
        if (flags & CHANNEL_PACKET_FLUSHED) mflags |= 0x80;
        const out = this._mppcRecv.decompress(chunk, mflags);
        if (out) chunk = out;
        else this._log("svc: MPPC decompress failed on '" + name + "' (" + chunk.length + "B)");
    }

    const first = (flags & CHANNEL_FLAG_FIRST) !== 0;
    const last = (flags & CHANNEL_FLAG_LAST) !== 0;

    if (first && last) return chunk;     // single-chunk (the common case)

    if (first) { this._svcReasm[name] = { parts: [chunk], len: chunk.length, total: totalLen }; return null; }
    const acc = this._svcReasm[name];
    if (!acc) return null;               // a NEXT/LAST without a FIRST; drop
    acc.parts.push(chunk); acc.len += chunk.length;
    if (!last) return null;

    const out = new Uint8Array(acc.len);
    let off = 0;
    for (const p of acc.parts) { out.set(p, off); off += p.length; }
    this._svcReasm[name] = null;
    return out;
};

// Encode a drdynvc channelId field of the smallest size, returning {cbId, bytes}.
function dvcEncodeChannelId(id) {
    if (id <= 0xff) return { cbId: 0, bytes: [id & 0xff] };
    if (id <= 0xffff) return { cbId: 1, bytes: [id & 0xff, (id >> 8) & 0xff] };
    return { cbId: 2, bytes: [id & 0xff, (id >> 8) & 0xff, (id >> 16) & 0xff, (id >> 24) & 0xff] };
}
// Read a drdynvc channelId of cbId-encoded width (0->1B, 1->2B, 2->4B), little-endian.
function dvcReadChannelId(r, cbId) {
    if (cbId === 0) return r.u8();
    if (cbId === 1) return r.u16le();
    return r.u32le();
}
// Encode a channelId at an *explicit* cbId width (little-endian). The drdynvc spec requires responses
// to echo the same channel-id width the server used in the request — using a narrower form (e.g. 1
// byte for id 18 when the server sent 2) desynchronizes the server and stalls the channel.
function dvcEncodeChannelIdFixed(id, cbId) {
    if (cbId === 0) return [id & 0xff];
    if (cbId === 1) return [id & 0xff, (id >> 8) & 0xff];
    return [id & 0xff, (id >> 8) & 0xff, (id >> 16) & 0xff, (id >> 24) & 0xff];
}

// Build a drdynvc PDU: first byte = (cmd<<4)|(sp<<2)|cbId, followed by the channelId (at the given
// cbId width) then body. cbId defaults to the smallest that fits when not specified.
function dvcBuildPdu(cmd, channelId, body, sp, cbId) {
    let idBytes;
    if (cbId === undefined) { const enc = dvcEncodeChannelId(channelId); cbId = enc.cbId; idBytes = enc.bytes; }
    else { idBytes = dvcEncodeChannelIdFixed(channelId, cbId); }
    const w = new ByteWriter();
    w.u8(((cmd & 0xf) << 4) | (((sp || 0) & 0x3) << 2) | (cbId & 0x3));
    w.bytes(idBytes);
    if (body && body.length) w.bytes(body);
    return w.toArray();
}

// drdynvc per-PDU chunk limit ([MS-RDPEDYC]; FreeRDP CHANNEL_CHUNK_LENGTH). A DVC DATA payload larger
// than what fits in one chunk MUST be split into DATA_FIRST (carrying the total length) + DATA chunks.
const DVC_CHUNK_LENGTH = 1600;

// Send a DVC DATA payload on `channelId`, fragmenting into DATA_FIRST + DATA* exactly like FreeRDP's
// drdynvc_write_data when it exceeds one chunk. Each emitted drdynvc PDU is wrapped by _sendOnChannel
// (CHANNEL_PDU_HEADER over MCS). Small payloads (control PDUs, audio) send as a single DATA PDU.
RdpProtocol.prototype._sendDvcData = function (channelId, cbId, payload, compress) {
    const enc = dvcEncodeChannelId(channelId);
    // Header(1) + channelId bytes, computed for a plain DATA PDU.
    const dataHeaderLen = 1 + enc.bytes.length;
    if (payload.length <= DVC_CHUNK_LENGTH - dataHeaderLen) {
        // `compress` MPPC-compresses the whole drdynvc DATA PDU at the CHANNEL layer (matches the macOS
        // app's GFX CAPS_ADVERTISE, sent with CHANNEL flags 0x600003). Only for single-chunk PDUs.
        this._sendOnChannel(this.drdynvcChannelId, dvcBuildPdu(DVC_CMD_DATA, channelId, payload, 0, cbId), compress);
        return;
    }
    // DATA_FIRST: header byte packs (cmd<<4)|(cbLen<<2)|cbId; then channelId, then the total length
    // (variable width = cbLen), then the first chunk. cbLen sizing mirrors dvcEncodeChannelId.
    const total = payload.length;
    let cbLen, lenBytes;
    if (total <= 0xff) { cbLen = 0; lenBytes = [total & 0xff]; }
    else if (total <= 0xffff) { cbLen = 1; lenBytes = [total & 0xff, (total >> 8) & 0xff]; }
    else { cbLen = 2; lenBytes = [total & 0xff, (total >> 8) & 0xff, (total >> 16) & 0xff, (total >> 24) & 0xff]; }

    const idBytes = dvcEncodeChannelIdFixed(channelId, cbId);
    const firstHeaderLen = 1 + idBytes.length + lenBytes.length;
    let off = 0;
    {
        const w = new ByteWriter();
        w.u8(((DVC_CMD_DATA_FIRST & 0xf) << 4) | ((cbLen & 0x3) << 2) | (cbId & 0x3));
        w.bytes(idBytes);
        w.bytes(lenBytes);
        const n = Math.min(DVC_CHUNK_LENGTH - firstHeaderLen, total - off);
        w.bytes(payload.subarray(off, off + n));
        off += n;
        this._sendOnChannel(this.drdynvcChannelId, w.toArray());
    }
    // Remaining DATA chunks.
    while (off < total) {
        const w = new ByteWriter();
        w.u8(((DVC_CMD_DATA & 0xf) << 4) | (cbId & 0x3));
        w.bytes(idBytes);
        const n = Math.min(DVC_CHUNK_LENGTH - dataHeaderLen, total - off);
        w.bytes(payload.subarray(off, off + n));
        off += n;
        this._sendOnChannel(this.drdynvcChannelId, w.toArray());
    }
};

// Inbound data on the drdynvc static channel: strip the CHANNEL_PDU_HEADER then dispatch the
// drdynvc command. (We assume single-chunk channel PDUs, which is the case for the small control
// PDUs we exchange.)
RdpProtocol.prototype._onDrdynvcData = function (r) {
    /* CHANNEL_PDU_HEADER.length */ r.u32le();
    /* flags */ r.u32le();

    const header = r.u8();
    const cmd = (header >> 4) & 0xf;
    const sp = (header >> 2) & 0x3;
    const cbId = header & 0x3;

    switch (cmd) {
        case DVC_CMD_CAPABILITIES: return this._dvcOnCapabilities(r);
        case DVC_CMD_CREATE: return this._dvcOnCreate(r, cbId);
        case DVC_CMD_DATA: return this._dvcOnData(r, cbId, false, sp);
        case DVC_CMD_DATA_FIRST: return this._dvcOnData(r, cbId, true, sp);
        case DVC_CMD_CLOSE: return this._dvcOnClose(r, cbId);
        // v3 compressed data ([MS-RDPEDYC] 2.2.3.3 / 2.2.3.4): same as DATA_FIRST/DATA but the body is
        // an RDP8_LITE-compressed ZGFX segment. _dvcOnData inflates it before reassembly/dispatch.
        case DVC_CMD_DATA_FIRST_COMPRESSED: return this._dvcOnData(r, cbId, true, sp, true);
        case DVC_CMD_DATA_COMPRESSED: return this._dvcOnData(r, cbId, false, sp, true);
        // Soft-Sync: only reachable if SOFTSYNC_TCP_TO_UDP was negotiated, which we never advertise — so
        // this should not occur. Log loudly rather than drop silently (a silent drop would present as a
        // frozen-but-connected session).
        case DVC_CMD_SOFT_SYNC_REQUEST:
        case DVC_CMD_SOFT_SYNC_RESPONSE:
            this._log("drdynvc: UNEXPECTED Soft-Sync cmd 0x" + cmd.toString(16) +
                " (we never advertised multitransport) — ignoring");
            return;
        default:
            this._log("drdynvc: ignoring cmd " + cmd);
    }
};

// Capabilities Request -> Capabilities Response.
//
// [MS-RDPEDYC] 3.2.3.1: "the client MUST respond ... indicating the HIGHEST version level supported by
// the CLIENT." The server advertises the highest IT supports and then ADAPTS its feature use to what
// the client claims:
//   v1 — uncompressed DATA_FIRST(0x02)/DATA(0x03) only.
//   v2 — adds priority classes (PriorityCharge fields; informational for bandwidth allocation).
//   v3 — adds COMPRESSED DVC data (DATA_FIRST_COMPRESSED 0x06 / DATA_COMPRESSED 0x07) and Soft-Sync.
//
// This host REQUIRES v3: when we answered v1 it created the Graphics DVC and then sent NOTHING (it only
// streams GFX over compressed DVC PDUs). So we MUST claim v3. We implement the compressed-data half
// (inbound 0x06/0x07 ZGFX-decompressed in _dvcOnData). Soft-Sync (0x08/0x09) is NOT a concern: per
// [MS-RDPEDYC] 3.1.5.3 it MUST NOT be used unless BOTH peers set SOFTSYNC_TCP_TO_UDP in their
// Multitransport Channel Data — we never advertise multitransport, so the host cannot initiate it.
// We never SEND compressed PDUs ourselves (our c2s traffic is small); claiming v3 only obligates us to
// RECEIVE them, which we now do.
const DVC_CLIENT_MAX_VERSION = 3;
RdpProtocol.prototype._dvcOnCapabilities = function (r) {
    // Request body: pad(1) version(2) [+ per-priority charges]. We read the server's advertised version
    // only for logging; the RESPONSE version is capped at what we actually implement.
    /* pad */ r.u8();
    let serverVersion = 1;
    if (r.remaining() >= 2) serverVersion = r.u16le();
    const version = Math.min(serverVersion < 1 ? 1 : serverVersion, DVC_CLIENT_MAX_VERSION);
    this._log("drdynvc: capabilities server=v" + serverVersion + " -> responding v" + version);

    const body = new ByteWriter();
    body.u8(((DVC_CMD_CAPABILITIES & 0xf) << 4)); // cmd, sp=0, cbId=0
    body.u8(0x00);          // pad
    body.u16le(version);    // Version (highest WE support)
    this._sendOnChannel(this.drdynvcChannelId, body.toArray());
};

// Create Request: the server opens a dynamic channel by name. Accept it (status 0). We remember the
// Display Control channel so we can drive resolution/scale; other channels are accepted but unused.
RdpProtocol.prototype._dvcOnCreate = function (r, cbId) {
    const channelId = dvcReadChannelId(r, cbId);
    // Channel name: null-terminated ASCII to end of PDU.
    let name = "";
    while (r.remaining() > 0) {
        const c = r.u8();
        if (c === 0) break;
        name += String.fromCharCode(c);
    }
    // Accept the channels we actually implement: Display Control (live resolution/scale via
    // MS-RDPEDISP) and, when audio is enabled, the audio output channel (AUDIO_PLAYBACK_DVC —
    // modern Windows streams rdpsnd here). EVERY OTHER dynamic channel (ECHO, CoreInput, Video,
    // Geometry, Input, TextInput, …) is REJECTED with a failure creationStatus, so the server keeps
    // that functionality on the legacy fastpath path (which we render/handle) instead of routing it
    // through a DVC and then stalling while it waits for responses we can't produce (e.g. ECHO
    // keepalives). Accepting channels we don't service is what made the server go silent.
    const isAudio = (name === AUDIO_PLAYBACK_DVC_NAME) && this.opts.audio;
    // Microphone (audio input). Only accept when enabled — otherwise the host would wait on capture
    // PDUs we never send. Like the output DVC, the host opens this dynamically.
    const isAudin = (name === AUDIO_INPUT_DVC_NAME) && this.opts.microphone;
    // Camera: the control/enumerator channel (when enabled), and the per-device channel the host opens
    // by the VirtualChannelName we advertised on the enumerator (camDeviceChannelName, set dynamically).
    const isCamEnum = (name === RDPECAM_CONTROL_DVC_NAME) && this.opts.camera;
    const isCamDevice = !!this.camDeviceChannelName && (name === this.camDeviceChannelName);
    // Accept the RDPEGFX Graphics channel only to keep the host's rich DVC set (incl. audio) alive.
    // We answer its capability exchange but do not render its surfaces yet.
    const isGfx = (name === RDPGFX_DVC_NAME);
    // Geometry tracking (MS-RDPEGT): when GFX is on, accept it like mstsc does. CRITICAL — a corrected
    // MITM decode (drdynvc CREATE_RSP on the static channel mstsc actually uses) showed mstsc ACCEPTS
    // only Graphics + Geometry and REJECTS Video::Control + Video::Data (status 0xC0000001). My earlier
    // "accept all video DVCs" reading was from a mis-parsed channel and was WRONG — accepting
    // Video::Control/Data does NOT match mstsc. So accept Geometry only; Video::Control/Data fall through
    // to the reject path below.
    // Geometry (MS-RDPEGT): ACCEPT. A byte-diff of our_c2s vs the macOS app's c2s (the source of truth)
    // showed the macOS app ACCEPTS the Geometry channel (CREATE_RSP id 0x0c → status 0) where we were
    // rejecting it (0xC0000001). Match it. (An earlier "reject all Geometry" experiment was wrong vs the
    // macOS app and did NOT fix the stall anyway.)
    const isGeometry = !!this.gfx && name.indexOf(RDPEGT_GEOMETRY_PREFIX) === 0;
    // MS-RDPEVOR Video::Control/Data: REJECT. Tested accept-and-keep-open — the host sent ZERO bytes on
    // these channels (no unhandled-channel DROP logs) and GFX still stopped after ~4 frames, so EVOR is
    // dormant and NOT the gate. Toggle accept via window.RDP_EVOR_ACCEPT=1 for re-testing.
    const isVideoEvor = !!this.gfx && (typeof window !== "undefined" && window.RDP_EVOR_ACCEPT) &&
        (name.indexOf(RDPEVOR_CONTROL_PREFIX) === 0 || name.indexOf(RDPEVOR_DATA_PREFIX) === 0);
    // DisplayControl (MS-RDPEDISP): ACCEPT. (Deferring it was tested — the host STILL did DELETE+2nd RESET
    // with DisplayControl rejected, so it is NOT the trigger. The DELETE_SURFACE happens at frame ~3-4
    // regardless; the stall is the post-recreate 137KB keyframe pausing, independent of DisplayControl.)
    const isDisplayControl = (name === DISPLAY_CONTROL_CHANNEL_NAME);
    const accept = isDisplayControl || isAudio || isAudin || isCamEnum || isCamDevice || isGfx || isGeometry || isVideoEvor;

    // The server REUSES dynamic channel ids: id 11 may be Geometry, then DisplayControl, then Geometry
    // again over the life of the session. A new Create for an id we currently hold means the server has
    // torn down whatever was on that id. If that id was our DisplayControl channel, drop our reference —
    // otherwise we'd keep sending MONITOR_LAYOUT to a now-defunct channel, which the server silently
    // ignores (the second-resize-goes-black bug). canResize() then reports false until DisplayControl is
    // re-created, instead of resizing into the void.
    if (channelId === this.displayControlChannelId && !accept) {
        this._log("drdynvc: id " + channelId + " reassigned away from DisplayControl");
        this.displayControlChannelId = null;
        this.displayControlActive = false;
        delete this.dvcById[channelId];
    }

    // creationStatus: 0 = success; for a refusal we send 0xC0000001 (STATUS_UNSUCCESSFUL) — this is the
    // EXACT code a real mstsc sends against this host (confirmed by a MITM capture: mstsc rejects
    // Video::Control/Video::Data/CoreInput/MouseCursor with 0xC0000001 and the host streams GFX fine).
    // (An older note preferred 0xC0000225/STATUS_NOT_FOUND for a legacy-bitmap resize bug; the GFX MITM
    // ground truth is 0xC0000001. If the live-resize regression returns, revisit per-channel.)
    const status = new ByteWriter().u32le(accept ? 0x00000000 : 0xC0000001).toArray();
    const pdu = dvcBuildPdu(DVC_CMD_CREATE, channelId, status, 0, cbId);
    this._sendOnChannel(this.drdynvcChannelId, pdu);

    if (accept) {
        this.dvcByName[name] = channelId;
        this.dvcById[channelId] = name;
        this._log("drdynvc: accepted '" + name + "' id=" + channelId);
        if (isAudio) {
            this.audioDvcChannelId = channelId;
            this.audioDvcCbId = cbId; // echo the same channel-id width when we send DATA back
            this._initAudioDvc(channelId, cbId);
        } else if (isAudin) {
            this.audinDvcChannelId = channelId;
            this.audinDvcCbId = cbId; // echo the same channel-id width when we send DATA back
            this._initAudinDvc(channelId, cbId);
        } else if (isCamEnum) {
            this.camEnumChannelId = channelId;
            this.camEnumCbId = cbId;
            this._initCamEnumDvc(channelId, cbId);
        } else if (isCamDevice) {
            this._initCamDeviceDvc(channelId, cbId);
        } else if (isGfx) {
            this.gfxDvcChannelId = channelId;
            this.gfxDvcCbId = cbId;
            this._initGfxDvc(channelId, cbId);
        } else if (isGeometry) {
            // Geometry (MS-RDPEGT): ACCEPT and KEEP OPEN. A fresh MITM capture (2026-06-23) of the working
            // mstsc/macOS session against this host shows the client NEVER closes Geometry — only the
            // SERVER sends DVC CLOSE on these channels, on its own schedule. Our previous "accept-then-
            // close" sent a client CLOSE the host didn't expect; the host then re-opened Geometry, we
            // closed it again, and this Create/Close war on the drdynvc static channel coincided exactly
            // with the GFX stall (host stops mid frame-4 DataFirst). So just accept it (CREATE_RSP status 0,
            // already sent above) and leave it open — the host closes it when done. (The old comment
            // claiming the macOS app sends CLOSE was wrong; verified against the capture.)
            // No-op: the accept (CREATE_RSP status 0) and dvcById registration already happened above;
            // we just don't send a client CLOSE. The host closes the channel when it's done with it.
        } else if (isVideoEvor) {
            // MS-RDPEVOR Video::Control/Data: accept and keep OPEN (host→client coordination; we don't
            // send data back). Just remembered in dvcById; inbound PDUs fall to the unhandled-channel log.
            this._log("drdynvc: kept '" + name + "' id=" + channelId + " OPEN (EVOR video — GFX sustain test)");
        } else {
            this.displayControlChannelId = channelId;
            this.displayControlCbId = cbId;
            if (this.cb.onDisplayControlReady) this.cb.onDisplayControlReady();
        }
    } else {
        this._log("drdynvc: rejected '" + name + "' id=" + channelId);
    }
};

// RDPEGFX ([MS-RDPEGFX]) Graphics DVC. With the GFX path on (rdpTryGfx) we stand up a full RdpGfx
// surface compositor (H.264/AVC420 decode via WebCodecs) and advertise H.264-capable capsets; the
// host then streams the desktop as a video surface instead of legacy bitmaps. With it off we send a
// minimal v8 CAPS_ADVERTISE only to keep the channel (and the host's audio DVC) alive.
// (RDPGFX_CMDID_* / RDPGFX_CAPVERSION_* constants live in rdpgfx.js, which loads before protocol.js.)

// Stand up the RdpGfx compositor for this graphics channel (if the module is loaded and GFX is on)
// and send its CAPS_ADVERTISE. Inbound graphics data routes here via _onGfxData.
RdpProtocol.prototype._initGfxDvc = function (channelId, cbId) {
    if (rdpTryGfx() && typeof RdpGfx !== "undefined") {
        const self = this;
        const mode = rdpGfxMode();
        this.gfx = new RdpGfx({
            mode: mode,
            onLog: function (m) { self._log(m); },
            // Forward a complete RDPGFX PDU back to the host on the graphics DVC. The client sends
            // uncompressed (no ZGFX), so the payload is the bare PDU — the DVC fragmenter wraps it.
            send: function (payload) { self._sendDvcData(channelId, cbId, payload); },
            onReset: function (w, h) { if (self.cb.onGfxReset) self.cb.onGfxReset(w, h); },
            onPaint: function (canvas, sx, sy, sw, sh, dx, dy) {
                if (self.cb.onGfxPaint) self.cb.onGfxPaint(canvas, sx, sy, sw, sh, dx, dy);
            },
            onDirectFrame: function (frame, surfaceId, map) {
                if (self.cb.onGfxDirectFrame) self.cb.onGfxDirectFrame(frame, surfaceId, map);
            },
        });
        // Send CAPS_ADVERTISE UNCOMPRESSED. (We previously MPPC-compressed it at the static-channel layer
        // with compress=true to mirror the macOS app's wire form. That was fine under a v1 DVC cap, but
        // once we negotiate DVC v3 the host stopped responding to the static-layer-compressed caps — it
        // accepted the Graphics channel and then went silent with NO CAPS_CONFIRM. Sending the caps
        // uncompressed is always valid: the host decompresses nothing and confirms. If a 1:1 wire match
        // with the macOS app is needed again, it must be done via DVC-layer compression, not static MPPC.)
        this._sendDvcData(channelId, cbId, this.gfx.buildCapsAdvertise(), false);
        this._log("rdpgfx: GFX rendering ENABLED (mode=" + mode + ")");
        return;
    }
    this._gfxAdvertiseCaps(channelId, cbId);
};

// Minimal v8 CAPS_ADVERTISE (no rendering) — keeps the Graphics channel alive to unlock audio.
RdpProtocol.prototype._gfxAdvertiseCaps = function (channelId, cbId) {
    const caps = new ByteWriter();
    caps.u16le(1);                       // capsSetCount
    caps.u32le(0x00080004);              // version: RDPGFX_CAPVERSION_8
    caps.u32le(4);                       // capsDataLength
    caps.u32le(0x00000000);              // capsData: RDPGFX_CAPSET_VERSION8.flags = 0
    const capsArr = caps.toArray();

    const pdu = new ByteWriter();
    pdu.u16le(0x0012);                     // cmdId: RDPGFX_CMDID_CAPSADVERTISE
    pdu.u16le(0x0000);                     // flags
    pdu.u32le(8 + capsArr.length);         // pduLength (header 8 + body)
    pdu.bytes(capsArr);

    const dvc = dvcBuildPdu(DVC_CMD_DATA, channelId, pdu.toArray(), 0, cbId);
    this._sendOnChannel(this.drdynvcChannelId, dvc);
    this._log("rdpgfx: sent caps advertise (v8) — surfaces not rendered (legacy bitmap path)");
};

// Build the RdpSnd handler bound to the audio DVC. Its send() wraps each rdpsnd PDU as a drdynvc
// DATA PDU on the audio channel (then a CHANNEL_PDU_HEADER on drdynvc, added by _sendOnChannel).
RdpProtocol.prototype._initAudioDvc = function (channelId, cbId) {
    const self = this;
    if (typeof RdpSnd === "undefined") return;
    this.rdpsnd = new RdpSnd(
        function (payload) {
            const dvc = dvcBuildPdu(DVC_CMD_DATA, channelId, payload, 0, cbId);
            self._sendOnChannel(self.drdynvcChannelId, dvc);
        },
        { onLog: function (m) { self._log("rdpsnd[dvc]: " + m); },
          onWave: function (fmt, pcm) { if (self.cb.onAudio) self.cb.onAudio(fmt, pcm); },
          onFormats: function (fmts) { if (self.cb.onAudioFormats) self.cb.onAudioFormats(fmts); } });
    this._log("rdpsnd: bound to AUDIO_PLAYBACK_DVC id=" + channelId);
};

// Build the AudInput (microphone) handler bound to the audio-input DVC. Its send() wraps each SNDIN
// PDU as a drdynvc DATA PDU on the audio-input channel (then CHANNEL_PDU_HEADER on drdynvc). When the
// server OPENs the device, onOpen(format) fires so client.js can begin capturing; captured PCM is
// pushed back via this.audin.sendPcm().
RdpProtocol.prototype._initAudinDvc = function (channelId, cbId) {
    const self = this;
    if (typeof AudInput === "undefined") { this._log("audin: AudInput module missing"); return; }
    this.audin = new AudInput(
        // Route through the DVC fragmenter (DATA_FIRST + DATA*) — captured PCM chunks routinely exceed
        // one DVC chunk (DVC_CHUNK_LENGTH=1600); a single oversized DATA PDU makes the host kill the
        // session with ERRINFO_VC_DATA_TOO_LONG (0x112B). Small PDUs (handshake) go as one DATA.
        function (payload) { self._sendDvcData(channelId, cbId, payload); },
        { onLog: function (m) { self._log("audin: " + m); },
          onOpen: function (fmt) { if (self.cb.onMicOpen) self.cb.onMicOpen(fmt); },
          onClose: function () { if (self.cb.onMicClose) self.cb.onMicClose(); } });
    this._log("audin: bound to AUDIO_INPUT id=" + channelId);
};

// Push one chunk of captured microphone PCM (little-endian interleaved in the OPENed format) to the
// server. No-op unless the audio-input DVC is open and the server has OPENed the capture device.
RdpProtocol.prototype.sendMicPcm = function (pcm) {
    if (this.audin && this.audin.isOpen()) this.audin.sendPcm(pcm);
};

// Build the RdpCamEnum (MS-RDPECAM control channel) bound to the enumerator DVC. Send wraps each PDU
// as a DVC DATA on the enumerator channel. We kick off version negotiation immediately (FreeRDP sends
// SelectVersionRequest in the channel's OnOpen, i.e. right after the create handshake). When it
// advertises our virtual camera, onDeviceChannelName records the name so we accept the device DVC the
// host opens next.
RdpProtocol.prototype._initCamEnumDvc = function (channelId, cbId) {
    const self = this;
    if (typeof RdpCamEnum === "undefined") { this._log("rdpecam: RdpCamEnum module missing"); return; }
    this.camEnum = new RdpCamEnum(
        function (payload) { self._sendDvcData(channelId, cbId, payload); },
        { onLog: function (m) { self._log("rdpecam: " + m); },
          onDeviceChannelName: function (name) { self.camDeviceChannelName = name; } });
    this._log("rdpecam: bound enumerator id=" + channelId);
    this.camEnum.start();
};

// Build the RdpCamDevice bound to the per-device DVC the host opened by our advertised name. The
// device channel drives the capture handshake; onStart/onStop/onSampleNeeded reach client.js, which
// captures JPEG frames and feeds them back via sendCameraFrame().
RdpProtocol.prototype._initCamDeviceDvc = function (channelId, cbId) {
    const self = this;
    if (typeof RdpCamDevice === "undefined") { this._log("rdpecam: RdpCamDevice module missing"); return; }
    const dev = new RdpCamDevice(
        function (payload) { self._sendDvcData(channelId, cbId, payload); },
        { onLog: function (m) { self._log("rdpecam[dev]: " + m); },
          onStart: function (mt) { if (self.cb.onCameraStart) self.cb.onCameraStart(mt); },
          onStop: function () { self._onCamDeviceStopped(); },
          onSampleNeeded: function (idx) { if (self.cb.onCameraSampleNeeded) self.cb.onCameraSampleNeeded(idx); } });
    this.camDevices[channelId] = dev;
    this._log("rdpecam: bound device channel id=" + channelId);
};

// Fire onCameraStop only when NO device channel is still streaming (the host keeps several open).
RdpProtocol.prototype._onCamDeviceStopped = function () {
    for (const id in this.camDevices) { if (this.camDevices[id] && this.camDevices[id].isStreaming()) return; }
    if (this.cb.onCameraStop) this.cb.onCameraStop();
};

// client.js delivers a captured JPEG frame (Uint8Array). Fan it out to every device channel that has
// an outstanding SampleRequest (each consuming pipeline pulls frames independently).
RdpProtocol.prototype.sendCameraFrame = function (jpegBytes) {
    for (const id in this.camDevices) { const d = this.camDevices[id]; if (d) d.pushFrame(jpegBytes); }
};

// The media type some device channel is streaming with ({width,height,fps}), or null if none.
RdpProtocol.prototype.cameraMediaType = function () {
    for (const id in this.camDevices) { const d = this.camDevices[id]; if (d && d.isStreaming()) return d.currentMediaType; }
    return null;
};

// The format the server OPENed the microphone with ({rate, bits, channels}), or null if not capturing.
RdpProtocol.prototype.micFormat = function () {
    return (this.audin && this.audin.isOpen()) ? this.audin.openFormat : null;
};

RdpProtocol.prototype._dvcOnClose = function (r, cbId) {
    const channelId = dvcReadChannelId(r, cbId);
    const name = this.dvcById[channelId];
    if (name) { delete this.dvcByName[name]; delete this.dvcById[channelId]; }
    if (channelId === this.displayControlChannelId) this.displayControlChannelId = null;
    if (channelId === this.audioDvcChannelId) { this.audioDvcChannelId = null; this.rdpsnd = null; }
    if (channelId === this.audinDvcChannelId) {
        this.audinDvcChannelId = null;
        this.audin = null;
        if (this.cb.onMicClose) this.cb.onMicClose();
    }
    if (channelId === this.camEnumChannelId) { this.camEnumChannelId = null; this.camEnum = null; }
    if (this.camDevices[channelId]) {
        delete this.camDevices[channelId];
        this._onCamDeviceStopped();
    }
    if (channelId === this.gfxDvcChannelId) {
        this.gfxDvcChannelId = null;
        if (this.gfx) { this.gfx.destroy(); this.gfx = null; }
    }
    delete this._dvcReasm[channelId];
    delete this._dvcZgfx[channelId];
};

// DVC data (possibly fragmented via DATA_FIRST + DATA). DisplayControl PDUs are tiny (single-chunk),
// but audio waves are large and arrive as DATA_FIRST followed by DATA chunks — so reassemble per
// channel using the DATA_FIRST total-length field before dispatching a complete message.
//
// `compressed` (v3 DATA_*_COMPRESSED, [MS-RDPEDYC] 2.2.3.3/2.2.3.4): the per-chunk body is an
// RDP_SEGMENTED_DATA (ZGFX) blob compressed with RDP8_LITE (8 KB history). We ZGFX-inflate each chunk
// through a PER-CHANNEL, session-persistent context (the LZ77 history carries across chunks) BEFORE
// reassembly, so `total` (which is the total UNCOMPRESSED length) and the accumulated lengths line up.
RdpProtocol.prototype._dvcOnData = function (r, cbId, isFirst, sp, compressed) {
    const channelId = dvcReadChannelId(r, cbId);
    let total = 0;
    if (isFirst) {
        // DATA_FIRST carries a total length field whose width is encoded in sp (0->1B,1->2B,2->4B).
        if (sp === 0) total = r.u8(); else if (sp === 1) total = r.u16le(); else total = r.u32le();
    }
    let chunk = r.bytes(r.remaining()).slice();

    if (compressed) {
        const zctx = this._dvcZgfx[channelId] || (this._dvcZgfx[channelId] = new ZgfxDecode());
        const inflated = zctx.decompress(chunk);
        if (!inflated) {
            this._log("drdynvc: ZGFX(LITE) inflate failed on channel " + channelId +
                " (" + chunk.length + "B compressed) — dropping");
            delete this._dvcReasm[channelId];
            return;
        }
        // First few compressed chunks per channel: confirm the v3 path is live and the inflate ratio.
        const n = (this._dvcCompDbg = (this._dvcCompDbg || 0) + 1);
        if (n <= 8) this._log("drdynvc: v3 compressed " + (isFirst ? "FIRST" : "DATA") + " ch=" +
            channelId + " " + chunk.length + "B -> " + inflated.length + "B inflated");
        chunk = inflated.slice(); // detach: decompress() returns a view into the ctx scratch buffer
    }

    let data;
    if (isFirst) {
        if (chunk.length >= total) { data = chunk; }       // whole message in the first PDU
        else { this._dvcReasm[channelId] = { parts: [chunk], len: chunk.length, total: total }; return; }
    } else {
        const acc = this._dvcReasm[channelId];
        if (acc) {
            acc.parts.push(chunk); acc.len += chunk.length;
            if (acc.len < acc.total) return;               // more chunks pending
            data = new Uint8Array(acc.len);
            let off = 0;
            for (const p of acc.parts) { data.set(p, off); off += p.length; }
            delete this._dvcReasm[channelId];
        } else {
            data = chunk;                                   // unfragmented DATA (no prior FIRST)
        }
    }

    if (channelId === this.displayControlChannelId) return this._onDisplayControlData(data);
    if (channelId === this.audioDvcChannelId && this.rdpsnd) return this.rdpsnd.onData(data);
    if (channelId === this.audinDvcChannelId && this.audin) return this.audin.onData(data);
    if (channelId === this.camEnumChannelId && this.camEnum) return this.camEnum.onData(data);
    if (this.camDevices[channelId]) return this.camDevices[channelId].onData(data);
    if (channelId === this.gfxDvcChannelId) {
        if (this.gfx) return this.gfx.onChannelData(data); // RDPEGFX surfaces → H.264 compositor
        this._log("drdynvc: DROP " + data.length + "B on GFX channel " + channelId + " (GFX disabled)");
        return; // GFX off: channel kept alive only, surfaces ignored (legacy bitmap path renders)
    }
    // Data on a DVC channel we accepted/created but have no handler for. Silently dropping this is how
    // a host feature stalls invisibly — name the channel so it's diagnosable.
    this._log("drdynvc: DROP " + data.length + "B on unhandled channel id=" + channelId +
        " name='" + (this.dvcById[channelId] || "?") + "'");
};

// ================================================================================================
// MS-RDPEDISP Display Control
// ================================================================================================
const DISPLAYCONTROL_PDU_TYPE_CAPS = 0x00000005;
const DISPLAYCONTROL_PDU_TYPE_MONITOR_LAYOUT = 0x00000002;
const DISPLAYCONTROL_MONITOR_PRIMARY = 0x00000001;

RdpProtocol.prototype._onDisplayControlData = function (data) {
    const r = new ByteReader(data);
    if (r.remaining() < 8) return;
    const type = r.u32le();
    /* length */ r.u32le();
    if (type === DISPLAYCONTROL_PDU_TYPE_CAPS) {
        // CAPS: maxNumMonitors(4) maxMonitorAreaFactorA(4) maxMonitorAreaFactorB(4).
        this.displayMaxMonitors = r.remaining() >= 4 ? r.u32le() : 1;
        this._log("RDPEDISP: caps (maxMonitors=" + this.displayMaxMonitors + ")");
        this.displayControlActive = true;
        if (this.cb.onDisplayControlReady) this.cb.onDisplayControlReady();
    }
};

// Whether a live resolution/scale change is currently possible.
RdpProtocol.prototype.canResize = function () {
    return this.state === ST.ACTIVE && !!this.displayControlChannelId && !!this.displayControlActive;
};

// Send a DISPLAYCONTROL_MONITOR_LAYOUT_PDU describing a single primary monitor at width x height
// device pixels with the given desktop/device scale factors. Triggers a server Deactivation-
// Reactivation Sequence (handled in _beginReactivation) that resizes the session.
//
// Per [MS-RDPEDISP]: Width 200..8192 even; Height 200..8192 even; DesktopScaleFactor 100..500;
// DeviceScaleFactor one of 100/140/180.
RdpProtocol.prototype.sendMonitorLayout = function (width, height, desktopScale, deviceScale) {
    if (!this.canResize()) return false;

    width = Math.max(200, Math.min(8192, width - (width % 2)));
    height = Math.max(200, Math.min(8192, height - (height % 2)));
    desktopScale = Math.max(100, Math.min(500, desktopScale || 100));
    deviceScale = (deviceScale === 140 || deviceScale === 180) ? deviceScale : 100;

    // No-op if nothing actually changed — avoids a pointless Deactivation-Reactivation (and the brief
    // black frame it causes) when a spurious resize event fires at the same resolution/scale.
    // EXCEPTION: always send the FIRST MONITOR_LAYOUT. Under GFX this host streams the initial keyframe
    // generation, then WAITS for the client's DISPLAYCONTROL_MONITOR_LAYOUT before doing its 2nd
    // RESET_GRAPHICS (the resolution/DPI-adaptation reconfigure) and free-running the rest of the
    // stream. mstsc always sends one even when the layout matches the host's RESET_GRAPHICS size; if we
    // suppress it as a no-op (our canvas already equals the host's 2560x1606 at scale 100), the host
    // never does the 2nd reset and GFX stalls after ~3 frames. So gate the no-op on having sent at
    // least one layout already. ([MS-RDPEDISP] 1.3.3 / 2.2.2.2 DISPLAYCONTROL_MONITOR_LAYOUT_PDU.)
    if (this._monitorLayoutSent &&
        width === this.width && height === this.height &&
        desktopScale === this.desktopScaleFactor && deviceScale === this.deviceScaleFactor) {
        return false;
    }
    this._monitorLayoutSent = true;

    this.desktopScaleFactor = desktopScale;
    this.deviceScaleFactor = deviceScale;
    this.pendingWidth = width;
    this.pendingHeight = height;
    // This server applies the layout without reactivating, so adopt the new size as current (keeps the
    // no-op guard and any later Confirm Active consistent). A reactivating host re-confirms the same.
    this.width = width;
    this.height = height;

    // DISPLAYCONTROL_MONITOR_LAYOUT (40 bytes per monitor).
    const mon = new ByteWriter();
    mon.u32le(DISPLAYCONTROL_MONITOR_PRIMARY); // Flags
    mon.u32le(0);            // Left
    mon.u32le(0);            // Top
    mon.u32le(width);        // Width
    mon.u32le(height);       // Height
    mon.u32le(0);            // PhysicalWidth (0 = unspecified)
    mon.u32le(0);            // PhysicalHeight
    mon.u32le(0);            // Orientation (landscape)
    mon.u32le(desktopScale); // DesktopScaleFactor
    mon.u32le(deviceScale);  // DeviceScaleFactor
    const monArr = mon.toArray();

    const body = new ByteWriter();
    body.u32le(40);          // MonitorLayoutSize (per [MS-RDPEDISP] 2.2.2.2.1 = 40)
    body.u32le(1);           // NumMonitors
    body.bytes(monArr);
    const bodyArr = body.toArray();

    // DISPLAYCONTROL_HEADER: Type(4) Length(4) then body.
    const pdu = new ByteWriter();
    pdu.u32le(DISPLAYCONTROL_PDU_TYPE_MONITOR_LAYOUT);
    pdu.u32le(8 + bodyArr.length);
    pdu.bytes(bodyArr);

    // Wrap as a DVC DATA PDU on the Display Control channel (echoing its channel-id width), then as a
    // static-channel PDU on drdynvc.
    const dvc = dvcBuildPdu(DVC_CMD_DATA, this.displayControlChannelId, pdu.toArray(), 0, this.displayControlCbId);
    this._sendOnChannel(this.drdynvcChannelId, dvc);
    this._log("RDPEDISP: monitor layout " + width + "x" + height + " @ " + desktopScale + "%");

    // The server responds to the layout with a Deactivation-Reactivation Sequence (DEACTIVATE_ALL then
    // a fresh Demand Active at the new size), which we re-confirm in _beginReactivation. It does this
    // only because our Confirm Active advertises desktopResizeFlag=1 — without that it would silently
    // drop the layout. No refresh rect is needed: the reactivation repaints the whole desktop.
    return true;
};

// ================================================================================================
// Virtual-channel client API (clipboard out-bound)
// ================================================================================================
// Offer local clipboard text to the remote session (the remote can then paste it). No-op if the
// clipboard channel isn't active. See ClipRdr.
RdpProtocol.prototype.sendClipboardText = function (text) {
    if (this.cliprdr) this.cliprdr.setLocalText(text);
};

// ================================================================================================
// Deactivation-Reactivation Sequence ([MS-RDPBCGR] 1.3.1.3)
// ================================================================================================
// A DEACTIVATE_ALL (e.g. after a monitor-layout change) tears down the active share. The server then
// re-sends a Demand Active with the new desktop size; we re-confirm and re-finalize, keeping the same
// MCS/channel state. The canvas is resized when the new Demand Active's dimensions are known.
RdpProtocol.prototype._beginReactivation = function () {
    this.state = ST.CAPABILITIES;
    this._reactivating = true; // so _sendFinalization goes ACTIVE immediately after we re-confirm
    // Apply the pending size optimistically (it matches what we requested); the renderer resizes its
    // canvas so subsequent bitmaps land correctly. The new Demand Active will re-confirm the size.
    if (this.pendingWidth && this.pendingHeight) {
        this.width = this.pendingWidth;
        this.height = this.pendingHeight;
        if (this.cb.onResize) this.cb.onResize(this.width, this.height);
        this.pendingWidth = this.pendingHeight = 0;
    }
};
