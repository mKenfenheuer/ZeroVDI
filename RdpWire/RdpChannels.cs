// RdpChannels — static virtual channels and the drdynvc dynamic-channel demux + RDPEGFX.
//
// Static channels (rdpsnd / cliprdr / rdpdr / drdynvc) each carry a CHANNEL_PDU_HEADER {length(4),
// flags(4)} then a chunk; FIRST..LAST flags delimit messages split across chunks. The complete payload
// is then dispatched by channel:
//   - rdpsnd  (MS-RDPEA):    SNDPROLOG msgType + body
//   - cliprdr (MS-RDPECLIP): CLIPRDR_HEADER msgType/msgFlags + body
//   - rdpdr   (MS-RDPEFS):   component + packetId
//   - drdynvc (MS-RDPEDYC):  the dynamic-channel command stream (below)
//
// drdynvc commands ([MS-RDPEDYC] 2.2): a 1-byte header (cmd<<4 | sp<<2 | cbId), then a channelId of
// cbId-encoded width, then a body. Commands: CAPABILITIES(5), CREATE(1), DATA_FIRST(2), DATA(3),
// CLOSE(4), and the v3 compressed variants DATA_FIRST_COMPRESSED(6) / DATA_COMPRESSED(7) whose body is
// ZGFX(LITE)-compressed. CREATE carries a channel name; we record id<->name so DATA can be labeled and
// routed to its extension decoder (Display Control, audio in/out, camera, RDPEGFX graphics).
//
// RDPEGFX graphics ([MS-RDPEGFX]): each DVC payload on the Graphics channel is a ZGFX message; once
// inflated it is one or more RDPGFX PDUs (header {cmdId(2), flags(2), pduLength(4)} + body). We decode
// every command; codec bitstreams inside WIRE_TO_SURFACE stay opaque.
//
// Round-trip note: drdynvc/GFX framing bytes are echoed via Node.Raw at each layer, EXCEPT where a
// payload had to be decompressed (ZGFX) to be parsed — there the decompressed view cannot reproduce the
// original compressed bytes, so the enclosing DVC-DATA node echoes the ORIGINAL compressed bytes (round-
// trips) and the inflated GFX sub-tree is logged-only (its bytes are not re-emitted into the wire copy).

using System.Text.Json.Nodes;

namespace KSol.RDPGateway.RDP;

internal static class RdpChannels
{
    private const int DVC_CMD_CREATE = 0x01;
    private const int DVC_CMD_DATA_FIRST = 0x02;
    private const int DVC_CMD_DATA = 0x03;
    private const int DVC_CMD_CLOSE = 0x04;
    private const int DVC_CMD_CAPABILITIES = 0x05;
    private const int DVC_CMD_DATA_FIRST_COMPRESSED = 0x06;
    private const int DVC_CMD_DATA_COMPRESSED = 0x07;
    private const int DVC_CMD_SOFT_SYNC_REQUEST = 0x08;
    private const int DVC_CMD_SOFT_SYNC_RESPONSE = 0x09;

    // ---- static channel dispatch (payload = full reassembled CHANNEL_PDU_HEADER-stripped message) ----
    public static void DecodeStaticChannel(RdpSession s, RdpDir dir, Node parent, string name, ReadOnlySpan<byte> chanData)
    {
        var c = new Cur(chanData);
        var payload = s.ReassembleSvc(name, ref c, out uint chFlags);
        var hdr = new Node("CHANNEL_PDU_HEADER");
        hdr.Field("channel", name).Field("flags", "0x" + chFlags.ToString("X8"));
        hdr.Raw(chanData); // whole channel chunk echoed (incl. header) — round-trips
        parent.Child(hdr);
        if (payload == null) { hdr.Field("reassembly", "pending"); return; }

        switch (name)
        {
            case "rdpsnd": DecodeRdpSnd(hdr, payload); break;
            case "cliprdr": DecodeClipRdr(hdr, payload); break;
            case "rdpdr": DecodeRdpDr(hdr, payload); break;
            default: hdr.FieldHex("payload", payload); break;
        }
    }

