using System.Security.Cryptography;
using System.Text;

namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// One SPICE channel — its own TCP connection to the host. Handles the shared link handshake
/// (SpiceLinkHeader/Mess → SpiceLinkReply → RSA-OAEP ticket → SpiceLinkAuthReply), then a
/// mini-header (<c>SpiceMiniData</c>) message loop with the common SET_ACK/PING/NOTIFY handling.
/// Channel-specific messages are dispatched to <see cref="HandleMessageAsync"/> in derived channels
/// (main/display/inputs).
///
/// SPICE opens a separate connection per channel; the shared session id (from MAIN_INIT) ties them to
/// the same server session. The bridge runs over whatever <see cref="Stream"/> the transport yields
/// (direct TCP or connector tunnel), so connector-reachable SPICE hosts work for free — exactly like
/// the VNC bridge.
/// </summary>
internal abstract class SpiceChannel : IAsyncDisposable
{
    private readonly IHostTransport _transport;
    private readonly string _host;
    private readonly int _port;
    private string? _password;
    protected readonly ILogger Logger;

    private Stream _s = Stream.Null;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    protected uint ConnectionId;
    public byte ChannelType { get; }
    public byte ChannelId { get; }

    // Ack accounting: after SET_ACK the server expects an ACK every `window` messages.
    private uint _ackWindow;
    private uint _msgsUntilAck;

    // Optional per-connection password source: for the Proxmox spiceproxy path each channel rides its own
    // freshly-fetched ticket, so the SPICE auth password must be read AFTER the tunnel is dialed (the
    // transport exposes the ticket it just used). Null → use the static _password (direct SPICE).
    private readonly Func<string?>? _passwordProvider;

    protected SpiceChannel(IHostTransport transport, string host, int port, string? password,
        byte channelType, byte channelId, uint connectionId, ILogger logger, Func<string?>? passwordProvider = null)
    {
        _transport = transport; _host = host; _port = port; _password = password;
        _passwordProvider = passwordProvider;
        ChannelType = channelType; ChannelId = channelId; ConnectionId = connectionId; Logger = logger;
    }

