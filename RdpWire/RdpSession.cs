// RdpSession — stateful, bidirectional structural decoder/encoder for the decrypted RDP stream.
//
// One instance per MITM connection. Both directions feed the same instance (so server-learned state —
// the SC_NET channel-id table, the negotiated DVC version, GFX surfaces — labels client traffic too).
// Each direction has its own framing reassembly buffer.
//
// Public surface: Feed(dir, bytes) returns the bytes to forward. When every PDU in the chunk round-trips
// byte-identically, it returns OUR re-encoded bytes; otherwise it returns the original chunk unchanged
// (so the live session never breaks) while still emitting per-PDU MISMATCH events.

using System.Text.Json.Nodes;

namespace KSol.RDPGateway.RDP;

public sealed class RdpSession
{
    private readonly IRdpEventSink _sink;
    private readonly IRdpMediaSink? _media;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    // Per-direction inbound framing reassembly (the proxy hands us arbitrary partial reads).
    private readonly List<byte> _rxC2S = new();
    private readonly List<byte> _rxS2C = new();

    // ---- shared connection state (mirrors RdpProtocol fields) ----
    private uint _selectedProtocol = 2;
    private int _ioChannelId = 1003;        // global I/O (SC_NET first id)
    private int _userId;
    private bool _skipChannelJoin;
    // Static virtual channels, positional (client CS_NET order) -> server SC_NET id.
    private readonly List<string> _staticChannelNames = new(); // from CS_NET (client)
    private readonly List<int> _staticChannelIds = new();      // from SC_NET (server), positional
    private readonly Dictionary<int, string> _channelById = new();
    private int _drdynvcId;

    // drdynvc dynamic channels.
    private readonly Dictionary<int, string> _dvcById = new();
    private int _dvcVersion = 1;
    // Per-DVC reassembly for DATA_FIRST + DATA* (key: dvc channel id).
    private readonly Dictionary<int, DvcReasm> _dvcReasm = new();
    // Per-DVC ZGFX context for v3 compressed data, and a dedicated one for the GFX channel.
    private readonly Dictionary<int, Zgfx> _dvcZgfx = new();
    private readonly Dictionary<int, Zgfx> _gfxZgfx = new();
    // Per-static-channel MPPC context: with INFO_COMPRESSION the host bulk-compresses static-channel
    // chunks (CHANNEL_PACKET_COMPRESSED). Keyed by "name|dir" since each direction has its own history.
    private readonly Dictionary<string, Mppc> _svcMppc = new();
    // Per-static-channel reassembly for CHANNEL_PDU_HEADER FIRST..LAST (key: channel name).
    private readonly Dictionary<string, SvcReasm> _svcReasm = new();

    private sealed class DvcReasm { public List<byte> Parts = new(); public long Total; }
    private sealed class SvcReasm { public List<byte> Parts = new(); public long Total; }

    public RdpSession(IRdpEventSink sink) : this(sink, null) { }
    public RdpSession(IRdpEventSink sink, IRdpMediaSink? media) { _sink = sink; _media = media; }

    // ---- media-extraction state (used only when a media sink is attached) ----
    // rdpsnd: negotiated PCM formats (indexed by wFormatNo) and the pending legacy WaveInfo stitch.
    private readonly List<PcmFormat> _sndFormats = new();
    private (int formatNo, byte[] head)? _pendingWave;
    // audin (mic): negotiated PCM formats, separate index space from rdpsnd, and the active OPEN index.
    private readonly List<PcmFormat> _audinFormats = new();
    private int _audinOpenFormat;
    // GFX: current frame from the most recent START_FRAME (per session — frames are not interleaved).
    private long _gfxFrameId;
    private long _gfxFrameTs;
    // Camera (RDPECAM): the negotiated current media type from StartStreams, used to size NV12 frames.
    private (int width, int height, int fps, int fmt)? _camMediaType;
    // Set once the server opens the RDCamera_Device_Enumerator DVC. Camera device channels are opened
    // afterwards with a client-assigned (non-fixed) name, so once enumeration is seen we treat unknown
    // channels carrying the ECAM version(1)/msgId framing as camera-device payloads.
    private bool _cameraEnumerated;
    // GFX progressive compositor: decodes RemoteFX Progressive (WIRE_TO_SURFACE_2) into a server-side
    // surface framebuffer and emits a composited BGRA frame to the media sink at each END_FRAME. Created
    // lazily on the first progressive tile (only when recording, i.e. a media sink is present).
    private GfxProgressiveCompositor? _gfxComp;

    internal IRdpMediaSink? Media => _media;
    internal GfxProgressiveCompositor GfxCompositor => _gfxComp ??= new GfxProgressiveCompositor();
    internal List<PcmFormat> SndFormats => _sndFormats;
    internal List<PcmFormat> AudinFormats => _audinFormats;
    internal int AudinOpenFormat { get => _audinOpenFormat; set => _audinOpenFormat = value; }
    internal (int formatNo, byte[] head)? PendingWave { get => _pendingWave; set => _pendingWave = value; }
    internal long GfxFrameId { get => _gfxFrameId; set => _gfxFrameId = value; }
    internal long GfxFrameTs { get => _gfxFrameTs; set => _gfxFrameTs = value; }
    internal (int width, int height, int fps, int fmt)? CamMediaType { get => _camMediaType; set => _camMediaType = value; }
    internal bool CameraEnumerated { get => _cameraEnumerated; set => _cameraEnumerated = value; }

