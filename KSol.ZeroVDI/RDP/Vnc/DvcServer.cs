namespace KSol.ZeroVDI.RDP.Vnc;

/// <summary>
/// Server side of MS-RDPEDYC dynamic virtual channels, multiplexed over the "drdynvc" static channel.
/// Drives the capability handshake (we advertise v1 — uncompressed DATA/DATA_FIRST both ways, sparing us
/// ZGFX-LITE inflate for client data), opens server-initiated channels by name (Graphics), fragments
/// outbound channel data into chunk-sized drdynvc PDUs, and reassembles inbound ones. Ported from
/// macRDP's DvcServer; the wire structures mirror what the browser client implements as the client half.
///
/// <see cref="SendRaw"/> is given a raw drdynvc payload already wrapped in a CHANNEL_PDU_HEADER; the
/// front-end wraps that in an MCS Send-Data-Indication on the drdynvc channel.
/// </summary>
internal sealed class DvcServer
{
    // drdynvc command codes ([MS-RDPEDYC] 2.2).
    private const byte CMD_CREATE = 0x01;
    private const byte CMD_DATA_FIRST = 0x02;
    private const byte CMD_DATA = 0x03;
    private const byte CMD_CLOSE = 0x04;
    private const byte CMD_CAPABILITIES = 0x05;

    // Max drdynvc PDU payload per chunk: the whole DVC PDU must fit one static-channel chunk
    // (CHANNEL_CHUNK_LENGTH = 1600); 1590 leaves room for the cmd/id/length header (FreeRDP's margin).
    private const int DataChunk = 1590;

    private sealed class Channel
    {
        public uint Id;
        public string Name = "";
        public bool Open;
        public Action<byte[]>? OnData;
        public Action? OnOpen;
        public List<byte> Reasm = new();
        public int ReasmTotal;
    }

    private readonly Action<byte[]> _sendRaw;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, Channel> _channels = new();
    private uint _nextChannelId = 1;
    private bool _capsDone;
    private readonly List<Channel> _pendingCreates = new();
    // Inbound static-channel chunk reassembly (CHANNEL_FLAG_FIRST/LAST).
    private List<byte> _svcReasm = new();
    private bool _svcReasmActive;

    public DvcServer(Action<byte[]> sendRaw, ILogger logger) { _sendRaw = sendRaw; _logger = logger; }

    // ── Outbound ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Kicks off the capability handshake (DYNVC_CAPS v1). Channel creates queue until the
    /// client's capabilities response arrives.</summary>
    public void Start()
    {
        // cmd=CAPABILITIES(5)<<4, Sp=0, cbId=0; Pad; Version(2 LE) = 1.
        Send(new byte[] { 0x50, 0x00, 0x01, 0x00 });
        _logger.LogInformation("drdynvc: capabilities request sent (v1)");
    }

    /// <summary>Opens a server-initiated dynamic channel by name; returns its id. onOpen fires on accept.</summary>
    public uint CreateChannel(string name, Action onOpen, Action<byte[]> onData)
    {
        var ch = new Channel { Id = _nextChannelId++, Name = name, OnOpen = onOpen, OnData = onData };
        _channels[ch.Id] = ch;
        if (_capsDone) SendCreate(ch); else _pendingCreates.Add(ch);
        return ch.Id;
    }

    private void SendCreate(Channel ch)
    {
        var w = new W();
        w.U8((CMD_CREATE << 4) | 0x00);      // cmd, sp=0, cbId=0 (1-byte channel id)
        w.U8((byte)(ch.Id & 0xff));
        w.Bytes(System.Text.Encoding.ASCII.GetBytes(ch.Name));
        w.U8(0);                             // null terminator
        Send(w.ToArray());
        _logger.LogInformation("drdynvc: CREATE '{Name}' id={Id}", ch.Name, ch.Id);
    }

    /// <summary>Sends a complete message on an open dynamic channel, fragmenting into DATA_FIRST + DATA
    /// chunks when it exceeds one drdynvc PDU (mirrors FreeRDP drdynvc_write_data).</summary>
    public void SendData(uint channelId, byte[] payload)
    {
        if (!_channels.TryGetValue(channelId, out var ch) || !ch.Open) return;
        if (payload.Length <= DataChunk)
        {
            var w = new W();
            w.U8((CMD_DATA << 4) | 0x00);
            w.U8((byte)(channelId & 0xff));
            w.Bytes(payload);
            Send(w.ToArray());
            return;
        }
        int off = 0; bool first = true;
        while (off < payload.Length)
        {
            int n = Math.Min(DataChunk, payload.Length - off);
            var w = new W();
            if (first)
            {
                w.U8((CMD_DATA_FIRST << 4) | (2 << 2) | 0x00); // cmd, sp=2 (u32 length), cbId=0
                w.U8((byte)(channelId & 0xff));
                w.U32le((uint)payload.Length);
                first = false;
            }
            else
            {
                w.U8((CMD_DATA << 4) | 0x00);
                w.U8((byte)(channelId & 0xff));
            }
            w.Bytes(payload.AsSpan(off, n));
            Send(w.ToArray());
            off += n;
        }
    }

