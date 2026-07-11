namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// Server side of the MS-RDPEGFX graphics pipeline on the "Microsoft::Windows::RDS::Graphics" dynamic
/// channel. Negotiates the client's CAPS_ADVERTISE, picks an H.264 mode (AVC444 or AVC420) from a capset
/// the client actually advertised AVC on (else RemoteFX Progressive), stands up one full-desktop surface,
/// and streams each frame as START_FRAME + WIRE_TO_SURFACE_1 (AVC) / _2 (progressive) + END_FRAME. Every
/// server→client message is ZGFX-wrapped in raw (uncompressed) RDP8 segments — valid per [MS-RDPEGFX]
/// 2.2.5 and what the browser's ZgfxDecode expects. Ported from macRDP's RdpGfxServer; wire structures
/// mirror <c>wwwroot/lib/rdpweb/rdpgfx.js</c>.
/// </summary>
internal sealed class RdpGfxServer
{
    // RDPGFX command ids ([MS-RDPEGFX] 2.2.1).
    private const ushort CMDID_WIRETOSURFACE_1 = 0x0001;
    private const ushort CMDID_WIRETOSURFACE_2 = 0x0002;
    private const ushort CMDID_DELETESURFACE = 0x000a;
    private const ushort CMDID_STARTFRAME = 0x000b;
    private const ushort CMDID_ENDFRAME = 0x000c;
    private const ushort CMDID_FRAMEACKNOWLEDGE = 0x000d;
    private const ushort CMDID_RESETGRAPHICS = 0x000e;
    private const ushort CMDID_MAPSURFACETOOUTPUT = 0x000f;
    private const ushort CMDID_CACHEIMPORTOFFER = 0x0010;
    private const ushort CMDID_CACHEIMPORTREPLY = 0x0011;
    private const ushort CMDID_CAPSADVERTISE = 0x0012;
    private const ushort CMDID_CAPSCONFIRM = 0x0013;
    private const ushort CMDID_CREATESURFACE = 0x0009;
    private const ushort CMDID_QOEFRAMEACKNOWLEDGE = 0x0016;

    private const ushort CODECID_CAPROGRESSIVE = 0x0009;
    private const ushort CODECID_AVC420 = 0x000b;
    private const ushort CODECID_AVC444 = 0x000e;
    private const byte PIXEL_FORMAT_XRGB = 0x20;
    private const uint CAPVERSION_81 = 0x00080105;
    private const uint CAPVERSION_10_1 = 0x000A0100;

    private const uint SUSPEND_FRAME_ACK = 0xFFFFFFFF;
    private const uint FLAG_AVC420_ENABLED = 0x00000010; // v8.1 only
    private const uint FLAG_AVC_DISABLED = 0x00000020;   // v10+

    private const uint MaxUnacked = 8;

    private int _width, _height;
    private const ushort SurfaceId = 0;
    private readonly Action<byte[]> _send;   // sends one ZGFX-wrapped graphics message on the Graphics DVC
    private readonly ILogger _logger;

    /// <summary>Fires once AVC/GFX streaming is negotiated and the surface is mapped.</summary>
    public Action? OnActivated;
    /// <summary>Fires on every client FRAME_ACKNOWLEDGE (the unacked window gained room).</summary>
    public Action? OnFrameAcked;

    private readonly object _lock = new();
    private uint _frameId;
    private uint _lastAckedFrameId;
    private bool _acksSuspended;
    private ushort _codecId = CODECID_AVC420;

    public bool Active { get; private set; }
    public bool IsProgressive { get { lock (_lock) { return Active && _codecId == CODECID_CAPROGRESSIVE; } } }
    public bool IsAvc444 { get { lock (_lock) { return _codecId == CODECID_AVC444; } } }

    public RdpGfxServer(int width, int height, Action<byte[]> send, ILogger logger)
    {
        _width = width; _height = height; _send = send; _logger = logger;
    }

    // ── Inbound (client → server) ───────────────────────────────────────────────────────────────
    public void OnChannelData(byte[] data)
    {
        // rdpweb sends bare RDPGFX PDUs; mstsc wraps them in ZGFX. A bare PDU can't start with 0xE0/0xE1.
        byte[] payload = data;
        if (data.Length > 0 && (data[0] == 0xE0 || data[0] == 0xE1))
        {
            var inflated = ZgfxUnwrap(data);
            if (inflated == null) { _logger.LogWarning("rdpgfx: client ZGFX unwrap failed ({N}B)", data.Length); return; }
            payload = inflated;
        }
        int off = 0;
        while (off + 8 <= payload.Length)
        {
            var r = new Cur(payload.AsSpan(off));
            ushort cmdId = r.U16le();
            _ = r.U16le();                   // flags
            int pduLength = (int)r.U32le();
            if (pduLength < 8 || off + pduLength > payload.Length) { _logger.LogWarning("rdpgfx: bad pduLength {L}", pduLength); return; }
            var body = new Cur(payload.AsSpan(off + 8, pduLength - 8));
            Dispatch(cmdId, ref body);
            off += pduLength;
        }
    }

