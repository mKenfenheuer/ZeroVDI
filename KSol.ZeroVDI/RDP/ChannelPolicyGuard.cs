using System.Text;
using KSol.ZeroVDI.Models;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Protocol-level enforcement of the tenant <see cref="DevicePolicy"/> on the relayed (decrypted) RDP
/// stream. The console UI and the settings save endpoint clamp a <c>Disabled</c> feature, but the relay
/// was a transparent byte tunnel, so a modified client could still join the clipboard/audio static
/// channels or accept the microphone/camera dynamic channels. This guard watches the stream in both
/// directions and:
///
///   1. parses the client's MCS Connect Initial (CS_NET, [MS-RDPBCGR] 2.2.1.3.4) and refuses the session
///      if it requests a static channel a Disabled feature needs (<c>cliprdr</c>, <c>rdpsnd</c>);
///   2. learns dynamic-channel names from the host's DYNVC_CREATE PDUs ([MS-RDPEDYC] 2.2.2.1) on the
///      <c>drdynvc</c> static channel and refuses the session the moment the client ACCEPTS a blocked one
///      (creation status 0 — <c>AUDIO_INPUT</c>, <c>AUDIO_PLAYBACK_DVC</c>, the camera enumerator), i.e.
///      before a single byte of channel data can flow.
///
/// Refusing rather than stripping keeps the relay a byte pump (no PDU rewriting, no length fix-ups) and
/// gives the user a clear reason; the stock browser client never trips it because it already omits
/// blocked static channels and rejects blocked DVC creates. Only <c>Disabled</c> is enforceable here —
/// <c>Forced</c> (feature on) has no protocol-level meaning. Static-channel bulk compression (MPPC 8K/64K)
/// on <c>drdynvc</c> is inflated so the check cannot be dodged by compressing the CREATE exchange.
///
/// Fail-open by design: a framing/parse error disables inspection for that direction (logged) rather than
/// dropping a healthy session on a decoder edge case. Buffers are bounded.
/// </summary>
public sealed class ChannelPolicyGuard
{
    public sealed record Violation(string Feature, string Channel, string Message);

    // Channel name → user-facing feature label. Static channels ride CS_NET; the rest are DVC names.
    private static readonly Dictionary<string, string> ClipboardChannels = new() { ["cliprdr"] = "Clipboard" };
    private static readonly Dictionary<string, string> AudioChannels = new()
    {
        ["rdpsnd"] = "Remote audio",
        ["AUDIO_PLAYBACK_DVC"] = "Remote audio",
        ["AUDIO_PLAYBACK_LOSSY_DVC"] = "Remote audio",
    };
    private static readonly Dictionary<string, string> MicrophoneChannels = new() { ["AUDIO_INPUT"] = "Microphone" };
    private static readonly Dictionary<string, string> CameraChannels = new() { ["RDCamera_Device_Enumerator"] = "Camera" };

    private readonly Dictionary<string, string> _blocked; // channel name → feature label
    private readonly ILogger _logger;

    // Per-direction framing reassembly (the relay hands us arbitrary partial reads).
    private readonly Reassembler _c2s = new();
    private readonly Reassembler _s2c = new();
    private bool _c2sDisabled, _s2cDisabled;

    // Learned connection state.
    private readonly List<string> _clientChannels = new(); // CS_NET names, positional
    private int _drdynvcId;                                // SC_NET id of drdynvc (0 = unknown)
    private readonly Dictionary<int, string> _dvcNames = new();
    // drdynvc CHANNEL_PDU_HEADER reassembly + MPPC contexts, per direction.
    private readonly SvcState _svcC2S = new();
    private readonly SvcState _svcS2C = new();

    private const int MaxUnitBuffer = 1 << 20;   // 1 MB of unframed bytes before we give up (fail-open)
    private const int MaxSvcMessage = 64 * 1024; // drdynvc control messages are tiny; bound reassembly

    private ChannelPolicyGuard(Dictionary<string, string> blocked, ILogger logger)
    {
        _blocked = blocked;
        _logger = logger;
    }

    /// <summary>Build a guard for the policy, or null when nothing is Disabled (no inspection needed).</summary>
    public static ChannelPolicyGuard? ForPolicy(DevicePolicy policy, ILogger logger)
    {
        var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(Dictionary<string, string> src) { foreach (var kv in src) blocked[kv.Key] = kv.Value; }
        if (policy.Clipboard == DevicePolicyMode.Disabled) Add(ClipboardChannels);
        if (policy.Audio == DevicePolicyMode.Disabled) Add(AudioChannels);
        if (policy.Microphone == DevicePolicyMode.Disabled) Add(MicrophoneChannels);
        if (policy.Camera == DevicePolicyMode.Disabled) Add(CameraChannels);
        return blocked.Count == 0 ? null : new ChannelPolicyGuard(blocked, logger);
    }

