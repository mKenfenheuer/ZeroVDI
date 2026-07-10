namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// A minimal RDP <b>server</b> state machine that drives the browser console client through the
/// connection sequence and then streams desktop content as fastpath bitmap updates. It runs over the
/// plaintext <see cref="DuplexPipeStream.ServerSide"/>; the gateway already terminated the browser's
/// transport (X.224/TLS/NLA), so the client begins at MCS Connect-Initial and we answer from there.
///
/// The client is the active party (see <c>protocol.js</c>): it sends Connect-Initial, Erect-Domain,
/// Attach-User, the channel joins, Client Info, then waits for our Licensing + Demand Active, replies
/// Confirm Active + finalization, and goes ACTIVE. We only have to answer each step. M1 supports the
/// legacy fastpath BITMAP path (16bpp RGB565); GFX/H.264 comes later.
/// </summary>
internal sealed class RdpServerFrontEnd
{
    private readonly Stream _s;
    private int _width;    // session size — fixed from the browser's Connect-Initial at handshake time
    private int _height;
    private readonly ILogger _logger;

    private int _userId = 1002;              // granted user channel (matches AttachUserConfirm)
    private const int IoChannelId = 1003;    // global I/O channel
    private uint _shareId = 0x000103EA;      // arbitrary but stable server share id
    private int _joinsRemaining;
    private bool _active;
    private List<string> _channelNames = new();

    /// <summary>MCS channel id granted to the client's "drdynvc" static channel, or 0 if not requested.
    /// The GFX pipeline (MS-RDPEGFX) rides dynamic channels multiplexed over this.</summary>
    public int DrdynvcChannelId { get; private set; }

    /// <summary>Raised with the raw CHANNEL_PDU payload received on the drdynvc static channel.</summary>
    public event Action<byte[]>? OnDrdynvcData;

    /// <param name="fallbackWidth">Session size used only if the client's Connect-Initial omits a valid one.</param>
    public RdpServerFrontEnd(Stream serverSide, int fallbackWidth, int fallbackHeight, ILogger logger)
    {
        _s = serverSide;
        _width = fallbackWidth;
        _height = fallbackHeight;
        _logger = logger;
    }

    public bool IsActive => _active;
    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Updates the recorded session size after a live resize. The RDP-level size change itself is driven
    /// by the GFX RESET_GRAPHICS the encoder sends (the client canvas follows that); this just keeps the
    /// front-end's Width/Height consistent for any later bitmap-path sizing.
    /// </summary>
    public void SetSize(int width, int height) { _width = width; _height = height; }
    public int UserId => _userId;
    public int IoChannel => IoChannelId;

