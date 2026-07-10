using System.Text;

namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// Server-side RDP PDU builders for the VNC→RDP bridge. These synthesize the PDUs the browser client
/// (<c>wwwroot/lib/rdpweb/protocol.js</c>) expects the SERVER to send during the connection sequence,
/// then wrap desktop content as fastpath bitmap updates. They are the inverse of the client serializers
/// in <c>protocol.js</c> and are written from scratch — the existing C# RDP code is decode-and-echo, not
/// a PDU builder.
///
/// Everything the client reads over the I/O channel is delivered as an MCS <b>Send-Data-Indication</b>
/// (<see cref="McsSendDataIndication"/>) wrapping a slow-path share PDU. Fastpath output uses its own
/// tiny header (<see cref="FastPathBitmapUpdate"/>).
/// </summary>
internal static class RdpServerEncoders
{
    // ── Framing ─────────────────────────────────────────────────────────────────────────────────
    // MCS choice byte is (application << 2). Application codes per T.125 (mirror protocol.js).
    private const int MCS_ATTACH_USER_CONFIRM = 11;
    private const int MCS_CHANNEL_JOIN_CONFIRM = 15;
    private const int MCS_SEND_DATA_INDICATION = 26;

    /// <summary>TPKT (0x03 0x00 len16be) + X.224 Data (LI=2, 0xF0, EOT=0x80) around a body.</summary>
    public static byte[] TpktX224(ReadOnlySpan<byte> body)
    {
        int total = 4 + 3 + body.Length;
        var w = new W();
        w.U8(0x03).U8(0x00).U16be(total);
        w.U8(0x02).U8(0xF0).U8(0x80);
        w.Bytes(body);
        return w.ToArray();
    }

    // PER helpers (as used by the MCS domain PDUs in protocol.js).
    private static void PerWriteLength(W w, int value)
    {
        if (value > 0x7f) w.U16be(value | 0x8000); else w.U8(value);
    }
    private static void PerWriteInteger16(W w, int value, int minimum) => w.U16be((value - minimum) & 0xffff);

    /// <summary>
    /// MCS Send-Data-Indication (T.125): choice, initiator(1001-based), channelId, priority/segmentation
    /// byte 0x70, PER length, then the payload. This is the exact frame <c>_mcsSendDataIndication</c>
    /// parses in the client.
    /// </summary>
    public static byte[] McsSendDataIndication(int userId, int channelId, ReadOnlySpan<byte> payload)
    {
        var w = new W();
        w.U8(MCS_SEND_DATA_INDICATION << 2);
        PerWriteInteger16(w, userId, 1001);   // initiator
        PerWriteInteger16(w, channelId, 0);   // channelId
        w.U8(0x70);                           // dataPriority + segmentation
        PerWriteLength(w, payload.Length);
        w.Bytes(payload);
        return w.ToArray();
    }

    /// <summary>MCS Attach-User-Confirm: choice, result=0 (rt-successful), granted userId (1001-based).</summary>
    public static byte[] AttachUserConfirm(int userId)
    {
        var w = new W();
        w.U8(MCS_ATTACH_USER_CONFIRM << 2 | 0x02); // choice with the "user id present" bit
        w.U8(0x00);                                // result = rt-successful (PER enumerated)
        PerWriteInteger16(w, userId, 1001);        // initiator (granted user channel)
        return w.ToArray();
    }

    /// <summary>
    /// MCS Channel-Join-Confirm: choice+flags, result=0, initiator(1001-based), requested channelId(0-based),
    /// then the (same) channelId echoed back. Matches <c>_onChannelJoinConfirm</c>, which reads choice +
    /// result and accepts.
    /// </summary>
    public static byte[] ChannelJoinConfirm(int userId, int channelId)
    {
        var w = new W();
        w.U8(MCS_CHANNEL_JOIN_CONFIRM << 2 | 0x02); // choice with the "channelId present" bit
        w.U8(0x00);                                 // result = rt-successful
        PerWriteInteger16(w, userId, 1001);         // initiator
        PerWriteInteger16(w, channelId, 0);         // requested
        PerWriteInteger16(w, channelId, 0);         // channelId (granted)
        return w.ToArray();
    }