    /// <summary>Opens the TCP connection and completes the SPICE link handshake + ticket auth.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _s = await _transport.ConnectAsync(_host, _port, ct);
        // For spiceproxy, the transport fetched this channel's ticket while dialing; adopt its password.
        if (_passwordProvider != null) _password = _passwordProvider() ?? _password;
        await LinkAsync(ct);
    }

    // ── link handshake ────────────────────────────────────────────────────────────────────────────

    private async Task LinkAsync(CancellationToken ct)
    {
        // SpiceLinkMess body: connection_id u32, channel_type u8, channel_id u8, num_common_caps u32,
        // num_channel_caps u32, caps_offset u32, then the caps u32[]. caps_offset is measured from the
        // start of SpiceLinkMess (i.e. 18 = the fixed header size before the caps arrays).
        uint commonCaps = (1u << SpiceConst.CAP_PROTOCOL_AUTH_SELECTION) | (1u << SpiceConst.CAP_MINI_HEADER);
        uint channelCaps = ChannelCaps();
        int numCommon = 1;
        int numChannel = channelCaps != 0 ? 1 : 0;

        var mess = new SpiceWriter();
        mess.U32(ConnectionId).U8(ChannelType).U8(ChannelId)
            .U32(numCommon).U32(numChannel).U32(18);
        mess.U32(commonCaps);
        if (numChannel > 0) mess.U32(channelCaps);
        byte[] messBytes = mess.ToArray();

        // SpiceLinkHeader: magic "REDQ", major u32, minor u32, size u32 (= link message size).
        var hdr = new SpiceWriter();
        hdr.Bytes(SpiceConst.Magic).U32(SpiceConst.VersionMajor).U32(SpiceConst.VersionMinor).U32(messBytes.Length);

        await WriteRawAsync(hdr.ToArray(), ct);
        await WriteRawAsync(messBytes, ct);

        // Read reply header (16 bytes): magic, major, minor, size.
        var rhdr = await ReadExactAsync(16, ct);
        var rr = new SpiceReader(rhdr);
        if (rr.U8() != 'R' || rr.U8() != 'E' || rr.U8() != 'D' || rr.U8() != 'Q')
            throw new RdpHostConnection.ConnectException("SPICE: bad magic in link reply");
        rr.Skip(8); // major, minor
        uint replySize = rr.U32();

        // SpiceLinkReply body: error u32, pub_key[162], num_common_caps u32, num_channel_caps u32,
        // caps_offset u32, caps...
        var reply = await ReadExactAsync((int)replySize, ct);
        var pr = new SpiceReader(reply);
        uint error = pr.U32();
        if (error != SpiceConst.LINK_ERR_OK)
            throw new RdpHostConnection.ConnectException($"SPICE: link error {error}");
        byte[] pubKeyDer = pr.Bytes(SpiceConst.TicketPubkeyBytes);

        // Build and send the encrypted ticket: auth_mechanism u32 + RSA/OAEP(password + '\0').
        byte[] encrypted = EncryptTicket(pubKeyDer, _password ?? "");
        var ticket = new SpiceWriter();
        ticket.U32(SpiceConst.AUTH_SPICE);
        // The wire field is a fixed 128 bytes; RSA-1024/OAEP output is exactly 128.
        ticket.Bytes(encrypted);
        if (encrypted.Length < SpiceConst.TicketKeyBytes)
            ticket.Zeros(SpiceConst.TicketKeyBytes - encrypted.Length);
        await WriteRawAsync(ticket.ToArray(), ct);

        // SpiceLinkAuthReply: auth_code u32.
        var auth = await ReadExactAsync(4, ct);
        uint authCode = new SpiceReader(auth).U32();
        if (authCode != SpiceConst.LINK_ERR_OK)
        {
            if (authCode == SpiceConst.LINK_ERR_PERMISSION_DENIED)
                throw new RdpHostConnection.ConnectException("SPICE: permission denied (bad password)");
            throw new RdpHostConnection.ConnectException($"SPICE: auth error {authCode}");
        }
        Logger.LogInformation("SPICE: channel type {Type}/{Id} linked", ChannelType, ChannelId);
    }

    /// <summary>Channel-specific capability bits advertised in the link message (0 = none).</summary>
    protected virtual uint ChannelCaps() => 0;

    private static byte[] EncryptTicket(byte[] subjectPublicKeyInfoDer, string password)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfoDer, out _);
        // SPICE encrypts the NUL-terminated password with RSA/OAEP (SHA-1, MGF1-SHA1).
        byte[] plain = Encoding.ASCII.GetBytes(password + "\0");
        return rsa.Encrypt(plain, RSAEncryptionPadding.OaepSHA1);
    }

    // ── message loop ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads mini-header messages until cancelled, dispatching common messages here and channel-specific
    /// ones to <see cref="HandleMessageAsync"/>. Called after the channel has done any post-link setup.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // SpiceMiniData header: type u16, size u32.
            var head = await ReadExactAsync(6, ct);
            var hr = new SpiceReader(head);
            int type = hr.U16();
            int size = (int)hr.U32();
            byte[] data = size > 0 ? await ReadExactAsync(size, ct) : Array.Empty<byte>();

            if (!await HandleCommonAsync(type, data, ct))
                await HandleMessageAsync(type, data, ct);

            // Ack accounting (after SET_ACK arms it).
            if (_ackWindow != 0)
            {
                if (--_msgsUntilAck == 0)
                {
                    _msgsUntilAck = _ackWindow;
                    await SendAsync(SpiceConst.MSGC_ACK, Array.Empty<byte>(), ct);
                }
            }
        }
    }

    private async Task<bool> HandleCommonAsync(int type, byte[] data, CancellationToken ct)
    {
        switch (type)
        {
            case SpiceConst.MSG_SET_ACK:
            {
                // SpiceMsgSetAck: generation u32, window u32.
                var r = new SpiceReader(data);
                r.U32(); // generation
                _ackWindow = r.U32();
                _msgsUntilAck = _ackWindow;
                // Reply ACK_SYNC echoing the generation.
                var w = new SpiceWriter().U32(new SpiceReader(data).U32());
                await SendAsync(SpiceConst.MSGC_ACK_SYNC, w.ToArray(), ct);
                return true;
            }
            case SpiceConst.MSG_PING:
            {
                // Echo the first 12 bytes (id u32 + timestamp u64) back as PONG.
                int n = Math.Min(12, data.Length);
                await SendAsync(SpiceConst.MSGC_PONG, data.AsSpan(0, n).ToArray(), ct);
                return true;
            }
            case SpiceConst.MSG_NOTIFY:
                return true; // ignore server notifications
            case SpiceConst.MSG_DISCONNECTING:
                Logger.LogInformation("SPICE: server disconnecting on channel {Type}", ChannelType);
                return true;
            case SpiceConst.MSG_MIGRATE:
                return true; // migration not supported; ignore
            default:
                return false;
        }
    }

    /// <summary>Dispatches a channel-specific message. Return quietly if unhandled.</summary>
    protected abstract Task HandleMessageAsync(int type, byte[] data, CancellationToken ct);

    // ── send / raw IO ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Sends a mini-header message (type u16, size u32, body) on this channel.</summary>
    protected async Task SendAsync(int type, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var w = new SpiceWriter().U16(type).U32(body.Length);
        byte[] head = w.ToArray();
        await _writeLock.WaitAsync(ct);
        try
        {
            await _s.WriteAsync(head, ct);
            if (!body.IsEmpty) await _s.WriteAsync(body, ct);
            await _s.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    private async Task WriteRawAsync(byte[] bytes, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await _s.WriteAsync(bytes, ct); await _s.FlushAsync(ct); }
        finally { _writeLock.Release(); }
    }

    private async Task<byte[]> ReadExactAsync(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = await _s.ReadAsync(buf.AsMemory(off, n - off), ct);
            if (r == 0) throw new EndOfStreamException("SPICE: connection closed");
            off += r;
        }
        return buf;
    }

    public ValueTask DisposeAsync()
    {
        try { _s.Dispose(); } catch { }
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
