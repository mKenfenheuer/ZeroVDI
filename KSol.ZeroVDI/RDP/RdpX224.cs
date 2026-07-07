// RdpX224 — X.224 + MCS layer decode/encode (round-trip), plus the GCC/BER/PER connection sequence.
//
// After the 4-byte TPKT header, a slow-path PDU is an X.224 TPDU:
//   - Connection Request (CR, 0xE0) / Connection Confirm (CC, 0xD0): the X.224 negotiation, normally
//     consumed by the proxy itself before TLS — but if seen, decoded here.
//   - Data TPDU (DT, 0xF0, EOT 0x80): carries an MCS PDU.
//
// MCS PDUs (T.125): the connect-initial/connect-response are BER-encoded (application tags 101/102) and
// wrap a GCC conference-create (PER) carrying the CS_*/SC_* settings blocks. The domain PDUs
// (erect-domain, attach-user, channel-join, send-data) are PER-encoded; the choice byte's high 6 bits are
// the application code.
//
// Round-trip strategy: every header byte we read is echoed via Node.Raw, so re-encoding reproduces the
// exact bytes. We decode fields for logging and hand the embedded RDP/channel payload to the next layer
// as a child node (which echoes its own bytes), so the whole tree re-serializes byte-identically.

using System.Text.Json.Nodes;

namespace KSol.ZeroVDI.RDP;

internal static class RdpX224
{
    private const int MCS_ERECT_DOMAIN = 1;
    private const int MCS_ATTACH_USER_REQUEST = 10;
    private const int MCS_ATTACH_USER_CONFIRM = 11;
    private const int MCS_CHANNEL_JOIN_REQUEST = 14;
    private const int MCS_CHANNEL_JOIN_CONFIRM = 15;
    private const int MCS_SEND_DATA_REQUEST = 25;
    private const int MCS_SEND_DATA_INDICATION = 26;
    private const int MCS_DISCONNECT_ULTIMATUM = 8;

    public static Node Decode(RdpSession s, RdpDir dir, Node tpkt, ReadOnlySpan<byte> x224)
    {
        var c = new Cur(x224);
        byte li = c.U8();
        byte code = c.U8();
        byte codeHi = (byte)(code & 0xf0);

        if (codeHi == 0xE0 || codeHi == 0xD0)
        {
            // CR/CC: LI(1) code(1) dst(2) src(2) class(1) [+ RDP_NEG_*]
            var n = new Node(codeHi == 0xE0 ? "X224.ConnectionRequest" : "X224.ConnectionConfirm");
            n.Raw(x224); // whole X.224 echoed; we just decode fields for the log
            n.Field("li", li);
            // Optional RDP negotiation structure begins at offset 7 (after the 7-byte fixed header).
            if (x224.Length >= 15 && x224[7] is 0x01 or 0x02 or 0x03)
            {
                byte negType = x224[7];
                uint val = (uint)(x224[11] | (x224[12] << 8) | (x224[13] << 16) | (x224[14] << 24));
                n.Field("negType", negType == 1 ? "REQ" : negType == 2 ? "RSP" : "FAILURE");
                n.Field(negType == 1 ? "requestedProtocols" : "selectedProtocol", "0x" + val.ToString("X"));
            }
            tpkt.Child(n);
            return tpkt;
        }

        if (codeHi == 0xF0)
        {
            // Data TPDU: LI(1)=2 code(1)=0xF0 EOT(1). Echo the 3-byte X.224 data header, then MCS.
            var dt = new Node("X224.Data");
            dt.Raw(x224.Slice(0, 3));
            tpkt.Child(dt);
            var mcs = x224.Slice(3);
            DecodeMcs(s, dir, dt, mcs);
            return tpkt;
        }

        // Unknown X.224 code — echo verbatim.
        var raw = new Node("X224.raw");
        raw.Field("code", "0x" + code.ToString("X2")).FieldHex("bytes", x224);
        raw.Raw(x224);
        tpkt.Child(raw);
        return tpkt;
    }