    private static void DecodeRdpSnd(Node parent, byte[] payload)
    {
        var c = new Cur(payload);
        var n = new Node("rdpsnd");
        if (c.Remaining >= 4)
        {
            byte msgType = c.U8(); c.U8(); int bodySize = c.U16le();
            n.Field("msgType", "0x" + msgType.ToString("X2") + " " + msgType switch
            {
                0x01 => "WaveInfo", 0x02 => "Wave", 0x03 => "Close", 0x04 => "WaveConfirm",
                0x05 => "Training", 0x06 => "Formats", 0x07 => "CryptKey", 0x08 => "WaveEncrypt",
                0x09 => "UDPWave", 0x0A => "UDPWaveLast", 0x0B => "Quality", 0x0C => "Volume",
                0x0D => "Pitch", 0x38 => "WaveV2", _ => "?",
            }).Field("bodySize", bodySize);
        }
        n.Field("len", payload.Length);
        parent.Child(n); // logging only; bytes already echoed by CHANNEL_PDU_HEADER.Raw
    }

    private static void DecodeClipRdr(Node parent, byte[] payload)
    {
        var c = new Cur(payload);
        var n = new Node("cliprdr");
        if (c.Remaining >= 8)
        {
            int msgType = c.U16le(); int msgFlags = c.U16le(); long dataLen = c.U32le();
            n.Field("msgType", "0x" + msgType.ToString("X4") + " " + msgType switch
            {
                0x0001 => "MONITOR_READY", 0x0002 => "FORMAT_LIST", 0x0003 => "FORMAT_LIST_RESPONSE",
                0x0004 => "FORMAT_DATA_REQUEST", 0x0005 => "FORMAT_DATA_RESPONSE", 0x0006 => "TEMP_DIRECTORY",
                0x0007 => "CAPABILITIES", 0x0008 => "FILECONTENTS_REQUEST", 0x0009 => "FILECONTENTS_RESPONSE",
                0x000A => "LOCK_CLIPDATA", 0x000B => "UNLOCK_CLIPDATA", _ => "?",
            }).Field("msgFlags", "0x" + msgFlags.ToString("X4")).Field("dataLen", dataLen);
        }
        parent.Child(n);
    }

    private static void DecodeRdpDr(Node parent, byte[] payload)
    {
        var c = new Cur(payload);
        var n = new Node("rdpdr");
        if (c.Remaining >= 4)
        {
            int component = c.U16le(); int packetId = c.U16le();
            n.Field("component", "0x" + component.ToString("X4") + (component == 0x4472 ? " RDPDR_CTYP_CORE" : component == 0x5052 ? " RDPDR_CTYP_PRN" : ""));
            n.Field("packetId", "0x" + packetId.ToString("X4") + " " + packetId switch
            {
                0x496E => "SERVER_ANNOUNCE", 0x4343 => "CLIENTID_CONFIRM", 0x434E => "CLIENT_NAME",
                0x4441 => "DEVICELIST_ANNOUNCE", 0x5350 => "SERVER_CAPABILITY", 0x4350 => "CLIENT_CAPABILITY",
                0x554C => "USER_LOGGEDON", 0x646D => "DEVICE_REPLY", 0x4952 => "DEVICE_IOREQUEST",
                0x4943 => "DEVICE_IOCOMPLETION", _ => "?",
            });
        }
        parent.Child(n);
    }

