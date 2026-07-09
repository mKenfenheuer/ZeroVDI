namespace KSol.ZeroVDI.RDP.Vnc;

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
            _logger.LogInformation("VNC/RDP-server: client requested {W}x{H}", _width, _height);
        }
        await SendRawAsync(RdpServerEncoders.TpktX224(
            RdpServerEncoders.ConnectResponse(IoChannelId, 0x00000001 /* PROTOCOL_SSL selected */,
                Array.Empty<int>())), ct);
        _logger.LogInformation("VNC/RDP-server: sent MCS Connect-Response");

        // 2) Erect-Domain (drop) then Attach-User-Request → Confirm.
        await ReadMcsDomainPduAsync(ct); // erect domain request
        await ReadMcsDomainPduAsync(ct); // attach user request
        // Attach-User-Confirm / Channel-Join-Confirm are BARE MCS domain PDUs in the X.224 Data payload
        // — NOT wrapped in a Send-Data-Indication (that wrapper is only for share PDUs post-join).
        await SendMcsDomainAsync(RdpServerEncoders.AttachUserConfirm(_userId), ct);
        _logger.LogInformation("VNC/RDP-server: sent Attach-User-Confirm (user {User})", _userId);

        // 3) Channel joins: client joins [userId, ioChannel] (no static channels in MVP) — 2 joins.
        _joinsRemaining = 2;
        while (_joinsRemaining > 0)
        {
            int ch = await ReadChannelJoinRequestAsync(ct);
            await SendMcsDomainAsync(RdpServerEncoders.ChannelJoinConfirm(_userId, ch), ct);
            _joinsRemaining--;
        }
        _logger.LogInformation("VNC/RDP-server: channels joined");

        // 4) Client Info PDU (drop — VNC auth happened host-side, credentials irrelevant here).
        await ReadTpktAsync(ct);

        // 5) Licensing valid-client, 6) Demand Active.
        await SendMcsAsync(RdpServerEncoders.LicensingValidClient(), ct);
        await SendMcsAsync(RdpServerEncoders.DemandActive(_shareId, _width, _height), ct);
        _logger.LogInformation("VNC/RDP-server: sent Licensing + Demand-Active (share {Share:X})", _shareId);

        // 7) Confirm Active (drop), then finalization. The client sends Sync/Control/Control/FontList;
        // we answer Sync/Control(Coop)/Control(Granted)/FontMap. Read the client's confirm+finalization
        // PDUs opportunistically; we don't need their contents.
        await ReadTpktAsync(ct); // Confirm Active

        await SendMcsAsync(RdpServerEncoders.Synchronize(_shareId, _userId), ct);
        await SendMcsAsync(RdpServerEncoders.ControlCooperate(_shareId), ct);
        await SendMcsAsync(RdpServerEncoders.ControlGrantedControl(_shareId, _userId), ct);
        await SendMcsAsync(RdpServerEncoders.FontMap(_shareId), ct);
        _logger.LogInformation("VNC/RDP-server: sent finalization; session ACTIVE");
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

    /// <summary>Drains client→server traffic after ACTIVE so the pipe never back-pressures. M3 will
    /// parse input events out of this stream; for now it is discarded.</summary>
    public async Task DrainClientAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pdu = await ReadTpktOrFastpathAsync(ct);
                if (pdu == null) break;
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

    /// <summary>Reads one complete TPKT (slow-path) PDU and returns its X.224 payload (past LI/code/EOT).</summary>
    private async Task<byte[]> ReadTpktAsync(CancellationToken ct)
    {
        var hdr = await RdpHostConnection.ReadExactAsync(_s, 4, ct);
        if (hdr[0] != 0x03) throw new IOException("VNC/RDP-server: bad TPKT version");
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
        if (mcs.Length < 5) throw new IOException("VNC/RDP-server: short channel-join request");
        int channelId = (mcs[3] << 8) | mcs[4];
        return channelId;
    }

    /// <summary>Reads either a TPKT slow-path or a fastpath input PDU; returns its raw bytes, or null on EOF.</summary>
    private async Task<byte[]?> ReadTpktOrFastpathAsync(CancellationToken ct)
    {
        var first = new byte[1];
        int n = await _s.ReadAsync(first.AsMemory(0, 1), ct);
        if (n == 0) return null;
        if (first[0] == 0x03)
        {
            var rest = await RdpHostConnection.ReadExactAsync(_s, 3, ct);
            int len = (rest[1] << 8) | rest[2];
            var body = await RdpHostConnection.ReadExactAsync(_s, len - 4, ct);
            return body;
        }
        // Fastpath input: header(1) then length (1 or 2 bytes, PER-style).
        var lb = await RdpHostConnection.ReadExactAsync(_s, 1, ct);
        int length; int consumed;
        if ((lb[0] & 0x80) != 0)
        {
            var lb2 = await RdpHostConnection.ReadExactAsync(_s, 1, ct);
            length = ((lb[0] & 0x7f) << 8) | lb2[0];
            consumed = 3;
        }
        else { length = lb[0]; consumed = 2; }
        if (length > consumed)
            await RdpHostConnection.ReadExactAsync(_s, length - consumed, ct);
        return Array.Empty<byte>();
    }
}