    // ── BER (MCS Connect-Response envelope) ─────────────────────────────────────────────────────
    private static void BerWriteLength(W w, int len)
    {
        if (len > 0x7f)
        {
            if (len <= 0xff) { w.U8(0x81).U8(len); }
            else { w.U8(0x82).U16be(len); }
        }
        else w.U8(len);
    }
    private static void BerTag(W w, int tag, int len) { w.U8(tag); BerWriteLength(w, len); }
    private static byte[] BerInteger(int value)
    {
        // Minimal big-endian, T.125 uses 1-byte where possible.
        var w = new W();
        if (value <= 0xff) { w.U8(0x02).U8(0x01).U8(value); }
        else if (value <= 0xffff) { w.U8(0x02).U8(0x02).U16be(value); }
        else { w.U8(0x02).U8(0x04).U32le(value); } // large values unused here
        return w.ToArray();
    }
    private static byte[] BerEnumerated(int value) { var w = new W(); w.U8(0x0A).U8(0x01).U8(value); return w.ToArray(); }

    // GCC / T.124 constants (mirror protocol.js).
    private static readonly int[] T124_OID = { 0, 0, 20, 124, 0, 1 };
    private const string H221_SC_KEY = "McDn";

    /// <summary>
    /// MCS Connect-Response ([MS-RDPBCGR] 2.2.1.4): BER application tag 102, result=0, calledConnectId,
    /// domainParameters, then an octet string holding the GCC conference-create-response that wraps the
    /// server user data (SC_CORE + SC_NET). Parsed by <c>mcsParseConnectResponse</c>/<c>parseServerUserData</c>.
    /// </summary>
    /// <param name="channelIds">Granted static channel ids, positionally matching the client's channel list (empty for MVP).</param>
    public static byte[] ConnectResponse(int ioChannelId, uint selectedProtocol, IReadOnlyList<int> channelIds)
    {
        // 1) Server user data: SC_CORE (0x0C01) + SC_NET (0x0C03).
        var ud = new W();
        // SC_CORE: version(4) clientRequestedProtocols(4) earlyCapabilityFlags(4). length includes the
        // 4-byte header. earlyCaps 0 → skipChannelJoin=false so the deterministic join sequence runs.
        ud.U16le(0x0C01).U16le(16).U32le(0x00080004).U32le(selectedProtocol).U32le(0x00000000);
        // SC_NET: MCSChannelId(2) channelCount(2) then one id per channel (each padded to even count).
        int count = channelIds.Count;
        int scNetLen = 8 + count * 2;
        int pad = (count % 2 == 1) ? 2 : 0; // channel id array padded to a 4-byte boundary
        ud.U16le(0x0C03).U16le(scNetLen + pad).U16le(ioChannelId).U16le(count);
        foreach (var id in channelIds) ud.U16le(id);
        if (pad != 0) ud.U16le(0x0000);
        var userData = ud.ToArray();

        // 2) GCC conference-create-response wrapping the user data.
        var gcc = GccConferenceCreateResponse(userData);

        // 3) MCS Connect-Response BER envelope.
        var inner = new W();
        inner.Bytes(BerEnumerated(0));                 // result = rt-successful
        inner.Bytes(BerInteger(0));                    // calledConnectId
        // domainParameters SEQUENCE (8 integers) — values echo the client's "target".
        var dp = new W();
        // maxChannelIds, maxUserIds, maxTokenIds, numPriorities, minThroughput, maxHeight,
        // maxMCSPDUsize (65535), protocolVersion — matches macRDP's proven domainParameters.
        foreach (var v in new[] { 34, 3, 0, 1, 0, 1, 0xFFFF, 2 }) dp.Bytes(BerInteger(v));
        var dpArr = dp.ToArray();
        BerTag(inner, 0x30, dpArr.Length); inner.Bytes(dpArr);   // SEQUENCE
        BerTag(inner, 0x04, gcc.Length); inner.Bytes(gcc);       // OCTET STRING (userData)
        var innerArr = inner.ToArray();

        var w = new W();
        w.U8(0x7F).U8(102);                            // APPLICATION 102 (Connect-Response), high tag form
        BerWriteLength(w, innerArr.Length);
        w.Bytes(innerArr);
        return w.ToArray();
    }