    private void Dispatch(ushort cmdId, ref Cur r)
    {
        switch (cmdId)
        {
            case CMDID_CAPSADVERTISE: OnCapsAdvertise(ref r); break;
            case CMDID_FRAMEACKNOWLEDGE: OnFrameAck(ref r); break;
            case CMDID_QOEFRAMEACKNOWLEDGE: break;
            case CMDID_CACHEIMPORTOFFER: SendPdu(CMDID_CACHEIMPORTREPLY, new byte[] { 0x00, 0x00 }); break;
            default: _logger.LogDebug("rdpgfx: unhandled client cmdId 0x{C:X}", cmdId); break;
        }
    }

    private void OnCapsAdvertise(ref Cur r)
    {
        if (r.Remaining < 2) return;
        int count = r.U16le();
        (uint version, uint flags)? bestAvc420 = null, bestAvc444 = null, bestAny = null;

        for (int i = 0; i < count; i++)
        {
            if (r.Remaining < 8) break;
            uint version = r.U32le();
            int len = (int)r.U32le();
            if (len < 4 || r.Remaining < len) break;
            uint flags = r.U32le();
            for (int s = 0; s < len - 4; s++) r.U8();  // skip rest of capset
            _logger.LogInformation("rdpgfx: capset v0x{V:X} flags=0x{F:X}", version, flags);
            if (version < CAPVERSION_81) continue;

            if (bestAny == null || version > bestAny.Value.version) bestAny = (version, flags);
            bool avcDisabled = (flags & FLAG_AVC_DISABLED) != 0;
            bool isAvc420 = version == CAPVERSION_81 ? (flags & FLAG_AVC420_ENABLED) != 0 : !avcDisabled;
            if (isAvc420 && (bestAvc420 == null || version > bestAvc420.Value.version)) bestAvc420 = (version, flags);
            if (version >= CAPVERSION_10_1 && !avcDisabled && (bestAvc444 == null || version > bestAvc444.Value.version)) bestAvc444 = (version, flags);
        }

        (uint version, uint flags) pick;
        if (bestAvc444 is { } a444) { pick = a444; _codecId = CODECID_AVC444; }
        else if (bestAvc420 is { } a420) { pick = a420; _codecId = CODECID_AVC420; }
        else if (bestAny is { } any) { pick = any; _codecId = CODECID_CAPROGRESSIVE; }
        else { _logger.LogInformation("rdpgfx: client advertised no v8.1+ capset — staying on bitmaps"); return; }

        string codecName = _codecId == CODECID_AVC444 ? "AVC444" : _codecId == CODECID_AVC420 ? "AVC420" : "RFX-Progressive";
        _logger.LogInformation("rdpgfx: CAPS_CONFIRM v0x{V:X} flags=0x{F:X} — {C} ON", pick.version, pick.flags, codecName);

        var confirm = new W();
        confirm.U32le(pick.version);
        confirm.U32le(4);            // capsDataLength
        confirm.U32le(pick.flags);   // echo verbatim
        SendPdu(CMDID_CAPSCONFIRM, confirm.ToArray());

        SendResetGraphics();
        SendCreateSurface();
        SendMapSurfaceToOutput();

        lock (_lock) { Active = true; _frameId = 0; _lastAckedFrameId = 0; }
        OnActivated?.Invoke();
    }

    private void OnFrameAck(ref Cur r)
    {
        if (r.Remaining < 8) return;
        uint queueDepth = r.U32le();
        uint acked = r.U32le();
        lock (_lock)
        {
            if (queueDepth == SUSPEND_FRAME_ACK) _acksSuspended = true;
            else { _acksSuspended = false; _lastAckedFrameId = acked; }
        }
        OnFrameAcked?.Invoke();
    }