    /// <summary>Names of the channels this guard blocks (for logging).</summary>
    public IEnumerable<string> BlockedChannels => _blocked.Keys;

    /// <summary>
    /// Inspect a slice of the relayed stream. Returns a violation when the client requested or accepted
    /// a blocked channel; the caller must stop relaying (the offending bytes have not reached the host
    /// when this returns for the client→server direction).
    /// </summary>
    public Violation? Feed(RdpDir dir, ReadOnlySpan<byte> chunk)
    {
        bool c2s = dir == RdpDir.ClientToServer;
        if (c2s ? _c2sDisabled : _s2cDisabled) return null;
        var rx = c2s ? _c2s : _s2c;
        rx.Append(chunk);
        try
        {
            while (true)
            {
                int unitLen = rx.NextUnitLength();
                if (unitLen == 0)
                {
                    if (rx.Length > MaxUnitBuffer) throw new InvalidDataException("no complete PDU within 1 MB");
                    return null;
                }
                if (unitLen < 0) throw new InvalidDataException("invalid PDU framing");
                var unit = rx.Peek(unitLen);
                var v = InspectUnit(c2s, unit);
                rx.Consume(unitLen);
                if (v != null) return v;
            }
        }
        catch (Exception ex)
        {
            // Fail open for this direction: never drop a healthy session over a decoder edge case.
            if (c2s) _c2sDisabled = true; else _s2cDisabled = true;
            rx.Clear();
            _logger.LogWarning(ex, "Device policy guard: stopped inspecting {Dir} after a parse error", dir);
            return null;
        }
    }

    // ---- one complete TPKT / fast-path unit -------------------------------------------------------
    private Violation? InspectUnit(bool c2s, ReadOnlySpan<byte> unit)
    {
        if (unit[0] != 0x03) return null;            // fast-path: input/output updates, never channels
        if (unit.Length < 7) return null;
        int li = unit[4];
        byte code = unit[5];
        if ((code & 0xF0) != 0xF0) return null;      // only X.224 Data TPDUs carry MCS
        int mcsStart = 5 + li;                       // LI counts the bytes after itself (code + EOT)
        if (mcsStart >= unit.Length) return null;
        var mcs = unit.Slice(mcsStart);
        byte b0 = mcs[0];
        if (b0 == 0x7f && mcs.Length > 1)
        {
            if (c2s && mcs[1] == 101) return InspectConnectInitial(mcs);
            if (!c2s && mcs[1] == 102) { LearnConnectResponse(mcs); return null; }
            return null;
        }
        int app = b0 >> 2;
        bool sendData = c2s ? app == 25 /* SendDataRequest */ : app == 26 /* SendDataIndication */;
        if (!sendData || _drdynvcId == 0 || mcs.Length < 7) return null;
        int channelId = (mcs[3] << 8) | mcs[4];
        if (channelId != _drdynvcId) return null;
        int o = 6;                                   // choice(1) initiator(2) channelId(2) priority(1)
        int len = ReadPerLength(mcs, ref o);
        if (len < 0 || o + len > mcs.Length) return null;
        return InspectDrdynvc(c2s, mcs.Slice(o, len));
    }

    // ---- MCS Connect Initial: CS_NET static channel names ----------------------------------------
    private Violation? InspectConnectInitial(ReadOnlySpan<byte> mcs)
    {
        // The GCC ConferenceCreateRequest wraps the client data behind the H.221 key "Duca" followed by a
        // PER length; the TS_UD blocks (type(2) length(2) body) follow. Locating the key avoids walking the
        // BER/PER envelope, which the round-trip decoder does and which is not needed to read four bytes.
        int at = IndexOf(mcs, "Duca"u8);
        if (at < 0) return null;
        int o = at + 4;
        int len = ReadPerLength(mcs, ref o);
        if (len < 0) return null;
        var blocks = mcs.Slice(o, Math.Min(len, mcs.Length - o));
        _clientChannels.Clear();
        int p = 0;
        while (p + 4 <= blocks.Length)
        {
            int type = blocks[p] | (blocks[p + 1] << 8);
            int blen = blocks[p + 2] | (blocks[p + 3] << 8);
            if (blen < 4 || p + blen > blocks.Length) break;
            if (type == 0xC003) // CS_NET
            {
                var body = blocks.Slice(p + 4, blen - 4);
                if (body.Length >= 4)
                {
                    int count = body[0] | (body[1] << 8) | (body[2] << 16) | (body[3] << 24);
                    int q = 4;
                    for (int i = 0; i < count && q + 12 <= body.Length; i++, q += 12)
                    {
                        var nameBytes = body.Slice(q, 8);
                        int end = 0; while (end < 8 && nameBytes[end] != 0) end++;
                        _clientChannels.Add(Encoding.ASCII.GetString(nameBytes.Slice(0, end)));
                    }
                }
            }
            p += blen;
        }
        foreach (var name in _clientChannels)
        {
            if (_blocked.TryGetValue(name, out var feature))
                return Refuse(feature, name, "requested static channel");
        }
        return null;
    }