    // ==============================================================================================
    // drdynvc (MS-RDPEDYC)
    // ==============================================================================================
    public static void DecodeDrdynvc(RdpSession s, RdpDir dir, Node parent, ReadOnlySpan<byte> chanData)
    {
        // drdynvc is a static virtual channel: strip+reassemble the CHANNEL_PDU_HEADER and MPPC-inflate
        // compressed chunks (the host sends e.g. the GFX CAPS_ADVERTISE bulk-compressed here). Keyed
        // "drdynvc" so each direction shares the session's per-channel MPPC/reassembly state.
        var rc = new Cur(chanData);
        if (rc.Remaining < 8) { Raw(parent, "drdynvc.short", chanData); return; }
        var dvcMsg = s.ReassembleSvc("drdynvc", ref rc, out uint svcFlags);
        if (dvcMsg == null)
        {
            var pend = new Node("drdynvc.pending");
            pend.Channel = $"drdynvc({s.DrdynvcId})";
            pend.Field("svcFlags", "0x" + svcFlags.ToString("X8")).Raw(chanData);
            parent.Child(pend);
            return;
        }

        var c = new Cur(dvcMsg);
        byte header = c.U8();
        int cmd = (header >> 4) & 0xf;
        int sp = (header >> 2) & 0x3;
        int cbId = header & 0x3;

        var n = new Node("drdynvc." + DvcCmdName(cmd));
        n.Channel = $"drdynvc({s.DrdynvcId})";
        n.Field("cmd", "0x" + cmd.ToString("X") + " " + DvcCmdName(cmd));
        n.Raw(chanData); // whole drdynvc channel chunk echoed verbatim — round-trips
        parent.Child(n);

        switch (cmd)
        {
            case DVC_CMD_CAPABILITIES:
            {
                // pad(1) version(2) [+ priority charges]
                if (c.Remaining >= 1) c.U8(); // (header byte already consumed; pad is part of body)
                int version = c.Remaining >= 2 ? c.U16le() : 1;
                n.Field("version", version);
                if (dir == RdpDir.ClientToServer) s.DvcVersion = version;
                break;
            }
            case DVC_CMD_CREATE:
            {
                int channelId = DvcReadId(ref c, cbId);
                string name = ReadCStr(ref c);
                n.Field("channelId", channelId).Field("name", name);
                // CREATE from server carries the name; the client echoes a CREATE response with a
                // creationStatus (4-byte). Detect that by remaining bytes.
                if (dir == RdpDir.ServerToClient)
                {
                    s.DvcById[channelId] = name;
                }
                else if (c.Remaining >= 4)
                {
                    uint status = c.U32le();
                    n.Field("creationStatus", "0x" + status.ToString("X8") + (status == 0 ? " OK" : " FAIL"));
                }
                break;
            }
            case DVC_CMD_CLOSE:
            {
                int channelId = DvcReadId(ref c, cbId);
                n.Field("channelId", channelId).Field("name", s.DvcById.GetValueOrDefault(channelId, "?"));
                s.DvcById.Remove(channelId);
                break;
            }
            case DVC_CMD_DATA:
            case DVC_CMD_DATA_FIRST:
            case DVC_CMD_DATA_COMPRESSED:
            case DVC_CMD_DATA_FIRST_COMPRESSED:
            {
                bool isFirst = cmd is DVC_CMD_DATA_FIRST or DVC_CMD_DATA_FIRST_COMPRESSED;
                bool compressed = cmd is DVC_CMD_DATA_COMPRESSED or DVC_CMD_DATA_FIRST_COMPRESSED;
                int channelId = DvcReadId(ref c, cbId);
                long total = 0;
                if (isFirst) { total = sp == 0 ? c.U8() : sp == 1 ? c.U16le() : c.U32le(); }
                string dvcName = s.DvcById.GetValueOrDefault(channelId, "?");
                n.Field("channelId", channelId).Field("name", dvcName);
                if (isFirst) n.Field("totalLen", total);

                var chunk = c.Rest().ToArray();

                // v3 compressed: ZGFX-inflate through a per-channel context before reassembly.
                if (compressed)
                {
                    var z = s.DvcZgfx.TryGetValue(channelId, out var zz) ? zz : (s.DvcZgfx[channelId] = new Zgfx());
                    var inflated = z.Decompress(chunk);
                    if (inflated == null) { n.Field("zgfx", "inflate-failed").Field("chunkLen", chunk.Length); break; }
                    n.Field("zgfx", $"{chunk.Length}B->{inflated.Length}B");
                    chunk = inflated;
                }

                var complete = s.ReassembleDvc(channelId, isFirst, isFirst ? total : 0, chunk);
                if (complete == null) { n.Field("reassembly", "pending"); break; }

                DispatchDvcPayload(s, dir, n, channelId, dvcName, complete);
                break;
            }
            case DVC_CMD_SOFT_SYNC_REQUEST:
            case DVC_CMD_SOFT_SYNC_RESPONSE:
                n.Field("note", "soft-sync (multitransport) — not detailed");
                break;
            default:
                n.Field("note", "unknown drdynvc cmd");
                break;
        }
    }

    // Route a complete (reassembled, decompressed) DVC payload to its extension decoder.
    private static void DispatchDvcPayload(RdpSession s, RdpDir dir, Node parent, int channelId, string name, byte[] data)
    {
        if (name == "Microsoft::Windows::RDS::Graphics")
        {
            DecodeGfx(s, parent, channelId, data);
            return;
        }
        if (name == "Microsoft::Windows::RDS::DisplayControl") { DecodeDisplayControl(parent, data); return; }
        if (name == "AUDIO_PLAYBACK_DVC" || name == "AUDIO_INPUT") { DecodeRdpSndInline(parent, name, data); return; }
        if (name.StartsWith("Microsoft::Windows::RDS::Video") || name.StartsWith("Microsoft::Windows::RDS::Geometry"))
        { var n = new Node("dvc.payload"); n.Field("channel", name).Field("len", data.Length).FieldHex("preview", data); parent.Child(n); return; }
        // Camera (MS-RDPECAM) and anything else: log structurally-unknown payload.
        var u = new Node("dvc.payload");
        u.Field("channel", name).Field("len", data.Length).FieldHex("preview", data);
        parent.Child(u);
    }