    private static void DecodeMcs(RdpSession s, RdpDir dir, Node parent, ReadOnlySpan<byte> mcs)
    {
        if (mcs.Length == 0) return;
        byte b0 = mcs[0];

        // BER application tag 0x7f => connect-initial/response (or other long app tags).
        if (b0 == 0x7f)
        {
            byte app = mcs.Length > 1 ? mcs[1] : (byte)0;
            if (app == 101) { DecodeConnectInitial(s, dir, parent, mcs); return; }
            if (app == 102) { DecodeConnectResponse(s, dir, parent, mcs); return; }
        }

        int choice = b0;
        int app2 = choice >> 2;
        switch (app2)
        {
            case MCS_ERECT_DOMAIN: { var n = new Node("MCS.ErectDomain"); n.Raw(mcs); parent.Child(n); return; }
            case MCS_ATTACH_USER_REQUEST: { var n = new Node("MCS.AttachUserRequest"); n.Raw(mcs); parent.Child(n); return; }
            case MCS_ATTACH_USER_CONFIRM:
            {
                var n = new Node("MCS.AttachUserConfirm"); n.Raw(mcs);
                var c = new Cur(mcs); c.U8(); byte result = c.U8();
                int initiator = c.Remaining >= 2 ? ((c.U16be()) + 1001) : 0;
                n.Field("result", result).Field("userId", initiator);
                if (result == 0) s.UserId = initiator;
                parent.Child(n); return;
            }
            case MCS_CHANNEL_JOIN_REQUEST:
            {
                var n = new Node("MCS.ChannelJoinRequest"); n.Raw(mcs);
                var c = new Cur(mcs); c.U8(); int init = c.U16be() + 1001; int ch = c.U16be();
                n.Field("initiator", init).Field("channelId", ch).Field("channel", s.ChannelLabel(ch));
                parent.Child(n); return;
            }
            case MCS_CHANNEL_JOIN_CONFIRM:
            {
                var n = new Node("MCS.ChannelJoinConfirm"); n.Raw(mcs);
                var c = new Cur(mcs); c.U8(); byte result = c.U8();
                int init = c.U16be() + 1001; int requested = c.U16be();
                n.Field("result", result).Field("channelId", requested).Field("channel", s.ChannelLabel(requested));
                parent.Child(n); return;
            }
            case MCS_SEND_DATA_REQUEST:
            case MCS_SEND_DATA_INDICATION:
            {
                DecodeSendData(s, dir, parent, mcs, app2 == MCS_SEND_DATA_REQUEST);
                return;
            }
            case MCS_DISCONNECT_ULTIMATUM:
            {
                var n = new Node("MCS.DisconnectUltimatum"); n.Raw(mcs); parent.Child(n); return;
            }
            default:
            {
                var n = new Node("MCS.raw"); n.Field("choice", "0x" + b0.ToString("X2")).Field("app", app2)
                    .FieldHex("bytes", mcs); n.Raw(mcs); parent.Child(n); return;
            }
        }
    }

    // SendDataRequest/Indication: choice(1) initiator(2,+1001) channelId(2) dataPriority(1) length(PER) payload.
    private static void DecodeSendData(RdpSession s, RdpDir dir, Node parent, ReadOnlySpan<byte> mcs, bool isRequest)
    {
        var c = new Cur(mcs);
        int hdrStart = c.O;
        c.U8();                       // choice
        int initiator = c.U16be() + 1001;
        int channelId = c.U16be();
        c.U8();                       // dataPriority/segmentation
        int lenStart = c.O;
        int payloadLen = PerReadLength(ref c);
        int hdrLen = c.O;             // bytes before payload

        var n = new Node(isRequest ? "MCS.SendDataRequest" : "MCS.SendDataIndication");
        n.Channel = s.ChannelLabel(channelId);
        n.Field("initiator", initiator).Field("channelId", channelId)
         .Field("channel", n.Channel).Field("payloadLen", payloadLen);
        // Echo the MCS send-data header bytes verbatim.
        n.Raw(mcs.Slice(hdrStart, hdrLen - hdrStart));
        parent.Child(n);

        var payload = mcs.Slice(hdrLen);
        // Route by channel: drdynvc, a known static channel, or the global I/O channel (share PDUs).
        if (channelId == s.DrdynvcId && s.DrdynvcId != 0)
            RdpChannels.DecodeDrdynvc(s, dir, n, payload);
        else if (s.ChannelById.TryGetValue(channelId, out var name) && channelId != s.IoChannelId)
            RdpChannels.DecodeStaticChannel(s, dir, n, name, payload);
        else
            RdpShare.Decode(s, dir, n, payload);
    }

    // ==============================================================================================
    // Connect-Initial / Connect-Response (BER + GCC/PER + CS_*/SC_* settings)
    // ==============================================================================================
    private static void DecodeConnectInitial(RdpSession s, RdpDir dir, Node parent, ReadOnlySpan<byte> mcs)
    {
        var n = new Node("MCS.ConnectInitial");
        n.Raw(mcs); // whole connect-initial echoed verbatim (round-trips trivially)
        try
        {
            var c = new Cur(mcs);
            // BER: 7f 65 <len> ; then octetstrings/boolean/3x sequence/octetstring(GCC)
            c.U8(); c.U8(); BerReadLength(ref c);          // app tag 101 + length
            BerSkipOctetString(ref c);                     // calledDomainSelector
            BerSkipOctetString(ref c);                     // callingDomainSelector
            BerSkipBoolean(ref c);                         // upwardFlag
            BerSkipSequence(ref c); BerSkipSequence(ref c); BerSkipSequence(ref c); // 3x domainParameters
            // userData octet string -> GCC conference create request
            c.U8(); int udLen = BerReadLength(ref c);      // 0x04 + length
            var gcc = c.Buf.Slice(c.O, Math.Min(udLen, c.Remaining));
            var settings = GccSkipCreateRequest(gcc, out int udStart);
            DecodeSettingsBlocks(s, dir, n, settings, isServer: false);
        }
        catch (Exception ex) { n.Field("parseError", ex.Message); }
        parent.Child(n);
    }