    /// <summary>Wraps a drdynvc PDU in a CHANNEL_PDU_HEADER (single chunk) and hands it to the transport.</summary>
    private void Send(byte[] pdu)
    {
        var w = new W();
        w.U32le((uint)pdu.Length);           // length (whole message)
        w.U32le(0x00000003);                 // CHANNEL_FLAG_FIRST | CHANNEL_FLAG_LAST
        w.Bytes(pdu);
        _sendRaw(w.ToArray());
    }

    // ── Inbound ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Feeds one static-channel chunk (CHANNEL_PDU_HEADER + data) received on drdynvc.</summary>
    public void HandleChannelChunk(byte[] data)
    {
        if (data.Length < 8) return;
        var r = new Cur(data);
        _ = r.U32le();                       // total message length
        uint flags = r.U32le();
        var chunk = r.Rest().ToArray();
        if ((flags & 0x00200000) != 0)       // CHANNEL_PACKET_COMPRESSED — never advertised
        {
            _logger.LogDebug("drdynvc: dropping compressed chunk ({N}B)", chunk.Length);
            return;
        }
        bool first = (flags & 0x01) != 0;
        bool last = (flags & 0x02) != 0;
        byte[] msg;
        if (first && last) msg = chunk;
        else if (first) { _svcReasm = new List<byte>(chunk); _svcReasmActive = true; return; }
        else
        {
            if (!_svcReasmActive) return;
            _svcReasm.AddRange(chunk);
            if (!last) return;
            msg = _svcReasm.ToArray(); _svcReasm = new List<byte>(); _svcReasmActive = false;
        }
        HandleDvcPdu(msg);
    }

    private void HandleDvcPdu(byte[] pdu)
    {
        if (pdu.Length == 0) return;
        var r = new Cur(pdu);
        byte header = r.U8();
        int cmd = header >> 4;
        int sp = (header >> 2) & 0x3;
        int cbId = header & 0x3;
        switch (cmd)
        {
            case CMD_CAPABILITIES:
                if (r.Remaining < 3) return;
                r.U8();                      // pad
                ushort version = r.U16le();
                _capsDone = true;
                _logger.LogInformation("drdynvc: client capabilities v{V}", version);
                foreach (var ch in _pendingCreates) SendCreate(ch);
                _pendingCreates.Clear();
                break;
            case CMD_CREATE:                 // create RESPONSE (client → server)
            {
                if (!TryReadChannelId(ref r, cbId, out uint id) || r.Remaining < 4) return;
                uint status = r.U32le();
                if (!_channels.TryGetValue(id, out var ch)) return;
                if (status == 0) { ch.Open = true; _logger.LogInformation("drdynvc: '{Name}' id={Id} OPEN", ch.Name, id); ch.OnOpen?.Invoke(); }
                else { _logger.LogWarning("drdynvc: '{Name}' id={Id} REJECTED 0x{S:X}", ch.Name, id, status); _channels.Remove(id); }
                break;
            }
            case CMD_DATA_FIRST:
            {
                if (!TryReadChannelId(ref r, cbId, out uint id) || !_channels.TryGetValue(id, out var ch)) return;
                int total = sp switch
                {
                    0 => r.Remaining >= 1 ? r.U8() : -1,
                    1 => r.Remaining >= 2 ? r.U16le() : -1,
                    _ => r.Remaining >= 4 ? (int)r.U32le() : -1,
                };
                if (total < 0) return;
                var chunk = r.Rest().ToArray();
                if (chunk.Length >= total) ch.OnData?.Invoke(chunk);
                else { ch.Reasm = new List<byte>(chunk); ch.ReasmTotal = total; }
                break;
            }
            case CMD_DATA:
            {
                if (!TryReadChannelId(ref r, cbId, out uint id) || !_channels.TryGetValue(id, out var ch)) return;
                var chunk = r.Rest().ToArray();
                if (ch.ReasmTotal > 0)
                {
                    ch.Reasm.AddRange(chunk);
                    if (ch.Reasm.Count >= ch.ReasmTotal)
                    {
                        var msg = ch.Reasm.ToArray();
                        ch.Reasm = new List<byte>(); ch.ReasmTotal = 0;
                        ch.OnData?.Invoke(msg);
                    }
                }
                else ch.OnData?.Invoke(chunk);
                break;
            }
            case CMD_CLOSE:
                if (TryReadChannelId(ref r, cbId, out uint cid)) _channels.Remove(cid);
                break;
            default:
                _logger.LogDebug("drdynvc: ignoring cmd 0x{C:X}", cmd);
                break;
        }
    }

    private static bool TryReadChannelId(ref Cur r, int cbId, out uint id)
    {
        id = 0;
        switch (cbId)
        {
            case 0: if (r.Remaining < 1) return false; id = r.U8(); return true;
            case 1: if (r.Remaining < 2) return false; id = r.U16le(); return true;
            default: if (r.Remaining < 4) return false; id = r.U32le(); return true;
        }
    }
}
