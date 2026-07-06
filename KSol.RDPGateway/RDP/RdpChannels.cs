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
            case "rdpsnd": DecodeRdpSnd(s, dir, hdr, payload); break;
            case "cliprdr": DecodeClipRdr(hdr, payload); break;
            case "rdpdr": DecodeRdpDr(hdr, payload); break;
            default: hdr.FieldHex("payload", payload); break;
        }
    }

    private static void DecodeRdpSnd(RdpSession s, RdpDir dir, Node parent, byte[] payload)
    {
        var n = new Node("rdpsnd");

        // A pending legacy WaveInfo body arrives as a bare PDU (no SNDPROLOG): 4-byte bPad then the rest
        // of the audio data. Detect it by the pending-wave state before parsing a SNDPROLOG header.
        // (Mirrors rdpsnd.js _onWaveBody.)
        if (s.Media != null && s.PendingWave is { } pw)
        {
            s.PendingWave = null;
            if (payload.Length >= 4)
            {
                var rest = payload.AsSpan(4);
                var full = new byte[pw.head.Length + rest.Length];
                pw.head.CopyTo(full, 0);
                rest.CopyTo(full.AsSpan(pw.head.Length));
                EmitRemoteWave(s, dir, pw.formatNo, full);
            }
            n.Field("note", "wave body (legacy stitch)").Field("len", payload.Length);
            parent.Child(n);
            return;
        }

        var c = new Cur(payload);
        byte msgType = 0; int bodySize = 0;
        if (c.Remaining >= 4)
        {
            msgType = c.U8(); c.U8(); bodySize = c.U16le();
            n.Field("msgType", "0x" + msgType.ToString("X2") + " " + msgType switch
            {
                0x01 => "WaveInfo", 0x02 => "Wave", 0x03 => "Close", 0x04 => "WaveConfirm",
                0x05 => "Training", 0x06 => "Formats", 0x07 => "CryptKey", 0x08 => "WaveEncrypt",
                0x09 => "UDPWave", 0x0A => "UDPWaveLast", 0x0B => "Quality", 0x0C => "Volume",
                0x0D => "WaveV2", 0x38 => "WaveV2", _ => "?",
            }).Field("bodySize", bodySize);
        }
        n.Field("len", payload.Length);
        parent.Child(n);

        // Media extraction (server→client only): negotiate formats, then capture wave PCM.
        if (s.Media == null || dir != RdpDir.ServerToClient) return;
        var body = c.Rest(); // bytes after the 4-byte SNDPROLOG
        switch (msgType)
        {
            case 0x07: // SNDC_FORMATS (msgType 0x07 server formats) — [MS-RDPEA] 2.2.2.1
            {
                int total = ParseSndFormats(s.SndFormats, body);
                int hn = Math.Min(28, body.Length);
                s.Media?.OnDiagnostic($"rdpsnd SNDC_FORMATS: total={total} pcm={s.SndFormats.Count} bodyLen={body.Length} head={Convert.ToHexString(body.Slice(0, hn))}");
                break;
            }
            case 0x02: // SNDC_WAVE — the "WaveInfo" PDU that STARTS a wave ([MS-RDPEA] 2.2.3.3):
                       // wTimeStamp(2) wFormatNo(2) cBlockNo(1) bPad(3) Data(4 — first 4 audio bytes).
                       // The REST of the wave follows as a separate bare PDU (no SNDPROLOG): bPad(4) Data.
                s.Media?.OnDiagnostic($"rdpsnd WaveInfo(0x02) fmtNo={(body.Length >= 4 ? body[2] | (body[3] << 8) : -1)} bodyLen={body.Length}");
                if (body.Length >= 12)
                {
                    int formatNo = body[2] | (body[3] << 8);
                    s.PendingWave = (formatNo, body.Slice(8, 4).ToArray());
                }
                break;
            case 0x0D: // SNDC_WAVE2: wTimeStamp(2) wFormatNo(2) cBlockNo(1) bPad(3) dwAudioTimeStamp(4) data
                s.Media?.OnDiagnostic($"rdpsnd Wave2 fmtNo={(body.Length >= 4 ? body[2] | (body[3] << 8) : -1)} bodyLen={body.Length}");
                if (body.Length >= 12)
                {
                    int formatNo = body[2] | (body[3] << 8);
                    EmitRemoteWave(s, dir, formatNo, body.Slice(12).ToArray());
                }
                break;
            default:
                s.Media?.OnDiagnostic($"rdpsnd msg 0x{msgType:X2} bodyLen={body.Length}");
                break;
        }
    }

    // SNDC_FORMATS body (Server Audio Formats and Version PDU, [MS-RDPEA] 2.2.2.1, the part AFTER the
    // 4-byte SNDPROLOG): dwFlags(4) dwVolume(4) dwPitch(4) wDGramPort(2) wNumberOfFormats(2)
    // cLastBlockConfirmed(1) wVersion(2) bPad(1) = 20-byte preamble, then wNumberOfFormats * AUDIO_FORMAT.
    // (Earlier this was parsed as a 19-byte preamble — dropping cLastBlockConfirmed — which shifted every
    // AUDIO_FORMAT by one byte so NO wFormatTag matched PCM. The host advertises 30 formats incl. PCM.)
    // Keep only PCM (wFormatTag 1). The agreed index space (wFormatNo in WAVE PDUs) is the PCM-only
    // subset in advertised order — see rdpsnd.js: the client advertises ONLY PCM, so the agreed list ==
    // the server's PCM formats. Returns the TOTAL number of advertised formats (PCM + non-PCM) for diag.
    private static int ParseSndFormats(List<PcmFormat> formats, ReadOnlySpan<byte> body)
    {
        formats.Clear();
        if (body.Length < 20) return 0;
        var c = new Cur(body);
        c.U32le(); c.U32le(); c.U32le(); c.U16le();
        int count = c.U16le(); c.U8(); c.U16le(); c.U8();
        for (int i = 0; i < count && c.Remaining >= 18; i++)
        {
            int wFormatTag = c.U16le();
            int nChannels = c.U16le();
            long nSamplesPerSec = c.U32le();
            c.U32le(); // nAvgBytesPerSec
            c.U16le(); // nBlockAlign
            int wBitsPerSample = c.U16le();
            int cbSize = c.U16le();
            if (cbSize > 0) c.O += cbSize;
            if (wFormatTag == 0x0001) // WAVE_FORMAT_PCM
                formats.Add(new PcmFormat((int)nSamplesPerSec, wBitsPerSample, nChannels));
        }
        return count;
    }

    private static void EmitRemoteWave(RdpSession s, RdpDir dir, int formatNo, byte[] pcm)
    {
        if (s.Media == null || pcm.Length == 0 || s.SndFormats.Count == 0) return;
        // wFormatNo indexes the AGREED format list, which (matching the browser's rdpsnd.js — it
        // advertises ONLY PCM, so the agreed list = the server's PCM subset in advertised order) is our
        // PCM-only `SndFormats`. Fall back to formats[0] if out of range, exactly like _deliverWave.
        var fmt = formatNo >= 0 && formatNo < s.SndFormats.Count ? s.SndFormats[formatNo] : s.SndFormats[0];
        s.Media.OnRemoteAudio(fmt, pcm, s.ElapsedMs);
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
                    s.Media?.OnDiagnostic($"DVC create id={channelId} name={name}");
                    // The camera enumerator precedes the per-device channels, which are opened with a
                    // client-assigned (non-fixed) name — flag enumeration so DispatchDvcPayload can route
                    // those unknown channels to the ECAM frame extractor.
                    if (name == "RDCamera_Device_Enumerator") s.CameraEnumerated = true;
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
        if (name == "AUDIO_PLAYBACK_DVC")
        {
            // Modern Windows carries remote sound (rdpsnd / MS-RDPEA) over THIS dynamic channel, not the
            // static "rdpsnd" channel — the DVC payload is a complete SNDPROLOG-framed rdpsnd message, so
            // it runs through the same wave-extraction path. (The client binds a full RdpSnd parser here.)
            DecodeRdpSnd(s, dir, parent, data);
            return;
        }
        if (name == "AUDIO_INPUT")
        {
            DecodeRdpSndInline(parent, name, data);
            if (s.Media != null)
            {
                if (data.Length >= 1) s.Media.OnDiagnostic($"audin msg 0x{data[0]:X2} {dir} len={data.Length}");
                ExtractMicAudio(s, dir, data);
            }
            return;
        }
        if (name.StartsWith("Microsoft::Windows::RDS::Video") || name.StartsWith("Microsoft::Windows::RDS::Geometry"))
        { var n = new Node("dvc.payload"); n.Field("channel", name).Field("len", data.Length).FieldHex("preview", data); parent.Child(n); return; }
        // Camera (MS-RDPECAM): per-device channels carry StartStreams (s2c, sets geometry) and
        // SampleResponse (c2s, NV12 frame). Their channel name is client-assigned (not a fixed string),
        // so once the RDCamera_Device_Enumerator has appeared we treat any otherwise-unknown channel as a
        // camera-device candidate; ExtractCameraFrame validates by ECAM msgId (0x0F/0x12) and ignores the
        // rest, so non-camera payloads pass through harmlessly to the diagnostic log below.
        if (s.Media != null && s.CameraEnumerated)
        {
            ExtractCameraFrame(s, dir, data);
        }
        // Camera and anything else: log structurally-unknown payload.
        var u = new Node("dvc.payload");
        u.Field("channel", name).Field("len", data.Length).FieldHex("preview", data);
        parent.Child(u);
    }

    // audin (MS-RDPEAI) mic extraction. The DVC payload is a SNDIN message: msgId(1) then body.
    //   server→client: FORMATS(0x02) advertises capture formats; OPEN(0x03) picks one by index into the
    //     PCM subset (matching the client's reply order). FORMATCHANGE(0x07) re-selects.
    //   client→server: DATA(0x06) carries raw PCM after the 1-byte header.
    private static void ExtractMicAudio(RdpSession s, RdpDir dir, byte[] data)
    {
        if (data.Length < 1) return;
        byte msgId = data[0];
        var body = data.AsSpan(1);
        if (dir == RdpDir.ServerToClient)
        {
            switch (msgId)
            {
                case 0x02: // MSG_SNDIN_FORMATS: NumFormats(4) cbSize(4) then AUDIO_FORMAT records
                    if (body.Length >= 8)
                    {
                        s.AudinFormats.Clear();
                        int num = body[0] | (body[1] << 8) | (body[2] << 16) | (body[3] << 24);
                        var fc = new Cur(body); fc.U32le(); fc.U32le();
                        for (int i = 0; i < num && fc.Remaining >= 18; i++)
                        {
                            int tag = fc.U16le(); int ch = fc.U16le(); long rate = fc.U32le();
                            fc.U32le(); fc.U16le(); int bits = fc.U16le(); int cb = fc.U16le();
                            if (cb > 0) fc.O += cb;
                            if (tag == 0x0001) s.AudinFormats.Add(new PcmFormat((int)rate, bits, ch));
                        }
                    }
                    break;
                case 0x03: // MSG_SNDIN_OPEN: FramesPerPacket(4) initialFormat(4) ...
                case 0x07: // MSG_SNDIN_FORMATCHANGE: NewFormat(4)
                    // tracked implicitly: EmitMic uses the active format index; OPEN's index is at off 4,
                    // FORMATCHANGE's at off 0. Store as the "current" first format if list non-empty.
                    break;
            }
            // Active format: OPEN/FORMATCHANGE select an index; we keep the simplest robust choice — use
            // index 0 unless an OPEN told us otherwise. Stash it on the session via a single-slot list.
            if (msgId == 0x03 && body.Length >= 8)
            {
                int idx = body[4] | (body[5] << 8) | (body[6] << 16) | (body[7] << 24);
                s.AudinOpenFormat = idx >= 0 && idx < s.AudinFormats.Count ? idx : 0;
            }
            else if (msgId == 0x07 && body.Length >= 4)
            {
                int idx = body[0] | (body[1] << 8) | (body[2] << 16) | (body[3] << 24);
                s.AudinOpenFormat = idx >= 0 && idx < s.AudinFormats.Count ? idx : 0;
            }
            return;
        }
        // client→server DATA(0x06): raw PCM.
        if (msgId == 0x06 && body.Length > 0)
        {
            var fmt = s.AudinFormats.Count > 0 && s.AudinOpenFormat < s.AudinFormats.Count
                ? s.AudinFormats[s.AudinOpenFormat]
                : new PcmFormat(44100, 16, 1);
            s.Media!.OnMicAudio(fmt, body, s.ElapsedMs);
        }
    }

    // Camera (MS-RDPECAM) NV12 extraction. Payload = version(1) msgId(1) body.
    //   server→client StartStreams(0x0F): streamIndex(1) + 30-byte media type → sets geometry.
    //   client→server SampleResponse(0x12): streamIndex(1) + raw NV12 frame.
    private static void ExtractCameraFrame(RdpSession s, RdpDir dir, byte[] data)
    {
        if (data.Length < 2) return;
        byte msgId = data[1];
        var body = data.AsSpan(2);
        if (dir == RdpDir.ServerToClient && msgId == 0x0F && body.Length >= 1 + 26)
        {
            // media type at body[1..]: Format(1) Width(4) Height(4) FrameRateNum(4) ...
            int fmt = body[1];
            int width = body[2] | (body[3] << 8) | (body[4] << 16) | (body[5] << 24);
            int height = body[6] | (body[7] << 8) | (body[8] << 16) | (body[9] << 24);
            int fps = body[10] | (body[11] << 8) | (body[12] << 16) | (body[13] << 24);
            s.CamMediaType = (width, height, fps <= 0 ? 30 : fps, fmt);
        }
        else if (dir == RdpDir.ClientToServer && msgId == 0x12 && body.Length >= 1)
        {
            var frame = body.Slice(1); // skip streamIndex
            if (frame.Length == 0 || s.CamMediaType is not { } mt) return;
            string fmtName = mt.fmt switch { 0x04 => "nv12", 0x05 => "i420", 0x02 => "mjpeg", 0x01 => "h264", _ => "raw" };
            s.Media!.OnCameraFrame(mt.width, mt.height, frame, fmtName, s.ElapsedMs);
        }
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
            DecodeGfxBody(s, cmdId, body, p);
            gfx.Child(p);
            off += (int)pduLength;
        }
    }

    private static void DecodeGfxBody(RdpSession s, int cmdId, ReadOnlySpan<byte> body, Node p)
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
                if (c.Remaining >= 8) { uint rw = c.U32le(), rh = c.U32le(); p.Field("width", rw).Field("height", rh); if (s.Media != null) s.GfxCompositor.OnResetGraphics((int)rw, (int)rh); }
                break;
            case 0x0009: // CREATE_SURFACE
                if (c.Remaining >= 7) { int sid = c.U16le(), sw = c.U16le(), sh = c.U16le(); p.Field("surfaceId", sid).Field("width", sw).Field("height", sh).Field("pixelFormat", "0x" + c.U8().ToString("X2")); if (s.Media != null) s.GfxCompositor.OnCreateSurface(sid, sw, sh); }
                break;
            case 0x000a: // DELETE_SURFACE
                if (c.Remaining >= 2) { int sid = c.U16le(); p.Field("surfaceId", sid); if (s.Media != null) s.GfxCompositor.OnDeleteSurface(sid); }
                break;
            case 0x000f: // MAP_SURFACE_TO_OUTPUT
                if (c.Remaining >= 12) { int sid = c.U16le(); c.U16le(); uint ox = c.U32le(), oy = c.U32le(); p.Field("surfaceId", sid).Field("originX", ox).Field("originY", oy); if (s.Media != null) s.GfxCompositor.OnMapSurfaceToOutput(sid, (int)ox, (int)oy); }
                break;
            case 0x0017: // MAP_SURFACE_TO_SCALED_OUTPUT
                if (c.Remaining >= 20) { p.Field("surfaceId", c.U16le()); c.U16le(); p.Field("originX", c.U32le()).Field("originY", c.U32le()).Field("targetWidth", c.U32le()).Field("targetHeight", c.U32le()); }
                break;
            case 0x000b: // START_FRAME
                if (c.Remaining >= 8)
                {
                    long ts = c.U32le(); long fid = c.U32le();
                    p.Field("timestamp", ts).Field("frameId", fid);
                    s.GfxFrameId = fid; s.GfxFrameTs = ts;
                }
                break;
            case 0x000c: // END_FRAME
                if (c.Remaining >= 4) p.Field("frameId", c.U32le());
                if (s.Media != null && s.GfxCompositor.HasProgressive) s.GfxCompositor.OnEndFrame(s.Media, s.GfxFrameTs != 0 ? s.GfxFrameTs : s.ElapsedMs, s.Pointer);
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
                    int surfaceId = c.U16le();
                    p.Field("surfaceId", surfaceId);
                    int codecId = c.U16le();
                    p.Field("codecId", "0x" + codecId.ToString("X4") + " " + GfxCodec(codecId));
                    p.Field("pixelFormat", "0x" + c.U8().ToString("X2"));
                    p.Field("destLeft", c.U16le()).Field("destTop", c.U16le()).Field("destRight", c.U16le()).Field("destBottom", c.U16le());
                    long bdl = c.U32le();
                    p.Field("bitmapDataLength", bdl + " (opaque " + GfxCodec(codecId) + " bitstream)");
                    if (s.Media != null && c.Remaining >= bdl && bdl > 0)
                        ExtractDesktopVideo(s, codecId, surfaceId, c.Rest().Slice(0, (int)bdl));
                }
                break;
            case 0x0002: // WIRE_TO_SURFACE_2
                if (c.Remaining >= 9)
                {
                    int surfaceId2 = c.U16le(); p.Field("surfaceId", surfaceId2);
                    int codecId = c.U16le();
                    p.Field("codecId", "0x" + codecId.ToString("X4") + " " + GfxCodec(codecId));
                    p.Field("codecContextId", c.U32le()).Field("pixelFormat", "0x" + c.U8().ToString("X2"));                 // bitmapDataLength (4 bytes, [MS-RDPEGFX] 2.2.2.2) — the codec bitstream length.
                    long bitmapDataLen = c.Remaining >= 4 ? c.U32le() : 0;
                    var bitstream = c.Rest();
                    if (bitmapDataLen > 0 && bitmapDataLen <= bitstream.Length) bitstream = bitstream[..(int)bitmapDataLen];
                    // RemoteFX Progressive (0x0009) / V2 (0x000d): decode + composite server-side for recording.
                    if (s.Media != null && (codecId == 0x0009 || codecId == 0x000d) && bitstream.Length > 0)
                    {
                        bool any = s.GfxCompositor.OnProgressive(surfaceId2, bitstream);
                        p.Field("bitstream", bitstream.Length + "B progressive" + (any ? " (decoded)" : " (no tiles)"));
                    }
                    else p.Field("bitstream", bitstream.Length + "B opaque");
                }
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

    // Extract the H.264 access unit(s) from a WIRE_TO_SURFACE_1 bitstream and push to the media sink.
    // AVC420 ([MS-RDPEGFX] 2.2.4.4): RFX_AVC420_METABLOCK then the Annex-B H.264. AVC444/v2 (2.2.4.5):
    // a 4-byte word { LC[31:30], cbAvc420EncodedBitstream1[29:0] } then stream1 (a full AVC420 bitmap
    // stream, the 4:2:0 main/luma view) and optionally stream2 (chroma-aux). v1 records the main view
    // and forwards the aux bytes for callers that want them. Non-AVC codecs have no server pixel decoder
    // and are passed through as GfxVideoCodec.Other (the recorder marks the recording unsupported-codec).
    private static void ExtractDesktopVideo(RdpSession s, int codecId, int surfaceId, ReadOnlySpan<byte> bitmapData)
    {
        var media = s.Media!;
        if (codecId == 0x000b) // AVC420
        {
            var au = StripAvc420Metablock(bitmapData);
            media.OnDesktopVideo(GfxVideoCodec.Avc420, surfaceId, au, ReadOnlySpan<byte>.Empty,
                s.GfxFrameId, s.GfxFrameTs);
        }
        else if (codecId == 0x000e || codecId == 0x000f) // AVC444 / AVC444v2
        {
            if (bitmapData.Length < 4) return;
            uint w = (uint)(bitmapData[0] | (bitmapData[1] << 8) | (bitmapData[2] << 16) | (bitmapData[3] << 24));
            int lc = (int)(w >> 30);
            int cb1 = (int)(w & 0x3FFFFFFF);
            var after = bitmapData.Slice(4);
            // LC: 0 = both streams (cb1 = stream1 length), 1 = stream1 only, 2 = stream2 only.
            ReadOnlySpan<byte> s1, s2;
            if (lc == 1) { s1 = after; s2 = ReadOnlySpan<byte>.Empty; }
            else if (lc == 2) { s1 = ReadOnlySpan<byte>.Empty; s2 = after; }
            else
            {
                if (cb1 > after.Length) cb1 = after.Length;
                s1 = after.Slice(0, cb1);
                s2 = after.Slice(cb1);
            }
            var main = s1.IsEmpty ? ReadOnlySpan<byte>.Empty : StripAvc420Metablock(s1);
            var aux = s2.IsEmpty ? ReadOnlySpan<byte>.Empty : StripAvc420Metablock(s2);
            media.OnDesktopVideo(codecId == 0x000f ? GfxVideoCodec.Avc444v2 : GfxVideoCodec.Avc444,
                surfaceId, main, aux, s.GfxFrameId, s.GfxFrameTs);
        }
        else
        {
            // ClearCodec / Progressive / planar / uncompressed: no server-side pixel decoder.
            media.OnDesktopVideo(GfxVideoCodec.Other, surfaceId, ReadOnlySpan<byte>.Empty,
                ReadOnlySpan<byte>.Empty, s.GfxFrameId, s.GfxFrameTs);
        }
    }

    // Skip the RFX_AVC420_METABLOCK ([MS-RDPEGFX] 2.2.4.4.1): numRegionRects(4) + numRegionRects*RFX_RECT
    // (8 bytes each) + numRegionRects*RFX_AVC420_QUANT_QUALITY (2 bytes each). What remains is the H.264
    // Annex-B access unit. On any inconsistency, return the input unchanged (best-effort).
    private static ReadOnlySpan<byte> StripAvc420Metablock(ReadOnlySpan<byte> stream)
    {
        if (stream.Length < 4) return stream;
        uint numRegionRects = (uint)(stream[0] | (stream[1] << 8) | (stream[2] << 16) | (stream[3] << 24));
        long meta = 4L + numRegionRects * 8L + numRegionRects * 2L;
        if (numRegionRects == 0 || meta < 0 || meta > stream.Length) return stream;
        return stream.Slice((int)meta);
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