    private static void DecodeConnectResponse(RdpSession s, RdpDir dir, Node parent, ReadOnlySpan<byte> mcs)
    {
        var n = new Node("MCS.ConnectResponse");
        n.Raw(mcs);
        try
        {
            var c = new Cur(mcs);
            c.U8(); c.U8(); BerReadLength(ref c);          // app tag 102 + length
            // result ENUMERATED
            c.U8(); int rl = BerReadLength(ref c); c.O += rl; // enumerated value
            // calledConnectId INTEGER
            c.U8(); int il = BerReadLength(ref c); c.O += il;
            // domainParameters SEQUENCE
            BerSkipSequence(ref c);
            // userData OCTET STRING -> GCC conference create response
            c.U8(); int udLen = BerReadLength(ref c);
            var gcc = c.Buf.Slice(c.O, Math.Min(udLen, c.Remaining));
            var settings = GccSkipCreateResponse(gcc);
            DecodeSettingsBlocks(s, dir, n, settings, isServer: true);
        }
        catch (Exception ex) { n.Field("parseError", ex.Message); }
        parent.Child(n);
    }

    // Walk the concatenated CS_*/SC_* user-data blocks: type(2) len(2) body(len-4).
    private static void DecodeSettingsBlocks(RdpSession s, RdpDir dir, Node parent, ReadOnlySpan<byte> data, bool isServer)
    {
        var c = new Cur(data);
        while (c.Remaining >= 4)
        {
            int type = c.U16le();
            int len = c.U16le();
            int bodyLen = len - 4;
            if (bodyLen < 0 || bodyLen > c.Remaining) break;
            var body = c.Take(bodyLen);
            var blk = new Node(SettingsName(type));
            blk.Field("type", "0x" + type.ToString("X4")).Field("length", len);
            DecodeSettingsBody(s, type, body, blk);
            parent.Child(blk); // logging only — bytes already echoed by parent's Raw
        }
    }

    private static string SettingsName(int t) => t switch
    {
        0xC001 => "CS_CORE", 0xC002 => "CS_SECURITY", 0xC003 => "CS_NET", 0xC004 => "CS_CLUSTER",
        0xC006 => "CS_MCS_MSGCHANNEL", 0xC00A => "CS_MULTITRANSPORT",
        0x0C01 => "SC_CORE", 0x0C02 => "SC_SECURITY", 0x0C03 => "SC_NET",
        0x0C04 => "SC_MSGCHANNEL", 0x0C08 => "SC_MULTITRANSPORT",
        _ => "GCC_BLOCK_0x" + t.ToString("X4"),
    };