    /// <summary>Runs the handshake to ACTIVE. Returns when the client reaches the active phase.</summary>
    public async Task RunHandshakeAsync(CancellationToken ct)
    {
        // 1) Connect-Initial → Connect-Response. The RDP session size is what the BROWSER requests here
        // (its console window / device size); the source desktop is letterbox-scaled into it. Fall back to
        // the ctor size only if the client omits a valid CS_CORE desktop size.
        var connectInitial = await ReadTpktAsync(ct);
        if (TryParseClientDesktopSize(connectInitial, out int reqW, out int reqH))
        {
            _width = reqW; _height = reqH;
            _logger.LogInformation("Bridge/RDP-server: client requested {W}x{H}", _width, _height);
        }
        // Parse the client's requested static virtual channels (CS_NET). We grant them positionally at
        // ids 1003+idx (matching the client's expectation). drdynvc carries the GFX dynamic channels.
        _channelNames = ParseClientChannels(connectInitial);
        var channelIds = new List<int>();
        for (int i = 0; i < _channelNames.Count; i++)
        {
            int id = IoChannelId + 1 + i; // 1003 is the I/O channel; static channels follow
            channelIds.Add(id);
            if (string.Equals(_channelNames[i], "drdynvc", StringComparison.OrdinalIgnoreCase))
                DrdynvcChannelId = id;
        }
        await SendRawAsync(RdpServerEncoders.TpktX224(
            RdpServerEncoders.ConnectResponse(IoChannelId, 0x00000001 /* PROTOCOL_SSL selected */,
                channelIds)), ct);
        _logger.LogInformation("Bridge/RDP-server: sent MCS Connect-Response (channels: {Names}; drdynvc id={Dv})",
            string.Join(",", _channelNames), DrdynvcChannelId);

        // 2) Erect-Domain (drop) then Attach-User-Request → Confirm.
        await ReadMcsDomainPduAsync(ct); // erect domain request
        await ReadMcsDomainPduAsync(ct); // attach user request
        // Attach-User-Confirm / Channel-Join-Confirm are BARE MCS domain PDUs in the X.224 Data payload
        // — NOT wrapped in a Send-Data-Indication (that wrapper is only for share PDUs post-join).
        await SendMcsDomainAsync(RdpServerEncoders.AttachUserConfirm(_userId), ct);
        _logger.LogInformation("Bridge/RDP-server: sent Attach-User-Confirm (user {User})", _userId);

        // 3) Channel joins: client joins [userId, ioChannel, ...staticChannels]. Confirm whatever channel
        // the client actually requests (its exact id) so drdynvc etc. join cleanly.
        _joinsRemaining = 2 + _channelNames.Count;
        while (_joinsRemaining > 0)
        {
            int ch = await ReadChannelJoinRequestAsync(ct);
            await SendMcsDomainAsync(RdpServerEncoders.ChannelJoinConfirm(_userId, ch), ct);
            _joinsRemaining--;
        }
        _logger.LogInformation("Bridge/RDP-server: {N} channels joined", 2 + _channelNames.Count);

        // 4) Client Info PDU (drop — VNC auth happened host-side, credentials irrelevant here).
        await ReadTpktAsync(ct);

        // 5) Licensing valid-client, 6) Demand Active.
        await SendMcsAsync(RdpServerEncoders.LicensingValidClient(), ct);
        await SendMcsAsync(RdpServerEncoders.DemandActive(_shareId, _width, _height), ct);
        _logger.LogInformation("Bridge/RDP-server: sent Licensing + Demand-Active (share {Share:X})", _shareId);

        // 7) Confirm Active (drop), then finalization. The client sends Sync/Control/Control/FontList;
        // we answer Sync/Control(Coop)/Control(Granted)/FontMap. Read the client's confirm+finalization
        // PDUs opportunistically; we don't need their contents.
        await ReadTpktAsync(ct); // Confirm Active

        await SendMcsAsync(RdpServerEncoders.Synchronize(_shareId, _userId), ct);
        await SendMcsAsync(RdpServerEncoders.ControlCooperate(_shareId), ct);
        await SendMcsAsync(RdpServerEncoders.ControlGrantedControl(_shareId, _userId), ct);
        await SendMcsAsync(RdpServerEncoders.FontMap(_shareId), ct);
        _logger.LogInformation("Bridge/RDP-server: sent finalization; session ACTIVE");
        _active = true;
    }

    /// <summary>
    /// Sends bitmap updates covering the given rectangles (16bpp RGB565, bottom-up). A fastpath output
    /// PDU can carry at most ~32KB, so each rect is banded into horizontal strips that fit; every strip
    /// goes out as its own fastpath bitmap update.
    /// </summary>
    public async Task SendBitmapAsync(IReadOnlyList<BitmapRect> rects, CancellationToken ct)
    {
        foreach (var rect in rects)
        {
            foreach (var strip in BandRect(rect))
            {
                var body = RdpServerEncoders.BitmapUpdateData(new[] { strip });
                await SendRawAsync(RdpServerEncoders.FastPathBitmapUpdate(body), ct);
            }
        }
    }