    // ── Outbound setup PDUs ─────────────────────────────────────────────────────────────────────
    private void SendResetGraphics()
    {
        var b = new W();
        b.U32le((uint)_width); b.U32le((uint)_height); b.U32le(1); // width, height, monitorCount
        b.U32le(0); b.U32le(0); b.U32le((uint)(_width - 1)); b.U32le((uint)(_height - 1)); b.U32le(1); // monitor def
        int padded = 340 - 8 - b.B.Count;
        if (padded > 0) b.Zeros(padded);
        SendPdu(CMDID_RESETGRAPHICS, b.ToArray());
    }

    private void SendCreateSurface()
    {
        var b = new W();
        b.U16le(SurfaceId); b.U16le((ushort)_width); b.U16le((ushort)_height); b.U8(PIXEL_FORMAT_XRGB);
        SendPdu(CMDID_CREATESURFACE, b.ToArray());
    }

    private void SendMapSurfaceToOutput()
    {
        var b = new W();
        b.U16le(SurfaceId); b.U16le(0); b.U32le(0); b.U32le(0); // surfaceId, reserved, originX, originY
        SendPdu(CMDID_MAPSURFACETOOUTPUT, b.ToArray());
    }

    /// <summary>Resizes the GFX surface in place after a geometry change. No-op unless active.</summary>
    public void Resize(int w, int h)
    {
        lock (_lock)
        {
            if (!Active) return;
            _width = w; _height = h; _frameId = 0; _lastAckedFrameId = 0;
        }
        SendResetGraphics();
        var del = new W(); del.U16le(SurfaceId); SendPdu(CMDID_DELETESURFACE, del.ToArray());
        SendCreateSurface();
        SendMapSurfaceToOutput();
        _logger.LogInformation("rdpgfx: surface resized to {W}x{H}", w, h);
    }

    // ── Frame streaming ─────────────────────────────────────────────────────────────────────────
    public bool CanSubmitFrame
    {
        get { lock (_lock) { if (!Active) return false; if (_acksSuspended) return true; return _frameId - _lastAckedFrameId < MaxUnacked; } }
    }

    /// <summary>Number of frames sent but not yet acknowledged by the client — the congestion signal the
    /// adaptive-quality controller reads. 0 = client keeping up; near <c>MaxUnacked</c> = falling behind.
    /// Returns 0 while acks are suspended (client asked us to stop counting).</summary>
    public int UnackedDepth
    {
        get { lock (_lock) { if (!Active || _acksSuspended) return 0; return (int)(_frameId - _lastAckedFrameId); } }
    }

    /// <summary>The saturation point of the unacked window (frames), for scaling the congestion signal.</summary>
    public int UnackedWindow => (int)MaxUnacked;

    /// <summary>Ships one Annex-B H.264 frame covering the whole surface as START_FRAME +
    /// WIRE_TO_SURFACE_1 (AVC420 metablock, wrapped in the AVC444 envelope with LC=1 for codec 0x0e) +
    /// END_FRAME, batched into one ZGFX message.</summary>
    public void SubmitFrame(byte[] annexB)
    {
        uint fid;
        lock (_lock) { if (!Active) return; fid = ++_frameId; }

        var msg = new W();

        var start = new W();
        start.U32le(0);    // timestamp: none (avoids the bit-packed-timestamp range checks)
        start.U32le(fid);
        AppendPdu(msg, CMDID_STARTFRAME, start.ToArray());

        // RFX_AVC420_BITMAP_STREAM = metablock (one full-frame rect + qp/quality) then raw Annex-B.
        var avc420 = new W();
        avc420.U32le(1);                                        // numRegionRects
        avc420.U16le(0); avc420.U16le(0);                       // rect left, top
        avc420.U16le((ushort)_width); avc420.U16le((ushort)_height); // right, bottom (exclusive)
        avc420.U8(26);                                          // qpVal
        avc420.U8(100);                                         // qualityVal
        avc420.Bytes(annexB);
        var avc420Stream = avc420.ToArray();

        byte[] bitmapData;
        if (_codecId == CODECID_AVC444)
        {
            var w = new W();
            uint info = ((uint)avc420Stream.Length & 0x3FFFFFFF) | (1u << 30); // LC=1: luma-only, no chroma
            w.U32le(info);
            w.Bytes(avc420Stream);
            bitmapData = w.ToArray();
        }
        else bitmapData = avc420Stream;

        var w2s = new W();
        w2s.U16le(SurfaceId);
        w2s.U16le(_codecId);
        w2s.U8(PIXEL_FORMAT_XRGB);
        w2s.U16le(0); w2s.U16le(0);                            // destRect left, top
        w2s.U16le((ushort)_width); w2s.U16le((ushort)_height); // right, bottom (exclusive)
        w2s.U32le((uint)bitmapData.Length);                   // bitmapDataLength
        w2s.Bytes(bitmapData);
        AppendPdu(msg, CMDID_WIRETOSURFACE_1, w2s.ToArray());

        var end = new W(); end.U32le(fid);
        AppendPdu(msg, CMDID_ENDFRAME, end.ToArray());

        _send(ZgfxWrap(msg.ToArray()));
    }