    private static byte[] GccConferenceCreateResponse(ReadOnlySpan<byte> userData)
    {
        // PER-encoded ConferenceCreateResponse (T.124). Layout mirrors the client's
        // gccParseConferenceCreateResponse read sequence, inverted.
        var w = new W();
        w.U8(0x00);                                    // choice
        // object identifier (T.124), PER length 5
        PerWriteLength(w, 5);
        w.U8(((T124_OID[0] << 4) & (T124_OID[1] & 0x0f)));
        w.U8(T124_OID[2]).U8(T124_OID[3]).U8(T124_OID[4]).U8(T124_OID[5]);
        // ConnectData::connectPDU length + body
        var body = new W();
        body.U8(0x00);                                 // choice (ConferenceCreateResponse)
        PerWriteInteger16(body, 1001, 1001);           // nodeID (userID base)
        // tag (PER integer, 1 byte)
        PerWriteLength(body, 1); body.U8(1);
        body.U8(0x00);                                 // result = success (enumerated)
        body.U8(0x01);                                 // numberOfUserData set
        body.U8(0xC0);                                 // choice: h221NonStandard present
        // H.221 SC key, PER octet stream min 4
        PerWriteLength(body, 0); body.Bytes(Encoding.ASCII.GetBytes(H221_SC_KEY));
        PerWriteLength(body, userData.Length);
        body.Bytes(userData);
        var bodyArr = body.ToArray();
        PerWriteLength(w, bodyArr.Length);
        w.Bytes(bodyArr);
        return w.ToArray();
    }

    // ── Licensing ───────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Server License Error PDU — STATUS_VALID_CLIENT ([MS-RDPELE] 2.2.2.4). Security header flags
    /// SEC_LICENSE_PKT(0x0080), then bMsgType=ERROR_ALERT(0xFF). <c>_onLicensing</c> treats 0xFF as
    /// "no license needed".
    /// </summary>
    public static byte[] LicensingValidClient()
    {
        var w = new W();
        w.U16le(0x0080).U16le(0x0000);   // security header: SEC_LICENSE_PKT
        w.U8(0xFF);                      // bMsgType = ERROR_ALERT
        w.U8(0x03);                      // flags (LicenseProtocolVersion 3)
        w.U16le(0x0010);                 // wMsgSize
        w.U32le(0x00000007);             // dwErrorCode = STATUS_VALID_CLIENT
        w.U32le(0x00000002);             // dwStateTransition = ST_NO_TRANSITION
        w.U16le(0x0000).U16le(0x0000);   // bbErrorInfo blob: wBlobType, wBlobLen=0
        return w.ToArray();
    }

    // ── Share-control / share-data PDUs ─────────────────────────────────────────────────────────
    private const int PDUTYPE_DEMANDACTIVE = 0x11;
    private const int PDUTYPE_DATAPDU = 0x17;
    private const int PDUTYPE2_SYNCHRONIZE = 0x1F;
    private const int PDUTYPE2_CONTROL = 0x14;
    private const int PDUTYPE2_FONTMAP = 0x28;
    private const int CTRLACTION_COOPERATE = 0x0004;
    private const int CTRLACTION_GRANTED_CONTROL = 0x0002;
    private const int STREAM_LOW = 0x01;

    private const int ServerChannelId = 0x03EA; // 1002, server's PDU source

    private static byte[] ShareControl(int pduType, int pduSource, ReadOnlySpan<byte> body)
    {
        var w = new W();
        int total = 6 + body.Length;
        w.U16le(total);
        w.U16le(pduType | 0x0010);   // pduType low nibble + TS_PROTOCOL_VERSION (0x10) high nibble
        w.U16le(pduSource);
        w.Bytes(body);
        return w.ToArray();
    }