    public void NotePatch(RdpDir dir, string what, uint val)
    {
        var n = new Node("PATCH") { };
        n.Field("field", what).Field("value", "0x" + val.ToString("X"));
        _sink.Emit(dir, _sw.ElapsedMilliseconds, n, new RoundTrip(true, 0, 0, -1));
    }

    /// <summary>Feed decrypted bytes for a direction; returns the bytes the proxy should forward.</summary>
    public byte[] Feed(RdpDir dir, ReadOnlySpan<byte> chunk)
    {
        var rx = dir == RdpDir.ClientToServer ? _rxC2S : _rxS2C;
        rx.AddRange(chunk.ToArray());

        var outBytes = new List<byte>(chunk.Length);
        bool allMatched = true;

        while (true)
        {
            int unitLen = NextUnitLength(rx);
            if (unitLen <= 0) break;                 // need more bytes (0) — keep buffering
            var unit = new byte[unitLen];
            rx.CopyTo(0, unit, 0, unitLen);
            rx.RemoveRange(0, unitLen);

            Node node;
            try { node = DecodeUnit(dir, unit); }
            catch (Exception ex)
            {
                node = new Node("raw");
                node.Field("error", ex.Message).FieldHex("bytes", unit);
                node.Raw(unit);
            }

            var re = node.Encode();
            bool match = re.Length == unit.Length && unit.AsSpan().SequenceEqual(re);
            int firstDiff = -1;
            if (!match)
            {
                int min = Math.Min(re.Length, unit.Length);
                for (int i = 0; i < min; i++) { if (re[i] != unit[i]) { firstDiff = i; break; } }
                if (firstDiff < 0) firstDiff = min;
                allMatched = false;
            }
            _sink.Emit(dir, _sw.ElapsedMilliseconds, node, new RoundTrip(match, unit.Length, re.Length, firstDiff));

            // Per the chosen policy: forward OUR re-encoded bytes when identical, else the original.
            outBytes.AddRange(match ? re : unit);
        }

        // If anything failed to round-trip, fall back to forwarding the exact original chunk slice that
        // corresponds to what we consumed this call. We already appended per-unit bytes above; since on a
        // mismatch we appended the original unit, outBytes already equals the original consumed bytes. The
        // `allMatched` flag is informational for callers/logs.
        _ = allMatched;
        return outBytes.ToArray();
    }

    // ---- framing: determine the length of the next complete unit (TPKT slow-path or fastpath) ----
    private static int NextUnitLength(List<byte> b)
    {
        if (b.Count < 1) return 0;
        byte first = b[0];
        if (first == 0x03)
        {
            if (b.Count < 4) return 0;
            int len = (b[2] << 8) | b[3];
            if (len < 4) return -1;
            if (b.Count < len) return 0;
            return len;
        }
        // fastpath: header(1) + PER-style length (1 or 2 bytes)
        if (b.Count < 2) return 0;
        byte len1 = b[1];
        int length, headerLen;
        if ((len1 & 0x80) != 0)
        {
            if (b.Count < 3) return 0;
            length = ((len1 & 0x7f) << 8) | b[2];
            headerLen = 3;
        }
        else { length = len1; headerLen = 2; }
        if (length < headerLen) return -1;
        if (b.Count < length) return 0;
        return length;
    }

    // ==============================================================================================
    // Top-level dispatch: TPKT slow-path vs fastpath.
    // ==============================================================================================
    private Node DecodeUnit(RdpDir dir, byte[] unit)
    {
        if (unit[0] == 0x03) return DecodeTpkt(dir, unit);
        return RdpFastPath.Decode(this, dir, unit);
    }

    private Node DecodeTpkt(RdpDir dir, byte[] unit)
    {
        var n = new Node("TPKT");
        var c = new Cur(unit);
        byte ver = c.U8(); byte rsv = c.U8(); int len = c.U16be();
        n.Field("version", ver).Field("length", len);
        n.Raw(unit.AsSpan(0, 4)); // TPKT header reproduced verbatim

        // X.224 follows. LI(1) code(1) ...
        var x224 = unit.AsSpan(4);
        return RdpX224.Decode(this, dir, n, x224);
    }

