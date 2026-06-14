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
function clientCoreData(selectedProtocol, width, height) {
    const w = new ByteWriter();
    w.u16le(0xC001); // CS_CORE
    w.u16le(216);    // length
    w.u32le(0x00080004); // version 5+
    w.u16le(width);
    w.u16le(height);
    w.u16le(0xCA01); // colorDepth RNS_UD_COLOR_8BPP
    w.u16le(0xAA03); // SASSequence
    w.u32le(0x00000409); // keyboardLayout US
    w.u32le(0xece);  // clientBuild
    // clientName[32] (UTF-16LE, padded)
    const nameW = new ByteWriter().utf16le(PROJECT_NAME).toArray();
    const name32 = new Uint8Array(32);
    name32.set(nameW.subarray(0, Math.min(nameW.length, 32)));
    w.bytes(name32);
    w.u32le(0x00000004); // keyboardType
    w.u32le(0x00000000); // keyboardSubType
    w.u32le(12);         // keyboardFunctionKey
    w.zeros(64);         // imeFileName[64]
    w.u16le(0xCA03);     // postBeta2ColorDepth
    w.u16le(0x0001);     // clientProductId
    w.u32le(0x00000000); // serialNumber
    w.u16le(0x0010);     // highColorDepth HIGH_COLOR_16BPP
    w.u16le(0x0002);     // supportedColorDepths RNS_UD_16BPP_SUPPORT
    w.u16le(0x0001);     // earlyCapabilityFlags ECF_SUPPORT_ERRINFO_PDU
    w.zeros(64);         // clientDigProductId[64]
    w.u8(0x00);          // connectionType
    w.u8(0x00);          // pad
    w.u32le(selectedProtocol >>> 0); // serverSelectedProtocol
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
// The static virtual channels we request, in order. The server returns one MCS channel id per entry
// (positionally) in SC_NET. We need "drdynvc" to carry dynamic virtual channels (MS-RDPEDYC), which
// in turn carries Display Control (MS-RDPEDISP) for live resolution/scale changes.
const STATIC_CHANNELS = ["drdynvc"];
// CHANNEL_OPTION_INITIALIZED | CHANNEL_OPTION_COMPRESS_RDP | CHANNEL_OPTION_SHOW_PROTOCOL
const DRDYNVC_OPTIONS = 0x80000000 | 0x00800000 | 0x00200000;

function clientNetworkData() {
    const w = new ByteWriter();
    const count = STATIC_CHANNELS.length;
    w.u16le(0xC003);            // CS_NET
    w.u16le(8 + count * 12);    // header(4) + channelCount(4) + count*ChannelDef(12)
    w.u32le(count);             // channelCount
    for (const name of STATIC_CHANNELS) {
        // ChannelDef: 7 ANSI chars + NUL (8 bytes) then options(4)
        const nameBytes = new Uint8Array(8);
        for (let i = 0; i < name.length && i < 7; i++) nameBytes[i] = name.charCodeAt(i) & 0xff;
        w.bytes(nameBytes);
        w.u32le(DRDYNVC_OPTIONS >>> 0);
    }
    return w.toArray();
}
function clientUserData(selectedProtocol, width, height) {
    const w = new ByteWriter();
    w.bytes(clientCoreData(selectedProtocol, width, height));
    w.bytes(clientSecurityData());
    w.bytes(clientNetworkData());
    return w.toArray();
}

// Parse the server user data (after GCC) to recover the global (I/O) MCS channel id and the
// SC_CORE earlyCapabilityFlags (for RNS_UD_SC_SKIP_CHANNELJOIN_SUPPORTED).
function parseServerUserData(r) {
    const out = { mcsChannelId: 1003, skipChannelJoin: false, channelIds: [] };
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
                // One id per requested static virtual channel, positionally matching STATIC_CHANNELS.
                for (let i = 0; i < channelCount; i++) out.channelIds.push(r.u16le());
                break;
            }
            // SC_SECURITY (0x0C02), SC_MSGCHANNEL (0x0C04), SC_MULTITRANSPORT (0x0C08): skipped.
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
const INFO_ENABLEWINDOWSKEY = 0x00000100;

function clientInfoPdu(domain, username, password) {
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
    info.u32le(INFO_MOUSE | INFO_UNICODE | INFO_AUTOLOGON | INFO_DISABLECTRLALTDEL | INFO_ENABLEWINDOWSKEY);
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
    // ExtendedInfoPacket
    info.u16le(0x0002); // clientAddressFamily AF_INET
    info.u16le(2); info.u16le(0); // cbClientAddress + address
    info.u16le(2); info.u16le(0); // cbClientDir + dir
    info.zeros(172); // clientTimeZone
    info.u32le(0);   // clientSessionId
    info.u32le(0);   // performanceFlags

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
    d.u16le(0);      // desktopResizeFlag
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
function capInput() {
    const d = new ByteWriter();
    d.u16le(0x0001 | 0x0004 | 0x0010 | 0x0020); // SCANCODES|MOUSEX|UNICODE|FASTPATH_INPUT2
    d.u16le(0);          // padding
    d.u32le(0x00000409); // keyboardLayout US
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
    return capSet(0x001A, new ByteWriter().u32le(0).toArray());
}

function confirmActivePdu(shareID, userId, width, height) {
    const caps = [
        capGeneral(), capBitmap(width, height), capOrder(), capBitmapCacheRev1(),
        capPointer(), capInput(), capBrush(), capGlyphCache(), capOffscreen(),
        capVirtualChannel(), capSound(), capMultifragmentUpdate(),
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

    this.userId = 0;
    this.shareID = 0;
    this.mcsChannelId = 1003;
    this.skipChannelJoin = false;
    this.joinQueue = [];
    this.staticChannelIds = {};
    this.drdynvcChannelId = 0;
    this._lastChannelId = 0;

    // Dynamic virtual channels (MS-RDPEDYC). Maps DVC channelId -> {name}. We only act on the
    // Display Control channel (MS-RDPEDISP) for live resolution/scale changes.
    this.dvcByName = {};        // name -> channelId
    this.dvcById = {};          // channelId -> name
    this.displayControlChannelId = null;
    this._dvcFragBuf = null;    // reassembly for fragmented DVC data
    this._dvcFragName = null;

    // Desktop scale factor (DPI). The RDP handshake (CS_CORE) carries no scale field, so the session
    // ALWAYS starts at 100% — these track the scale currently in effect on the server, not what the
    // client wants. Initializing them to anything but 100 would make the first MONITOR_LAYOUT (which
    // applies the real DPI scale) look like a no-op and never get sent. The desired DPI scale is
    // passed per-call to sendMonitorLayout instead. ([MS-RDPEDISP] percent values.)
    this.desktopScaleFactor = 100; // 100..500, current server scale
    this.deviceScaleFactor = 100;  // 100, 140 or 180, current server scale

    this.state = null;
    this.rxBuf = new Uint8Array(0); // inbound reassembly buffer

    // finalization progress flags
    this.fin = { sync: false, coop: false, granted: false, fontmap: false };
}

RdpProtocol.prototype._log = function (m) { if (this.cb.onLog) this.cb.onLog(m); };
RdpProtocol.prototype._err = function (m) { if (this.cb.onError) this.cb.onError(m); };

// Kick off the handshake: send MCS connect-initial. Called once the relay is "ready".
RdpProtocol.prototype.start = function () {
    this._log("MCS: Connect Initial");
    const userData = clientUserData(this.selectedProtocol, this.width, this.height);
    const connectInitial = mcsConnectInitialSerialize(userData);
    this.t.send(tpktX224Wrap(connectInitial));
    this.state = ST.BASIC_SETTINGS;
};

// Append inbound bytes and process every complete PDU available.
RdpProtocol.prototype.feed = function (chunk) {
    // grow rxBuf
    const merged = new Uint8Array(this.rxBuf.length + chunk.length);
    merged.set(this.rxBuf, 0);
    merged.set(chunk, this.rxBuf.length);
    this.rxBuf = merged;

    // Process as many framed units as are complete.
    for (;;) {
        const consumed = this._processOne();
        if (consumed <= 0) break;
        this.rxBuf = this.rxBuf.subarray(consumed);
    }
};

// Determine the length of the next inbound unit (TPKT slow-path or fastpath), returning 0 if more
// bytes are needed. Then dispatch it. Returns the number of bytes consumed (0 if incomplete).
RdpProtocol.prototype._processOne = function () {
    const b = this.rxBuf;
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
            return this._onLicensing(this._mcsSendDataIndication(r));
        case ST.CAPABILITIES:
            return this._onDemandActive(this._mcsSendDataIndication(r));
        case ST.FINALIZATION:
            return this._onFinalization(this._mcsSendDataIndication(r));
        case ST.ACTIVE:
            // Slow-path data during the active phase (e.g. error info / deactivate-all). Parse the
            // share control header to detect deactivate-all & error info; otherwise ignore.
            return this._onActiveSlowPath(this._mcsSendDataIndication(r));
        default:
            return;
    }
};

// Reads the MCS Send-Data-Indication header. Records the source channel id on `this._lastChannelId`
// and returns a reader positioned at the embedded RDP/virtual-channel PDU.
RdpProtocol.prototype._mcsSendDataIndication = function (r) {
    const choice = Per.readChoice(r);
    const application = choice >> 2;
    if (application === MCS_DISCONNECT_ULTIMATUM) throw new Error("server disconnected (MCS ultimatum)");
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
    // Map the positional SC_NET channel ids back to STATIC_CHANNELS names; remember drdynvc's id.
    this.staticChannelIds = {};
    for (let i = 0; i < STATIC_CHANNELS.length && i < parsed.channelIds.length; i++) {
        this.staticChannelIds[STATIC_CHANNELS[i]] = parsed.channelIds[i];
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
        this._sendClientInfo();
        return;
    }
    // Join the user channel, the global I/O channel, then each static virtual channel (drdynvc).
    this.joinQueue = [this.userId, this.mcsChannelId];
    for (const name of STATIC_CHANNELS) {
        const id = this.staticChannelIds[name];
        if (id) this.joinQueue.push(id);
    }
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
    this._sendClientInfo();
};

RdpProtocol.prototype._sendClientInfo = function () {
    this._log("RDP: Client Info");
    const info = clientInfoPdu(this.opts.domain, this.opts.username, this.opts.password);
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
    // (e.g. DEMANDACTIVE 0x11, CONFIRMACTIVE 0x13, DATAPDU 0x17). Compare on the low nibble.
    const startO = r.o;
    let totalLength = r.u16le();
    let pduType = r.u16le() & 0xf;
    if (pduType !== (PDUTYPE_DEMANDACTIVE & 0xf) && pduType !== (PDUTYPE_DATAPDU & 0xf)) {
        // Probably a 4-byte security header preceded the share control header; rewind + skip it.
        r.o = startO + 4;
        totalLength = r.u16le();
        pduType = r.u16le() & 0xf;
    }
    if (pduType !== (PDUTYPE_DEMANDACTIVE & 0xf)) {
        // Not the demand-active yet (could be an early data PDU); ignore and keep waiting.
        return;
    }
    r.u16le(); // pduSource
    this.shareID = r.u32le();
    this._log("RDP: Demand Active (shareID " + this.shareID + ")");

    // Reply with Confirm Active, then run connection finalization.
    const confirm = confirmActivePdu(this.shareID, this.userId, this.width, this.height);
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

// Active-phase slow-path MCS data. Routes by source channel: the drdynvc virtual channel feeds the
// DVC manager (MS-RDPEDYC); the global I/O channel carries share-control PDUs (error info, and the
// DEACTIVATE_ALL that begins a Deactivation-Reactivation Sequence after a resolution/scale change).
RdpProtocol.prototype._onActiveSlowPath = function (r) {
    try {
        if (this.drdynvcChannelId && this._lastChannelId === this.drdynvcChannelId) {
            return this._onDrdynvcData(r);
        }

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

        this._log("RDP: active slow-path PDU type 0x" + pduType.toString(16) + " on channel " + this._lastChannelId);

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
        return;
    }
    // DIAGNOSTIC: log the first few fastpath updates after a monitor layout, so we can tell whether
    // the server keeps streaming graphics post-resize (vs. going silent). Remove once resize is fixed.
    if (this._fpLogBudget > 0) {
        this._fpLogBudget--;
        this._log("RDP: fastpath update (" + payload.length + " bytes) state=" + this.state);
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

// ================================================================================================
// Static virtual channel send (CHANNEL_PDU_HEADER) + drdynvc dynamic virtual channels (MS-RDPEDYC)
// ================================================================================================
const CHANNEL_FLAG_FIRST = 0x00000001;
const CHANNEL_FLAG_LAST = 0x00000002;

// drdynvc command codes ([MS-RDPEDYC] 2.2).
const DVC_CMD_CREATE = 0x01;
const DVC_CMD_DATA_FIRST = 0x02;
const DVC_CMD_DATA = 0x03;
const DVC_CMD_CLOSE = 0x04;
const DVC_CMD_CAPABILITIES = 0x05;

const DISPLAY_CONTROL_CHANNEL_NAME = "Microsoft::Windows::RDS::DisplayControl";

// Wrap a virtual-channel payload in a CHANNEL_PDU_HEADER and send it on `channelId` via MCS
// send-data-request. Payloads here are small (caps/create responses, monitor layout) and fit a
// single chunk, so FIRST|LAST is always set.
RdpProtocol.prototype._sendOnChannel = function (channelId, payload) {
    const w = new ByteWriter();
    w.u32le(payload.length);                    // CHANNEL_PDU_HEADER.length (uncompressed total)
    w.u32le(CHANNEL_FLAG_FIRST | CHANNEL_FLAG_LAST); // flags
    w.bytes(payload);
    this.t.send(tpktX224Wrap(mcsSendDataSerialize(this.userId, channelId, w.toArray())));
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

    if (this._dvcLogBudget > 0) { this._dvcLogBudget--; this._log("drdynvc: rx cmd=" + cmd + " sp=" + sp + " cbId=" + cbId); }

    switch (cmd) {
        case DVC_CMD_CAPABILITIES: return this._dvcOnCapabilities(r);
        case DVC_CMD_CREATE: return this._dvcOnCreate(r, cbId);
        case DVC_CMD_DATA: return this._dvcOnData(r, cbId, false, sp);
        case DVC_CMD_DATA_FIRST: return this._dvcOnData(r, cbId, true, sp);
        case DVC_CMD_CLOSE: return this._dvcOnClose(r, cbId);
        default:
            this._log("drdynvc: ignoring cmd " + cmd);
    }
};

// Capabilities Request -> Capabilities Response (advertise version 1, the simplest that works).
RdpProtocol.prototype._dvcOnCapabilities = function (r) {
    // Request body: pad(1) version(2) [+ per-priority charges]. We only need the version echoed.
    /* pad */ r.u8();
    let version = 1;
    if (r.remaining() >= 2) version = r.u16le();
    if (version < 1) version = 1;
    this._log("drdynvc: capabilities v" + version);

    const body = new ByteWriter();
    body.u8(((DVC_CMD_CAPABILITIES & 0xf) << 4)); // cmd, sp=0, cbId=0
    body.u8(0x00);          // pad
    body.u16le(version);    // Version
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
    // Only accept the Display Control channel — it's the one DVC we actually implement (live
    // resolution/scale via MS-RDPEDISP). EVERY OTHER dynamic channel (ECHO, CoreInput, Video,
    // Geometry, Input, TextInput, …) is REJECTED with a failure creationStatus, so the server keeps
    // that functionality on the legacy fastpath path (which we render/handle) instead of routing it
    // through a DVC and then stalling while it waits for responses we can't produce (e.g. ECHO
    // keepalives). Accepting channels we don't service is what made the server go silent.
    const accept = (name === DISPLAY_CONTROL_CHANNEL_NAME);

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

    // creationStatus: 0 = success; 0xC0000001 (STATUS_UNSUCCESSFUL) = "won't use this channel".
    const status = new ByteWriter().u32le(accept ? 0x00000000 : 0xC0000001).toArray();
    const pdu = dvcBuildPdu(DVC_CMD_CREATE, channelId, status, 0, cbId);
    this._sendOnChannel(this.drdynvcChannelId, pdu);

    if (accept) {
        this.dvcByName[name] = channelId;
        this.dvcById[channelId] = name;
        this.displayControlChannelId = channelId;
        this.displayControlCbId = cbId; // reuse the same channel-id width when we send DATA back
        this._log("drdynvc: accepted '" + name + "' id=" + channelId);
        if (this.cb.onDisplayControlReady) this.cb.onDisplayControlReady();
    } else {
        this._log("drdynvc: rejected '" + name + "' id=" + channelId);
    }
};

RdpProtocol.prototype._dvcOnClose = function (r, cbId) {
    const channelId = dvcReadChannelId(r, cbId);
    const name = this.dvcById[channelId];
    if (name) { delete this.dvcByName[name]; delete this.dvcById[channelId]; }
    if (channelId === this.displayControlChannelId) this.displayControlChannelId = null;
};

// DVC data (possibly fragmented via DATA_FIRST + DATA). We only interpret data on the Display
// Control channel (server CAPS); everything else is ignored.
RdpProtocol.prototype._dvcOnData = function (r, cbId, isFirst, sp) {
    const channelId = dvcReadChannelId(r, cbId);
    if (isFirst) {
        // DATA_FIRST carries a total length field whose width is encoded in sp (0->1B,1->2B,2->4B).
        if (sp === 0) r.u8(); else if (sp === 1) r.u16le(); else r.u32le();
    }
    const data = r.bytes(r.remaining());
    if (channelId !== this.displayControlChannelId) return; // only Display Control is interpreted
    this._onDisplayControlData(data);
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
    this._log("RDPEDISP: inbound data type=0x" + type.toString(16) + " len=" + data.length);
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
    if (width === this.width && height === this.height &&
        desktopScale === this.desktopScaleFactor && deviceScale === this.deviceScaleFactor) {
        return false;
    }

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
    this._log("RDPEDISP: monitor layout " + width + "x" + height + " @ " + desktopScale + "%"
        + " (dvcChan=" + this.displayControlChannelId + " cbId=" + this.displayControlCbId
        + " drdynvc=" + this.drdynvcChannelId + ")");

    // NOTE: the real Windows client sends ONLY the MONITOR_LAYOUT for a resize — no refresh rect. We
    // previously sent a TS_REFRESH_RECT here to force a repaint on the in-place host, but that's our only
    // non-standard PDU and a prime suspect for the "second resize ignored" desync. Removed; rely on the
    // server's own repaint after the layout (it sent a full repaint for the first resize without it).
    this._fpLogBudget = 5; // DIAGNOSTIC: log the next few fastpath updates after this layout
    this._dvcLogBudget = 30; // DIAGNOSTIC: log inbound drdynvc commands after this layout
    return true;
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