    private static byte[] ShareData(uint shareId, int pduType2, ReadOnlySpan<byte> body)
    {
        // Share Data Header ([MS-RDPBCGR] 2.2.8.1.1.1.2) inside a DATAPDU share-control header.
        var sd = new W();
        sd.U32le(shareId);
        sd.U8(0x00);                 // pad1
        sd.U8(STREAM_LOW);           // streamId
        sd.U16le(body.Length + 4);   // uncompressedLength (body + this header's trailing 4? per spec: total)
        sd.U8(pduType2);
        sd.U8(0x00);                 // compressedType
        sd.U16le(0x0000);            // compressedLength
        sd.Bytes(body);
        return ShareControl(PDUTYPE_DATAPDU, ServerChannelId, sd.ToArray());
    }

    /// <summary>TS_SYNCHRONIZE_PDU: messageType=1 (SYNCMSGTYPE_SYNC), targetUser.</summary>
    public static byte[] Synchronize(uint shareId, int targetUser)
    {
        var b = new W(); b.U16le(0x0001).U16le(targetUser);
        return ShareData(shareId, PDUTYPE2_SYNCHRONIZE, b.ToArray());
    }

    /// <summary>TS_CONTROL_PDU: action, grantId, controlId.</summary>
    public static byte[] Control(uint shareId, int action, int grantId, int controlId)
    {
        var b = new W(); b.U16le(action).U16le(grantId).U32le(controlId);
        return ShareData(shareId, PDUTYPE2_CONTROL, b.ToArray());
    }

    public static byte[] ControlCooperate(uint shareId) => Control(shareId, CTRLACTION_COOPERATE, 0, 0);
    public static byte[] ControlGrantedControl(uint shareId, int userId)
        => Control(shareId, CTRLACTION_GRANTED_CONTROL, userId, ServerChannelId);

    /// <summary>TS_FONT_MAP_PDU: numberEntries=0, totalNumEntries=0, mapFlags=0x0003 (FIRST|LAST), entrySize=4.</summary>
    public static byte[] FontMap(uint shareId)
    {
        var b = new W(); b.U16le(0).U16le(0).U16le(0x0003).U16le(0x0004);
        return ShareData(shareId, PDUTYPE2_FONTMAP, b.ToArray());
    }

    /// <summary>
    /// Server Demand Active PDU ([MS-RDPBCGR] 2.2.1.13.1). Carries a minimal capability set the browser
    /// accepts. <c>_onDemandActiveControl</c> reads shareID and replies Confirm Active + finalization; it
    /// does not strictly validate individual capsets, so a conservative fixed set suffices.
    /// </summary>
    public static byte[] DemandActive(uint shareId, int width, int height)
    {
        var caps = RdpServerCapsets.ServerDemand(width, height);
        var b = new W();
        b.U32le(shareId);
        // sourceDescriptor: length-prefixed "RDP\0".
        var src = Encoding.ASCII.GetBytes("RDP\0");
        b.U16le(src.Length);                 // lengthSourceDescriptor
        b.U16le(caps.Length + 4);            // lengthCombinedCapabilities = numCaps(2)+pad(2)+capsets
        b.Bytes(src);
        b.U16le(RdpServerCapsets.ServerCapCount); // numberCapabilities
        b.U16le(0x0000);                     // pad2octets
        b.Bytes(caps);
        b.U32le(0x00000000);                 // sessionId
        return ShareControl(PDUTYPE_DEMANDACTIVE, ServerChannelId, b.ToArray());
    }