    /// <summary>
    /// Splits a full 16bpp bottom-up rect into horizontal strips each small enough for one fastpath PDU.
    /// Rows are bottom-up in <see cref="BitmapRect.Data"/>, so strip N (from the top) occupies the LAST
    /// rows of the buffer; we carve from the bottom of the source to keep each strip's own rows bottom-up.
    /// </summary>
    private static IEnumerable<BitmapRect> BandRect(BitmapRect rect)
    {
        // The row stride may exceed Width*2 because rows are padded to a 4-byte multiple; derive it from
        // the actual buffer so banding stays row-aligned regardless of padding.
        int stride = rect.Height > 0 ? rect.Data.Length / rect.Height : rect.Width * 2;
        int maxRows = Math.Max(1, (RdpServerEncoders.MaxFastPathBody - 32) / Math.Max(1, stride));
        if (rect.Height <= maxRows) { yield return rect; yield break; }

        // Walk top→bottom in destination space. Source data is bottom-up: the top-most destination row
        // is the LAST row in Data. For a strip covering destination rows [y, y+h), its source rows are
        // Data rows [Height-(y+h), Height-y), copied in the same (bottom-up) order.
        for (int y = 0; y < rect.Height; y += maxRows)
        {
            int h = Math.Min(maxRows, rect.Height - y);
            int srcStartRow = rect.Height - (y + h);   // bottom-up index of this strip's first (bottom) row
            var data = new byte[h * stride];
            Array.Copy(rect.Data, srcStartRow * stride, data, 0, data.Length);
            int top = rect.Top + y;
            // destRight/destBottom inclusive.
            yield return new BitmapRect(rect.Left, top, rect.Left + rect.Width - 1, top + h - 1, rect.Width, h, data);
        }
    }

    // ── input callbacks (raised from RunInputAsync; the encoder maps + forwards to the source) ──
    /// <summary>Browser fastpath MOUSE event: PTRFLAGS + session-space x/y.</summary>
    public event Action<ushort, int, int>? OnMouse;
    /// <summary>Browser fastpath SCANCODE event: PC/AT set-1 keyCode, released, extended (0xE0).</summary>
    public event Action<byte, bool, bool>? OnScancode;
    /// <summary>Browser fastpath UNICODE event: UTF-16 code unit, released.</summary>
    public event Action<ushort, bool>? OnUnicode;