    /// <summary>Ships RemoteFX Progressive frames (one WIRE_TO_SURFACE_2 per bitstream chunk), each its
    /// own ZGFX message (M6 supplies the encoder streams).</summary>
    public void SubmitProgressiveFrame(IReadOnlyList<byte[]> streams)
    {
        uint fid;
        lock (_lock) { if (!Active || _codecId != CODECID_CAPROGRESSIVE || streams.Count == 0) return; fid = ++_frameId; }
        uint thisFid = fid;
        for (int i = 0; i < streams.Count; i++)
        {
            if (i > 0) lock (_lock) { thisFid = ++_frameId; }
            var msg = new W();
            var start = new W(); start.U32le(0); start.U32le(thisFid);
            AppendPdu(msg, CMDID_STARTFRAME, start.ToArray());
            var w2s = new W();
            w2s.U16le(SurfaceId); w2s.U16le(CODECID_CAPROGRESSIVE); w2s.U32le(0); w2s.U8(PIXEL_FORMAT_XRGB);
            w2s.U32le((uint)streams[i].Length); w2s.Bytes(streams[i]);
            AppendPdu(msg, CMDID_WIRETOSURFACE_2, w2s.ToArray());
            var end = new W(); end.U32le(thisFid);
            AppendPdu(msg, CMDID_ENDFRAME, end.ToArray());
            _send(ZgfxWrap(msg.ToArray()));
        }
    }

    // ── PDU + ZGFX helpers ──────────────────────────────────────────────────────────────────────
    private static void AppendPdu(W w, ushort cmdId, byte[] body)
    {
        w.U16le(cmdId); w.U16le(0); w.U32le((uint)(8 + body.Length)); w.Bytes(body);
    }

    private void SendPdu(ushort cmdId, byte[] body)
    {
        var w = new W();
        AppendPdu(w, cmdId, body);
        _send(ZgfxWrap(w.ToArray()));
    }

    /// <summary>Wraps a message in RDP_SEGMENTED_DATA with raw (uncompressed) RDP8 segments (flag 0x04).</summary>
    private static byte[] ZgfxWrap(byte[] payload)
    {
        const int segMax = 65000;
        if (payload.Length <= segMax)
        {
            var w = new W();
            w.U8(0xE0);  // ZGFX_SEGMENTED_SINGLE
            w.U8(0x04);  // RDP8, uncompressed
            w.Bytes(payload);
            return w.ToArray();
        }
        int segments = (payload.Length + segMax - 1) / segMax;
        var mw = new W();
        mw.U8(0xE1);                          // ZGFX_SEGMENTED_MULTIPART
        mw.U16le(segments);
        mw.U32le((uint)payload.Length);
        int off = 0;
        while (off < payload.Length)
        {
            int n = Math.Min(segMax, payload.Length - off);
            mw.U32le((uint)(n + 1));           // segment size incl. flags byte
            mw.U8(0x04);
            mw.Bytes(payload.AsSpan(off, n));
            off += n;
        }
        return mw.ToArray();
    }

    private byte[]? ZgfxUnwrap(byte[] data)
    {
        var r = new Cur(data);
        if (r.Remaining < 1) return null;
        byte descriptor = r.U8();
        byte[]? RawSegment(ReadOnlySpan<byte> seg)
        {
            if (seg.Length < 1) return null;
            if ((seg[0] & 0x20) != 0) { _logger.LogWarning("rdpgfx: compressed client ZGFX segment — dropping"); return null; }
            return seg[1..].ToArray();
        }
        if (descriptor == 0xE0) return RawSegment(r.Rest());
        if (descriptor == 0xE1)
        {
            if (r.Remaining < 6) return null;
            int count = r.U16le();
            _ = r.U32le();
            var outb = new List<byte>();
            for (int i = 0; i < count; i++)
            {
                if (r.Remaining < 4) return null;
                int size = (int)r.U32le();
                if (size < 1 || r.Remaining < size) return null;
                var part = RawSegment(r.Take(size));
                if (part == null) return null;
                outb.AddRange(part);
            }
            return outb.ToArray();
        }
        return null;
    }
}