    // ── Fastpath bitmap output ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// Wraps one or more bitmap rectangles as a fastpath Server Update PDU with a single
    /// FASTPATH_UPDATETYPE_BITMAP (0x01) update ([MS-RDPBCGR] 2.2.9.1.2.1.2). The client's
    /// <c>_handleFastPath</c> parses the 2/3-byte header then dispatches by update code.
    /// </summary>
    /// <summary>
    /// Max bitmap-update body per SINGLE fastpath PDU. mstsc/MS-RDP reject large SINGLE updates and even
    /// over-large fragmented ones (aco.cpp "MF re-assembly failed" → 0x510d), so a real Windows host
    /// streams many small (~15 KB) SINGLE updates. We match: cap the pixel payload so the whole PDU stays
    /// well under FASTPATH_MAX_PACKET_SIZE (0x3FFF). Mirrors macRDP's <c>sendBitmap</c> (0x3B00 cap).
    /// </summary>
    public const int MaxFastPathBody = 0x3B00;

    public static byte[] FastPathBitmapUpdate(ReadOnlySpan<byte> bitmapUpdateData)
    {
        // Fastpath UPDATE header ([MS-RDPBCGR] 2.2.9.1.2.1): updateHeader(1) then updateData size(2 LE).
        // updateHeader low nibble = updateCode BITMAP(0x01); bits 4-5 fragmentation=SINGLE(0);
        // bits 6-7 compression=none(0). The client's parseUpdateHeader reads this size and slices the
        // body by it — omitting it made the client read the first 2 bitmap bytes AS the size (the bug).
        var inner = new W();
        inner.U8(0x01);                      // updateHeader: BITMAP, SINGLE fragment, no compression
        inner.U16le(bitmapUpdateData.Length); // updateData size
        inner.Bytes(bitmapUpdateData);
        var body = inner.ToArray();

        // Fastpath output PDU (protocol.js): fpOutputHeader(1)=action(0), then the whole-PDU length as a
        // PER length INCLUDING the header + length bytes. We always use the 2-byte form (matches macRDP).
        var w = new W();
        w.U8(0x00);                          // fpOutputHeader: action FASTPATH_OUTPUT_ACTION_FASTPATH(0)
        int total = 1 + 2 + body.Length;     // action(1) + 2-byte length + body
        if (total > 0x7fff) throw new InvalidOperationException(
            $"fastpath output PDU too large ({total}B > 0x7FFF); band the frame first");
        w.U16be(total | 0x8000);             // high bit set => 2-byte length; value = whole PDU size
        w.Bytes(body);
        return w.ToArray();
    }

    /// <summary>
    /// Builds one TS_UPDATE_BITMAP body ([MS-RDPBCGR] 2.2.9.1.1.3.1.2): updateType=BITMAP(0x0001),
    /// numberRectangles, then each TS_BITMAP_DATA.
    ///
    /// IMPORTANT: the browser client's uncompressed bitmap path (<c>client.js</c> + <c>color.js</c>) is
    /// hard-wired to <b>16bpp RGB565, little-endian, bottom-up</b> rows — it ignores the parsed
    /// <c>bitsPerPixel</c> field, always treats the stream as 2 bytes/pixel, runs <c>flipV</c> (so the
    /// wire is bottom-up) and <c>buf2RGBA</c> (565→888). So <see cref="BitmapRect.Data"/> MUST be 565
    /// bottom-up and <c>bitmapLength = width*height*2</c>.
    /// </summary>
    public static byte[] BitmapUpdateData(IReadOnlyList<BitmapRect> rects)
    {
        var w = new W();
        w.U16le(0x0001);                     // updateType = UPDATETYPE_BITMAP
        w.U16le(rects.Count);
        foreach (var r in rects)
        {
            w.U16le(r.Left).U16le(r.Top).U16le(r.Right).U16le(r.Bottom);
            w.U16le(r.Width).U16le(r.Height);
            w.U16le(16);                     // bitsPerPixel (client renders 565 regardless; kept honest)
            w.U16le(0x0000);                 // flags: uncompressed (no BITMAP_COMPRESSION)
            w.U16le(r.Data.Length);          // bitmapLength = width*height*2
            w.Bytes(r.Data);
        }
        return w.ToArray();
    }
}

/// <summary>One uncompressed 16bpp RGB565 bitmap rectangle (bottom-up rows) for a bitmap update.</summary>
internal sealed record BitmapRect(int Left, int Top, int Right, int Bottom, int Width, int Height, byte[] Data);