    // ---- MCS Connect Response: SC_NET ids → find drdynvc -----------------------------------------
    private void LearnConnectResponse(ReadOnlySpan<byte> mcs)
    {
        int at = IndexOf(mcs, "McDn"u8);
        if (at < 0) return;
        int o = at + 4;
        int len = ReadPerLength(mcs, ref o);
        if (len < 0) return;
        var blocks = mcs.Slice(o, Math.Min(len, mcs.Length - o));
        int p = 0;
        while (p + 4 <= blocks.Length)
        {
            int type = blocks[p] | (blocks[p + 1] << 8);
            int blen = blocks[p + 2] | (blocks[p + 3] << 8);
            if (blen < 4 || p + blen > blocks.Length) break;
            if (type == 0x0C03) // SC_NET: ioChannel(2) count(2) ids[count]
            {
                var body = blocks.Slice(p + 4, blen - 4);
                if (body.Length >= 4)
                {
                    int count = body[2] | (body[3] << 8);
                    int q = 4;
                    for (int i = 0; i < count && q + 2 <= body.Length; i++, q += 2)
                    {
                        int id = body[q] | (body[q + 1] << 8);
                        if (i < _clientChannels.Count && _clientChannels[i] == "drdynvc") _drdynvcId = id;
                    }
                }
            }
            p += blen;
        }
    }

    // ---- drdynvc: CHANNEL_PDU_HEADER (+MPPC, FIRST..LAST) then DYNVC commands ----------------------
    private const uint CHANNEL_FLAG_FIRST = 0x00000001;
    private const uint CHANNEL_FLAG_LAST = 0x00000002;
    private const uint CHANNEL_PACKET_COMPRESSED = 0x00200000;
    private const uint CHANNEL_PACKET_AT_FRONT = 0x00400000;
    private const uint CHANNEL_PACKET_FLUSHED = 0x00800000;

    private Violation? InspectDrdynvc(bool c2s, ReadOnlySpan<byte> chan)
    {
        if (chan.Length < 8) return null;
        uint flags = (uint)(chan[4] | (chan[5] << 8) | (chan[6] << 16) | (chan[7] << 24));
        var chunk = chan.Slice(8).ToArray();
        var st = c2s ? _svcC2S : _svcS2C;

        if ((flags & CHANNEL_PACKET_COMPRESSED) != 0)
        {
            int type = (int)((flags & 0x000F0000) >> 16);
            if (type > 1) throw new InvalidDataException("unsupported bulk compression type " + type);
            var mppc = type == 1 ? (st.Mppc64 ??= new Mppc(1)) : (st.Mppc8 ??= new Mppc(0));
            int mflags = Mppc.PACKET_COMPRESSED
                | ((flags & CHANNEL_PACKET_AT_FRONT) != 0 ? Mppc.PACKET_AT_FRONT : 0)
                | ((flags & CHANNEL_PACKET_FLUSHED) != 0 ? Mppc.PACKET_FLUSHED : 0);
            chunk = mppc.Decompress(chunk, mflags) ?? throw new InvalidDataException("MPPC inflate failed");
        }

        bool first = (flags & CHANNEL_FLAG_FIRST) != 0, last = (flags & CHANNEL_FLAG_LAST) != 0;
        byte[]? msg;
        if (first && last) msg = chunk;
        else if (first) { st.Parts = new List<byte>(chunk); msg = null; }
        else if (st.Parts == null) msg = null;
        else
        {
            st.Parts.AddRange(chunk);
            if (st.Parts.Count > MaxSvcMessage) { st.Parts = null; msg = null; }
            else if (last) { msg = st.Parts.ToArray(); st.Parts = null; }
            else msg = null;
        }
        if (msg == null || msg.Length < 2) return null;

        byte hdr = msg[0];
        int cmd = hdr >> 4, cbId = hdr & 0x3;
        if (cmd != 0x01 /* CREATE */) return null;
        int o = 1;
        int channelId;
        switch (cbId)
        {
            case 0: channelId = msg[o]; o += 1; break;
            case 1: if (msg.Length < 3) return null; channelId = msg[o] | (msg[o + 1] << 8); o += 2; break;
            default:
                if (msg.Length < 5) return null;
                channelId = msg[o] | (msg[o + 1] << 8) | (msg[o + 2] << 16) | (msg[o + 3] << 24); o += 4; break;
        }
        if (!c2s)
        {
            // Host → client: DYNVC_CREATE_REQ carries the channel name.
            int end = o; while (end < msg.Length && msg[end] != 0) end++;
            var name = Encoding.ASCII.GetString(msg, o, end - o);
            _dvcNames[channelId] = name;
            return null;
        }
        // Client → host: DYNVC_CREATE_RSP carries the creation status; 0 = the client accepted the channel.
        if (msg.Length < o + 4) return null;
        uint status = (uint)(msg[o] | (msg[o + 1] << 8) | (msg[o + 2] << 16) | (msg[o + 3] << 24));
        if (status != 0) return null;
        if (_dvcNames.TryGetValue(channelId, out var dvcName) && _blocked.TryGetValue(dvcName, out var feature))
            return Refuse(feature, dvcName, "accepted dynamic channel");
        return null;
    }