    private static void DecodeRdpSndInline(Node parent, string chan, byte[] data)
    {
        var n = new Node("audio.dvc");
        n.Field("channel", chan).Field("len", data.Length);
        var c = new Cur(data);
        if (c.Remaining >= 4) { byte t = c.U8(); c.U8(); n.Field("msgType", "0x" + t.ToString("X2")); }
        parent.Child(n);
    }

    private static void DecodeDisplayControl(Node parent, byte[] data)
    {
        var c = new Cur(data);
        var n = new Node("rdpedisp");
        if (c.Remaining >= 8)
        {
            uint type = c.U32le(); c.U32le();
            n.Field("type", "0x" + type.ToString("X") + " " + (type == 5 ? "CAPS" : type == 2 ? "MONITOR_LAYOUT" : "?"));
            if (type == 5 && c.Remaining >= 12)
                n.Field("maxNumMonitors", c.U32le()).Field("maxMonitorAreaFactorA", c.U32le()).Field("maxMonitorAreaFactorB", c.U32le());
            else if (type == 2 && c.Remaining >= 8)
            {
                c.U32le(); int num = (int)c.U32le(); n.Field("numMonitors", num);
                if (c.Remaining >= 20) { c.U32le(); n.Field("left", c.U32le()).Field("top", c.U32le()).Field("width", c.U32le()).Field("height", c.U32le()); }
            }
        }
        parent.Child(n);
    }

    // ==============================================================================================
    // RDPEGFX command stream (already ZGFX-inflated by the caller)
    // ==============================================================================================
    private static void DecodeGfx(RdpSession s, Node parent, int channelId, byte[] payload)
    {
        var gfx = new Node("rdpgfx");
        gfx.Field("len", payload.Length);
        parent.Child(gfx);

        // The Graphics DVC payload is a ZGFX (RDP8) message: descriptor 0xE0 (single) / 0xE1 (multipart)
        // then segment(s). The host streams its GFX PDUs this way (s2c). Client acks (c2s) are sent
        // UNCOMPRESSED — no descriptor — and start with a valid 8-byte RDPGFX header (cmdId in the low
        // byte, flags=0, sane pduLength). Some host DATA PDUs on the Graphics channel are neither: raw
        // continuation bytes with no descriptor (high-entropy, e.g. 0x5d-leading tiles). Classify the
        // three cases so we never feed garbage into the shared ZGFX history (which would desync every
        // subsequent frame) and never misreport raw bytes as a "bad pduLength".
        byte[] inflated = payload;
        if (payload.Length >= 1 && (payload[0] == 0xe0 || payload[0] == 0xe1))
        {
            var z = s.GfxZgfx.TryGetValue(channelId, out var zz) ? zz : (s.GfxZgfx[channelId] = new Zgfx());
            var r = z.Decompress(payload);
            if (r == null) { gfx.Field("error", $"zgfx inflate failed ({payload.Length}B)"); return; }
            gfx.Field("zgfx", $"{payload.Length}B->{r.Length}B");
            inflated = r;
        }
        else if (!LooksLikeGfxPdu(payload))
        {
            // Not a ZGFX message and not a plaintext RDPGFX header. Do NOT touch the ZGFX context.
            gfx.Field("note", "non-ZGFX GFX-channel DATA (raw continuation/opaque)")
               .Field("firstByte", "0x" + payload[0].ToString("X2"));
            gfx.FieldHex("preview", payload.AsSpan(0, Math.Min(16, payload.Length)));
            return;
        }

        int off = 0;
        while (off + 8 <= inflated.Length)
        {
            var c = new Cur(inflated.AsSpan(off));
            int cmdId = c.U16le(); int flags = c.U16le(); long pduLength = c.U32le();
            if (pduLength < 8 || off + pduLength > inflated.Length) { gfx.Field("error", $"bad pduLength {pduLength} at off {off}"); break; }
            var body = inflated.AsSpan(off + 8, (int)pduLength - 8);
            var p = new Node("gfx." + GfxName(cmdId));
            p.Field("cmdId", "0x" + cmdId.ToString("X4") + " " + GfxName(cmdId)).Field("pduLength", pduLength);
            DecodeGfxBody(cmdId, body, p);
            gfx.Child(p);
            off += (int)pduLength;
        }
    }