    // ---- accessors used by the layer decoders ----
    internal IRdpEventSink Sink => _sink;
    internal long ElapsedMs => _sw.ElapsedMilliseconds;
    internal uint SelectedProtocol { get => _selectedProtocol; set => _selectedProtocol = value; }
    internal int IoChannelId { get => _ioChannelId; set => _ioChannelId = value; }
    internal int UserId { get => _userId; set => _userId = value; }
    internal bool SkipChannelJoin { get => _skipChannelJoin; set => _skipChannelJoin = value; }
    internal List<string> StaticChannelNames => _staticChannelNames;
    internal List<int> StaticChannelIds => _staticChannelIds;
    internal Dictionary<int, string> ChannelById => _channelById;
    internal int DrdynvcId { get => _drdynvcId; set => _drdynvcId = value; }
    internal Dictionary<int, string> DvcById => _dvcById;
    internal int DvcVersion { get => _dvcVersion; set => _dvcVersion = value; }
    internal Dictionary<int, Zgfx> DvcZgfx => _dvcZgfx;
    internal Dictionary<int, Zgfx> GfxZgfx => _gfxZgfx;

    internal void RebuildChannelMap()
    {
        _channelById.Clear();
        for (int i = 0; i < _staticChannelNames.Count && i < _staticChannelIds.Count; i++)
            _channelById[_staticChannelIds[i]] = _staticChannelNames[i];
        _drdynvcId = 0;
        foreach (var kv in _channelById) if (kv.Value == "drdynvc") _drdynvcId = kv.Key;
    }

    internal string ChannelLabel(int id)
    {
        if (id == _ioChannelId) return $"I/O({id})";
        if (_channelById.TryGetValue(id, out var name)) return $"{name}({id})";
        if (id == _userId) return $"user({id})";
        return $"chan({id})";
    }

    // CHANNEL_PDU_HEADER flags ([MS-RDPBCGR] 2.2.6.1.1).
    private const uint CHANNEL_FLAG_FIRST = 0x00000001;
    private const uint CHANNEL_FLAG_LAST = 0x00000002;
    private const uint CHANNEL_PACKET_COMPRESSED = 0x00200000;
    private const uint CHANNEL_PACKET_AT_FRONT = 0x00400000;
    private const uint CHANNEL_PACKET_FLUSHED = 0x00800000;
    private const uint CHANNEL_COMPRESSION_TYPE_MASK = 0x000F0000;
    // PACKET_COMPR_TYPE_8K=0, _64K=1, _RDP6=2, _RDP61=3 (the type lives in the 0x000F0000 mask >> 16).

    // ---- static-channel CHANNEL_PDU_HEADER reassembly (returns complete payload or null) ----
    // Per-chunk MPPC-decompresses when CHANNEL_PACKET_COMPRESSED is set (the compression history spans
    // chunks, so we inflate each chunk before reassembling the FIRST..LAST message).
    internal byte[]? ReassembleSvc(string name, ref Cur r, out uint chFlags)
    {
        uint totalLen = r.U32le();
        chFlags = r.U32le();
        var chunk = r.Rest().ToArray();

        if ((chFlags & CHANNEL_PACKET_COMPRESSED) != 0)
        {
            int type = (int)((chFlags & CHANNEL_COMPRESSION_TYPE_MASK) >> 16); // 0=8K, 1=64K
            int level = type == 1 ? 1 : 0;                                     // RDP6/RDP61 fall back to 8K window
            string key = name + "|" + (level == 1 ? "64k" : "8k");
            if (!_svcMppc.TryGetValue(key, out var mppc)) _svcMppc[key] = mppc = new Mppc(level);
            int mflags = Mppc.PACKET_COMPRESSED
                | ((chFlags & CHANNEL_PACKET_AT_FRONT) != 0 ? Mppc.PACKET_AT_FRONT : 0)
                | ((chFlags & CHANNEL_PACKET_FLUSHED) != 0 ? Mppc.PACKET_FLUSHED : 0);
            var inflated = mppc.Decompress(chunk, mflags);
            if (inflated != null) chunk = inflated; // on failure, keep the raw chunk (logged via flags)
        }

        bool first = (chFlags & CHANNEL_FLAG_FIRST) != 0;
        bool last = (chFlags & CHANNEL_FLAG_LAST) != 0;
        if (first && last) return chunk;
        if (first) { _svcReasm[name] = new SvcReasm { Parts = new List<byte>(chunk), Total = totalLen }; return null; }
        if (!_svcReasm.TryGetValue(name, out var acc)) return null;
        acc.Parts.AddRange(chunk);
        if (!last) return null;
        _svcReasm.Remove(name);
        return acc.Parts.ToArray();
    }

    // ---- DVC DATA_FIRST/DATA reassembly (returns complete payload or null) ----
    internal byte[]? ReassembleDvc(int channelId, bool isFirst, long total, byte[] chunk)
    {
        if (isFirst)
        {
            if (chunk.Length >= total) return chunk;
            _dvcReasm[channelId] = new DvcReasm { Parts = new List<byte>(chunk), Total = total };
            return null;
        }
        if (_dvcReasm.TryGetValue(channelId, out var acc))
        {
            acc.Parts.AddRange(chunk);
            if (acc.Parts.Count < acc.Total) return null;
            _dvcReasm.Remove(channelId);
            return acc.Parts.ToArray();
        }
        return chunk; // unfragmented DATA with no prior FIRST
    }
}