    private Violation Refuse(string feature, string channel, string how)
    {
        _logger.LogWarning("Device policy guard: client {How} '{Channel}' while {Feature} is disabled by policy — refusing session",
            how, channel, feature);
        return new Violation(feature, channel,
            feature + " redirection is disabled by your administrator's device policy.");
    }

    // ---- helpers ---------------------------------------------------------------------------------
    private static int ReadPerLength(ReadOnlySpan<byte> b, ref int o)
    {
        if (o >= b.Length) return -1;
        int first = b[o];
        if ((first & 0x80) == 0) { o += 1; return first; }
        if (o + 1 >= b.Length) return -1;
        int len = ((first & 0x7f) << 8) | b[o + 1];
        o += 2;
        return len;
    }

    private static int IndexOf(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle) => hay.IndexOf(needle);

    private sealed class SvcState
    {
        public List<byte>? Parts;
        public Mppc? Mppc8, Mppc64;
    }

    // Growable front-consumed byte buffer with TPKT/fast-path framing (same rules as RdpSession).
    private sealed class Reassembler
    {
        private byte[] _buf = new byte[16 * 1024];
        private int _start, _end;
        public int Length => _end - _start;

        public void Append(ReadOnlySpan<byte> chunk)
        {
            if (_start > 0 && _start >= _buf.Length / 2) { Buffer.BlockCopy(_buf, _start, _buf, 0, Length); _end -= _start; _start = 0; }
            if (_end + chunk.Length > _buf.Length)
            {
                int need = Length + chunk.Length;
                var nb = new byte[Math.Max(need, _buf.Length * 2)];
                Buffer.BlockCopy(_buf, _start, nb, 0, Length);
                _end -= _start; _start = 0; _buf = nb;
            }
            chunk.CopyTo(_buf.AsSpan(_end));
            _end += chunk.Length;
        }

        public ReadOnlySpan<byte> Peek(int n) => _buf.AsSpan(_start, n);
        public void Consume(int n) { _start += n; if (_start == _end) { _start = _end = 0; } }
        public void Clear() { _start = _end = 0; }

        /// <summary>0 = need more bytes, -1 = invalid framing, else the unit length.</summary>
        public int NextUnitLength()
        {
            int n = Length;
            if (n < 1) return 0;
            var b = _buf.AsSpan(_start, n);
            if (b[0] == 0x03)
            {
                if (n < 4) return 0;
                int len = (b[2] << 8) | b[3];
                if (len < 4) return -1;
                return n < len ? 0 : len;
            }
            if (n < 2) return 0;
            int len1 = b[1];
            int length, headerLen;
            if ((len1 & 0x80) != 0)
            {
                if (n < 3) return 0;
                length = ((len1 & 0x7f) << 8) | b[2];
                headerLen = 3;
            }
            else { length = len1; headerLen = 2; }
            if (length < headerLen) return -1;
            return n < length ? 0 : length;
        }
    }
}