    private static void DecodeGfxBody(int cmdId, ReadOnlySpan<byte> body, Node p)
    {
        var c = new Cur(body);
        switch (cmdId)
        {
            case 0x0013: // CAPS_CONFIRM
                if (c.Remaining >= 8) { p.Field("version", "0x" + c.U32le().ToString("X")); c.U32le(); if (c.Remaining >= 4) p.Field("flags", "0x" + c.U32le().ToString("X")); }
                break;
            case 0x0012: // CAPS_ADVERTISE
                if (c.Remaining >= 2) { int n = c.U16le(); p.Field("capsSetCount", n); var arr = new JsonArray(); for (int i = 0; i < n && c.Remaining >= 8; i++) { uint v = c.U32le(); uint len = c.U32le(); uint f = c.Remaining >= 4 ? c.U32le() : 0; if (len > 4) c.O += (int)(len - 4); arr.Add((JsonNode?)$"0x{v:X}/flags=0x{f:X}"); } p.Field("caps", arr); }
                break;
            case 0x000e: // RESET_GRAPHICS
                if (c.Remaining >= 8) p.Field("width", c.U32le()).Field("height", c.U32le());
                break;
            case 0x0009: // CREATE_SURFACE
                if (c.Remaining >= 7) p.Field("surfaceId", c.U16le()).Field("width", c.U16le()).Field("height", c.U16le()).Field("pixelFormat", "0x" + c.U8().ToString("X2"));
                break;
            case 0x000a: // DELETE_SURFACE
                if (c.Remaining >= 2) p.Field("surfaceId", c.U16le());
                break;
            case 0x000f: // MAP_SURFACE_TO_OUTPUT
                if (c.Remaining >= 12) { p.Field("surfaceId", c.U16le()); c.U16le(); p.Field("originX", c.U32le()).Field("originY", c.U32le()); }
                break;
            case 0x0017: // MAP_SURFACE_TO_SCALED_OUTPUT
                if (c.Remaining >= 20) { p.Field("surfaceId", c.U16le()); c.U16le(); p.Field("originX", c.U32le()).Field("originY", c.U32le()).Field("targetWidth", c.U32le()).Field("targetHeight", c.U32le()); }
                break;
            case 0x000b: // START_FRAME
                if (c.Remaining >= 8) p.Field("timestamp", c.U32le()).Field("frameId", c.U32le());
                break;
            case 0x000c: // END_FRAME
                if (c.Remaining >= 4) p.Field("frameId", c.U32le());
                break;
            case 0x000d: // FRAME_ACKNOWLEDGE
                if (c.Remaining >= 12) p.Field("queueDepth", "0x" + c.U32le().ToString("X")).Field("frameId", c.U32le()).Field("totalFramesDecoded", c.U32le());
                break;
            case 0x0016: // QOE_FRAME_ACKNOWLEDGE
                if (c.Remaining >= 12) p.Field("frameId", c.U32le()).Field("timestamp", c.U32le()).Field("timeDiffSE", c.U16le()).Field("timeDiffEDR", c.U16le());
                break;
            case 0x0001: // WIRE_TO_SURFACE_1
                if (c.Remaining >= 17)
                {
                    p.Field("surfaceId", c.U16le());
                    int codecId = c.U16le();
                    p.Field("codecId", "0x" + codecId.ToString("X4") + " " + GfxCodec(codecId));
                    p.Field("pixelFormat", "0x" + c.U8().ToString("X2"));
                    p.Field("destLeft", c.U16le()).Field("destTop", c.U16le()).Field("destRight", c.U16le()).Field("destBottom", c.U16le());
                    long bdl = c.U32le();
                    p.Field("bitmapDataLength", bdl + " (opaque " + GfxCodec(codecId) + " bitstream)");
                }
                break;
            case 0x0002: // WIRE_TO_SURFACE_2
                if (c.Remaining >= 9) { p.Field("surfaceId", c.U16le()); int codecId = c.U16le(); p.Field("codecId", "0x" + codecId.ToString("X4") + " " + GfxCodec(codecId)); p.Field("codecContextId", c.U32le()).Field("pixelFormat", "0x" + c.U8().ToString("X2")); p.Field("bitstream", (body.Length - 9) + "B opaque"); }
                break;
            case 0x0004: // SOLIDFILL
                if (c.Remaining >= 8) { p.Field("surfaceId", c.U16le()); p.Field("fillPixel", "0x" + c.U32le().ToString("X8")); p.Field("fillRectCount", c.U16le()); }
                break;
            case 0x0005: // SURFACE_TO_SURFACE
                if (c.Remaining >= 2) p.Field("surfaceIdSrc", c.U16le());
                break;
            case 0x0006: // SURFACE_TO_CACHE
                if (c.Remaining >= 2) p.Field("surfaceId", c.U16le());
                break;
            case 0x0007: // CACHE_TO_SURFACE
                if (c.Remaining >= 4) p.Field("cacheSlot", c.U16le()).Field("surfaceId", c.U16le());
                break;
            default:
                if (body.Length > 0) p.FieldHex("body", body);
                break;
        }
    }