    /// <summary>
    /// Reads client→server traffic after ACTIVE and decodes fastpath INPUT events (mouse/keyboard),
    /// raising the events above. Slow-path (TPKT) control PDUs are consumed and ignored. Ends on EOF.
    /// </summary>
    public async Task RunInputAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadAndDispatchInputAsync(ct)) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    // ── wire helpers ────────────────────────────────────────────────────────────────────────────

    private Task SendRawAsync(byte[] bytes, CancellationToken ct) => _s.WriteAsync(bytes, ct).AsTask();

    /// <summary>Sends a share PDU wrapped in an MCS Send-Data-Indication on the I/O channel.</summary>
    private Task SendMcsAsync(byte[] body, CancellationToken ct)
        => SendRawAsync(RdpServerEncoders.TpktX224(RdpServerEncoders.McsSendDataIndication(
            ServerInitiator, IoChannelId, body)), ct);

    /// <summary>Sends a bare MCS domain PDU (Attach-User/Channel-Join confirm) — no Send-Data wrapper.</summary>
    private Task SendMcsDomainAsync(byte[] mcsPdu, CancellationToken ct)
        => SendRawAsync(RdpServerEncoders.TpktX224(mcsPdu), ct);

    // The server's MCS initiator id when it originates Send-Data-Indications toward the client. The
    // client only reads it positionally; 1002 (our granted user) is conventional.
    private const int ServerInitiator = 1002;

    /// <summary>
    /// Scans a Connect-Initial PDU's bytes for the GCC CS_CORE block (0xC001) and reads the client's
    /// requested desktopWidth/desktopHeight (u16le at block offset +8/+10). Mirrors macRDP's
    /// parseClientDesktopSize. Returns false if not found / out of range.
    /// </summary>
    private static bool TryParseClientDesktopSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        for (int i = 0; i + 12 <= data.Length; i++)
        {
            if (data[i] == 0x01 && data[i + 1] == 0xC0) // CS_CORE header 0xC001 (LE on the wire: 01 C0)
            {
                int w = data[i + 8] | (data[i + 9] << 8);
                int h = data[i + 10] | (data[i + 11] << 8);
                if (w is >= 200 and <= 8192 && h is >= 200 and <= 8192) { width = w; height = h; return true; }
            }
        }
        width = height = 0;
        return false;
    }

    /// <summary>
    /// Scans a Connect-Initial for the GCC CS_NET block (0xC003) and returns the requested static virtual
    /// channel names (8-byte, NUL-padded, then a 4-byte options field each). Mirrors macRDP's
    /// parseConnectInitial.
    /// </summary>
    private static List<string> ParseClientChannels(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 8 <= data.Length; i++)
        {
            if (data[i] == 0x03 && data[i + 1] == 0xC0) // CS_NET header 0xC003
            {
                int count = data[i + 4] | (data[i + 5] << 8) | (data[i + 6] << 16) | (data[i + 7] << 24);
                if (count is > 0 and < 32)
                {
                    var names = new List<string>(count);
                    int o = i + 8;
                    for (int c = 0; c < count && o + 12 <= data.Length; c++)
                    {
                        int end = o;
                        while (end < o + 8 && data[end] != 0) end++;
                        names.Add(System.Text.Encoding.ASCII.GetString(data.Slice(o, end - o)));
                        o += 12; // 8-byte name + 4-byte options
                    }
                    return names;
                }
            }
        }
        return new List<string>();
    }

    /// <summary>
    /// Sends a virtual-channel payload on a static channel, wrapped in a Send-Data-Indication. The caller
    /// supplies the raw channel payload (already carrying its CHANNEL_PDU_HEADER). Used for drdynvc.
    /// </summary>
    public Task SendOnChannelAsync(int channelId, byte[] channelPayload, CancellationToken ct)
        => SendRawAsync(RdpServerEncoders.TpktX224(
            RdpServerEncoders.McsSendDataIndication(ServerInitiator, channelId, channelPayload)), ct);

    /// <summary>
    /// Parses a slow-path X.224 Data payload (LI/0xF0/EOT + MCS Send-Data-Request) and, when it targets the
    /// drdynvc channel, raises <see cref="OnDrdynvcData"/> with the embedded virtual-channel payload.
    /// </summary>
    private void RouteSlowPathChannel(ReadOnlySpan<byte> x224)
    {
        if (DrdynvcChannelId == 0 || OnDrdynvcData == null) return;
        // x224: [0]=LI, [1]=0xF0, [2]=EOT, then MCS Send-Data-Request.
        if (x224.Length < 3 + 6) return;
        var mcs = x224[3..];
        int o = 0;
        int choice = mcs[o++];
        if ((choice >> 2) != 25) return;                 // MCS_SEND_DATA_REQUEST
        o += 2;                                           // initiator (PER integer16)
        int channelId = (mcs[o] << 8) | mcs[o + 1]; o += 2; // channelId (PER integer16, 0-based)
        o += 1;                                           // dataPriority/segmentation byte
        // PER length (1 or 2 bytes).
        int len = mcs[o++];
        if ((len & 0x80) != 0) { len = ((len & 0x7f) << 8) | mcs[o++]; }
        if (channelId != DrdynvcChannelId) return;
        if (o > mcs.Length) return;
        OnDrdynvcData(mcs[o..].ToArray());
    }

    /// <summary>Reads one complete TPKT (slow-path) PDU and returns its X.224 payload (past LI/code/EOT).</summary>
    private async Task<byte[]> ReadTpktAsync(CancellationToken ct)
    {
        var hdr = await RdpHostConnection.ReadExactAsync(_s, 4, ct);
        if (hdr[0] != 0x03) throw new IOException("Bridge/RDP-server: bad TPKT version");
        int len = (hdr[2] << 8) | hdr[3];
        var body = await RdpHostConnection.ReadExactAsync(_s, len - 4, ct);
        return body;
    }

    /// <summary>Reads a TPKT and strips the X.224 Data header (LI, 0xF0, EOT), returning the MCS PDU.</summary>
    private async Task<byte[]> ReadMcsDomainPduAsync(CancellationToken ct)
    {
        var x224 = await ReadTpktAsync(ct);
        // x224: [0]=LI, [1]=0xF0, [2]=EOT, then MCS.
        return x224.Length >= 3 ? x224[3..] : Array.Empty<byte>();
    }

    /// <summary>Reads an MCS Channel-Join-Request and returns the requested channelId.</summary>
    private async Task<int> ReadChannelJoinRequestAsync(CancellationToken ct)
    {
        var mcs = await ReadMcsDomainPduAsync(ct);
        // Channel-Join-Request: choice(1), initiator(2, PER 1001-based), channelId(2, PER 0-based).
        if (mcs.Length < 5) throw new IOException("Bridge/RDP-server: short channel-join request");
        int channelId = (mcs[3] << 8) | mcs[4];
        return channelId;
    }

    /// <summary>Reads either a TPKT slow-path or a fastpath input PDU; returns its raw bytes, or null on EOF.</summary>
    /// <summary>Reads one client PDU; decodes fastpath input events. Returns false on EOF.</summary>
    private async Task<bool> ReadAndDispatchInputAsync(CancellationToken ct)
    {
        var first = new byte[1];
        int n = await _s.ReadAsync(first.AsMemory(0, 1), ct);
        if (n == 0) return false;

        if (first[0] == 0x03)
        {
            // Slow-path TPKT: virtual-channel data (drdynvc → GFX) or control PDUs. Read the whole PDU and
            // route drdynvc channel data to the DVC server; ignore the rest.
            var rest = await RdpHostConnection.ReadExactAsync(_s, 3, ct);
            int tlen = (rest[1] << 8) | rest[2];
            var spBody = tlen > 4 ? await RdpHostConnection.ReadExactAsync(_s, tlen - 4, ct) : Array.Empty<byte>();
            RouteSlowPathChannel(spBody);
            return true;
        }

        // Fastpath INPUT PDU ([MS-RDPBCGR] 2.2.8.1.2): fpInputHeader(1) then length (1 or 2 bytes,
        // PER-style), then the events. The header's bits 2-5 carry numberEvents (0 ⇒ a single event, or
        // an eventHeader-per-event stream). We match the JS client: it sends numEvents=1 per PDU.
        byte fpHeader = first[0];
        int numEvents = (fpHeader >> 2) & 0x0f;
        var lb = await RdpHostConnection.ReadExactAsync(_s, 1, ct);
        int length, consumed;
        if ((lb[0] & 0x80) != 0)
        {
            var lb2 = await RdpHostConnection.ReadExactAsync(_s, 1, ct);
            length = ((lb[0] & 0x7f) << 8) | lb2[0];
            consumed = 3;
        }
        else { length = lb[0]; consumed = 2; }

        int bodyLen = Math.Max(0, length - consumed);
        var body = bodyLen > 0 ? await RdpHostConnection.ReadExactAsync(_s, bodyLen, ct) : Array.Empty<byte>();
        DispatchInputEvents(body, numEvents == 0 ? 1 : numEvents);
        return true;
    }

    /// <summary>Decodes fastpath input events from a PDU body (mirrors macRDP's decodeInputEvent).</summary>
    private void DispatchInputEvents(ReadOnlySpan<byte> body, int events)
    {
        int o = 0;
        for (int e = 0; e < events && o < body.Length; e++)
        {
            byte eventHeader = body[o++];
            int eventCode = (eventHeader >> 5) & 0x7;
            int eventFlags = eventHeader & 0x1f;
            switch (eventCode)
            {
                case 0x1: // MOUSE: flags(u16le) x(u16le) y(u16le)
                    if (o + 6 > body.Length) return;
                    ushort ptrFlags = (ushort)(body[o] | (body[o + 1] << 8));
                    int mx = body[o + 2] | (body[o + 3] << 8);
                    int my = body[o + 4] | (body[o + 5] << 8);
                    o += 6;
                    OnMouse?.Invoke(ptrFlags, mx, my);
                    break;
                case 0x2: // MOUSEX (extended buttons) — same 6-byte layout; ignored for now.
                    if (o + 6 > body.Length) return;
                    o += 6;
                    break;
                case 0x0: // SCANCODE: 1-byte keyCode; flags (release 0x01, extended 0x02) in eventFlags.
                    if (o + 1 > body.Length) return;
                    byte code = body[o++];
                    OnScancode?.Invoke(code, (eventFlags & 0x01) != 0, (eventFlags & 0x02) != 0);
                    break;
                case 0x4: // UNICODE: u16 code unit.
                    if (o + 2 > body.Length) return;
                    ushort ch = (ushort)(body[o] | (body[o + 1] << 8));
                    o += 2;
                    OnUnicode?.Invoke(ch, (eventFlags & 0x01) != 0);
                    break;
                case 0x3: // SYNC (toggle-key state) — nothing to do.
                    break;
                default:
                    o += 6; // unknown; skip a fixed body and hope to resync
                    break;
            }
        }
    }
}