    private static void DecodeSettingsBody(RdpSession s, int type, ReadOnlySpan<byte> body, Node n)
    {
        var c = new Cur(body);
        switch (type)
        {
            case 0xC001: // CS_CORE
                if (c.Remaining >= 12)
                {
                    n.Field("version", "0x" + c.U32le().ToString("X8"));
                    n.Field("desktopWidth", c.U16le()).Field("desktopHeight", c.U16le());
                    n.Field("colorDepth", "0x" + c.U16le().ToString("X4"));
                }
                // serverSelectedProtocol sits deep in the block; surface it if present.
                if (body.Length >= 4)
                {
                    uint ssp = (uint)(body[body.Length - 4] | (body[body.Length - 3] << 8) | (body[body.Length - 2] << 16) | (body[body.Length - 1] << 24));
                    if (ssp <= 0x0f) n.Field("serverSelectedProtocol", "0x" + ssp.ToString("X"));
                }
                break;
            case 0xC003: // CS_NET — channelCount then ChannelDef[count] (name8 + options4)
            {
                int count = (int)c.U32le();
                n.Field("channelCount", count);
                s.StaticChannelNames.Clear();
                var arr = new JsonArray();
                for (int i = 0; i < count && c.Remaining >= 12; i++)
                {
                    var nameBytes = c.Take(8);
                    int end = 0; while (end < 8 && nameBytes[end] != 0) end++;
                    string name = System.Text.Encoding.ASCII.GetString(nameBytes.Slice(0, end));
                    uint opt = c.U32le();
                    s.StaticChannelNames.Add(name);
                    arr.Add((JsonNode?)$"{name}=0x{opt:X8}");
                }
                n.Field("channels", arr);
                break;
            }
            case 0x0C01: // SC_CORE
                if (c.Remaining >= 4) n.Field("version", "0x" + c.U32le().ToString("X8"));
                if (c.Remaining >= 4) n.Field("clientRequestedProtocols", "0x" + c.U32le().ToString("X"));
                if (c.Remaining >= 4) { uint ec = c.U32le(); n.Field("earlyCapabilityFlags", "0x" + ec.ToString("X")); s.SkipChannelJoin = (ec & 0x8) == 0x8; }
                break;
            case 0x0C03: // SC_NET — global channel id + count + ids[count]
            {
                int io = c.U16le(); int count = c.U16le();
                n.Field("ioChannelId", io).Field("channelCount", count);
                s.IoChannelId = io;
                s.StaticChannelIds.Clear();
                var arr = new JsonArray();
                for (int i = 0; i < count && c.Remaining >= 2; i++) { int id = c.U16le(); s.StaticChannelIds.Add(id); arr.Add((JsonNode?)id); }
                // pad to even count is possible; we don't need it for the map.
                n.Field("channelIds", arr);
                s.RebuildChannelMap();
                n.Field("channelMap", string.Join(",", System.Linq.Enumerable.Select(s.ChannelById, kv => $"{kv.Value}={kv.Key}")));
                break;
            }
            default:
                n.FieldHex("body", body);
                break;
        }
    }

    // ---- minimal BER readers (skip helpers; values not needed for round-trip since bytes are echoed) ----
    private static int BerReadLength(ref Cur c)
    {
        int size = c.U8();
        if ((size & 0x80) != 0)
        {
            int nn = size & 0x7f;
            if (nn == 1) return c.U8();
            if (nn == 2) return c.U16be();
            throw new Exception("BER length too long");
        }
        return size;
    }
    private static void BerSkipOctetString(ref Cur c) { c.U8(); int l = BerReadLength(ref c); c.O += l; }
    private static void BerSkipBoolean(ref Cur c) { c.U8(); int l = BerReadLength(ref c); c.O += l; }
    private static void BerSkipSequence(ref Cur c) { c.U8(); int l = BerReadLength(ref c); c.O += l; }

    // ---- GCC PER conference-create wrappers: skip to the user-data settings blob ----
    private static ReadOnlySpan<byte> GccSkipCreateRequest(ReadOnlySpan<byte> gcc, out int udStart)
    {
        var c = new Cur(gcc);
        c.U8();                              // choice
        // object identifier (PER length 5) for t124
        int oidLen = PerReadLength(ref c); c.O += oidLen;
        PerReadLength(ref c);                // ConferenceCreateRequest length
        c.U8();                              // choice
        c.U8();                              // selection (0x08)
        // numeric string "1": PER length(1) then 1 byte
        int nsLen = PerReadLength(ref c); c.O += 1; // (length-1 form; the 1-byte digit)
        c.O += 1;                            // padding
        c.U8();                              // numberOfSet
        c.U8();                              // choice (0xc0)
        // octetstream H221 key "Duca": PER length then 4 bytes
        int keyLen = PerReadLength(ref c); c.O += 4;
        // octetstream userData: PER length then the blob
        int udLen = PerReadLength(ref c);
        udStart = c.O;
        return c.Buf.Slice(c.O, Math.Min(udLen, c.Remaining));
    }

    private static ReadOnlySpan<byte> GccSkipCreateResponse(ReadOnlySpan<byte> gcc)
    {
        var c = new Cur(gcc);
        c.U8();                              // choice
        int oidLen = PerReadLength(ref c); c.O += oidLen;     // t124 OID
        PerReadLength(ref c);                // length
        c.U8();                              // choice
        c.U16be();                           // nodeID (integer16, +1001)
        int tagLen = PerReadLength(ref c); c.O += tagLen;     // tag (integer)
        c.U8();                              // result enumerated
        c.U8();                              // numberOfSet
        c.U8();                              // choice
        int keyLen = PerReadLength(ref c); c.O += 4;          // H221 key "McDn"
        int udLen = PerReadLength(ref c);    // userData length
        return c.Buf.Slice(c.O, Math.Min(udLen, c.Remaining));
    }

    // PER length (used by GCC + send-data). High bit set => 2-byte (14-bit) length.
    internal static int PerReadLength(ref Cur c)
    {
        int octet = c.U8();
        if ((octet & 0x80) != 0x80) return octet;
        octet &= 0x7f;
        int size = octet << 8;
        size += c.U8();
        return size;
    }
}