    // Heuristic: does this look like an uncompressed RDPGFX PDU header (cmdId(2) flags(2) pduLength(4))?
    // Used to tell plaintext client acks / small PDUs from raw non-ZGFX continuation bytes.
    private static bool LooksLikeGfxPdu(byte[] b)
    {
        if (b.Length < 8) return false;
        int cmdId = b[0] | (b[1] << 8);
        int flags = b[2] | (b[3] << 8);
        long pduLength = (uint)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
        return cmdId is >= 0x0001 and <= 0x0018 && flags == 0 && pduLength >= 8 && pduLength <= b.Length;
    }

    private static string GfxName(int cmdId) => cmdId switch
    {
        0x0001 => "WIRE_TO_SURFACE_1", 0x0002 => "WIRE_TO_SURFACE_2", 0x0003 => "DELETE_ENCODING_CONTEXT",
        0x0004 => "SOLIDFILL", 0x0005 => "SURFACE_TO_SURFACE", 0x0006 => "SURFACE_TO_CACHE",
        0x0007 => "CACHE_TO_SURFACE", 0x0008 => "EVICT_CACHE_ENTRY", 0x0009 => "CREATE_SURFACE",
        0x000a => "DELETE_SURFACE", 0x000b => "START_FRAME", 0x000c => "END_FRAME",
        0x000d => "FRAME_ACKNOWLEDGE", 0x000e => "RESET_GRAPHICS", 0x000f => "MAP_SURFACE_TO_OUTPUT",
        0x0011 => "CACHE_IMPORT_REPLY", 0x0012 => "CAPS_ADVERTISE", 0x0013 => "CAPS_CONFIRM",
        0x0015 => "MAP_SURFACE_TO_WINDOW", 0x0016 => "QOE_FRAME_ACKNOWLEDGE",
        0x0017 => "MAP_SURFACE_TO_SCALED_OUTPUT", 0x0018 => "MAP_SURFACE_TO_SCALED_WINDOW",
        _ => "0x" + cmdId.ToString("X4"),
    };

    private static string GfxCodec(int id) => id switch
    {
        0x0000 => "UNCOMPRESSED", 0x0008 => "CLEARCODEC", 0x0009 => "CAPROGRESSIVE", 0x000a => "PLANAR",
        0x000b => "AVC420", 0x000d => "CAPROGRESSIVE_V2", 0x000e => "AVC444", 0x000f => "AVC444v2",
        _ => "0x" + id.ToString("X4"),
    };

    private static string DvcCmdName(int cmd) => cmd switch
    {
        DVC_CMD_CREATE => "Create", DVC_CMD_DATA_FIRST => "DataFirst", DVC_CMD_DATA => "Data",
        DVC_CMD_CLOSE => "Close", DVC_CMD_CAPABILITIES => "Capabilities",
        DVC_CMD_DATA_FIRST_COMPRESSED => "DataFirstCompressed", DVC_CMD_DATA_COMPRESSED => "DataCompressed",
        DVC_CMD_SOFT_SYNC_REQUEST => "SoftSyncRequest", DVC_CMD_SOFT_SYNC_RESPONSE => "SoftSyncResponse",
        _ => "0x" + cmd.ToString("X"),
    };

    private static int DvcReadId(ref Cur c, int cbId) => cbId == 0 ? c.U8() : cbId == 1 ? c.U16le() : (int)c.U32le();

    private static string ReadCStr(ref Cur c)
    {
        var sb = new System.Text.StringBuilder();
        while (c.Remaining > 0) { byte b = c.U8(); if (b == 0) break; sb.Append((char)b); }
        return sb.ToString();
    }

    private static void Raw(Node parent, string name, ReadOnlySpan<byte> b)
    {
        var n = new Node(name); n.FieldHex("bytes", b); n.Raw(b); parent.Child(n);
    }
}
