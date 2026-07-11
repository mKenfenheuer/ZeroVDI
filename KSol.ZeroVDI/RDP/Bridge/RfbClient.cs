using System.Security.Cryptography;
using System.Text;

namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// A minimal RFB (VNC) 3.8 client — just enough for the VNC→RDP bridge: version handshake, VNC
/// Authentication (DES challenge) or None, ClientInit/ServerInit, a fixed 32bpp BGRX pixel format, Raw
/// encoding, and the FramebufferUpdate loop. Runs over whatever <see cref="Stream"/> the transport yields
/// (direct TCP or connector tunnel), so connector-reachable VNC hosts work for free.
///
/// Encodings: Tight (preferred, zlib+JPEG — the bandwidth win on slow links), CopyRect, and Raw fallback,
/// plus the DesktopSize pseudo-encoding for mid-session resizes. Adaptive Tight quality/compression levels
/// are re-advertised on the fly (see <see cref="SetQualityAsync"/>). Pixels are delivered to the caller as
/// top-down 32bpp BGRX rectangles via <see cref="OnRectangle"/>.
/// </summary>
internal sealed class RfbClient : IProtocolSource
{
    private readonly IHostTransport _transport;
    private readonly string _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _password;
    private readonly ILogger _logger;
    private Stream _s = Stream.Null;
    private TightDecoder? _tight;
    // Native-resolution source framebuffer (top-down BGRX). Kept so CopyRect can copy already-received
    // pixels from a source region, and so DesktopSize resizes have a backing buffer. Sized at ServerInit.
    private byte[] _srcFb = Array.Empty<byte>();

    // RFB encoding ids we handle. Tight is the fast path; CopyRect avoids resending moved regions;
    // DesktopSize (a pseudo-encoding) lets the server tell us it resized mid-session.
    private const int EncRaw = 0, EncCopyRect = 1, EncTight = 7;
    private const int EncDesktopSize = -223;
    // Tight quality-level (-23..-32 = quality 0..9) and compression-level (-247..-256 = level 0..9)
    // pseudo-encodings tune the JPEG quality / zlib effort. Advertised order also = preference order.
    private const int EncTightQualityBase = -23;   // quality N → EncTightQualityBase - N
    private const int EncTightCompressBase = -247;  // level N   → EncTightCompressBase - N

    // Current adaptive quality (0=worst/smallest … 9=best/largest) and zlib compression level. Re-advertised
    // via SetEncodings when the backpressure controller changes them, so the server adapts on the next frame.
    private volatile int _jpegQuality = 7;
    private volatile int _compressLevel = 6;

    public int FramebufferWidth { get; private set; }
    public int FramebufferHeight { get; private set; }
    public string DesktopName { get; private set; } = "";

    // IProtocolSource geometry.
    public int Width => FramebufferWidth;
    public int Height => FramebufferHeight;

    /// <summary>Raised for each decoded rectangle: (x, y, w, h, top-down 32bpp BGRX pixels).</summary>
    public event Action<int, int, int, int, byte[]>? OnRectangle;

    /// <summary>Raised when the server resizes via the DesktopSize pseudo-encoding (mid-session resize).</summary>
    public event Action<int, int>? OnGeometryChanged;

    // End-to-end frame-ack gate (see IProtocolSource). RFB is self-clocking — it pulls the next incremental
    // after each update — so we await this before that pull, blocking until the encoder confirms the client
    // acked the previous frame. Null until the encoder sets it (or in unit tests) → no gating.
    public Func<CancellationToken, Task>? BeforeNextFrame { get; set; }

    public RfbClient(IHostTransport transport, string host, int port, string? user, string? password, ILogger logger)
    {
        _transport = transport; _host = host; _port = port; _user = user; _password = password; _logger = logger;
    }

    /// <summary>Connects the transport and completes the RFB handshake (IProtocolSource entry point).</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("VNC: connecting RFB to {Host}:{Port}", _host, _port);
        _s = await _transport.ConnectAsync(_host, _port, ct);
        _logger.LogInformation("VNC: RFB TCP connected to {Host}:{Port}", _host, _port);
        await HandshakeAsync(_user, _password, ct);
    }

    public Task RequestFullFrameAsync(CancellationToken ct) => RequestUpdateAsync(incremental: false, ct);
    public Task RequestIncrementalAsync(CancellationToken ct) => RequestUpdateAsync(incremental: true, ct);

    public ValueTask DisposeAsync()
    {
        try { _s.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs the RFB handshake up to (but not sending) the first FramebufferUpdateRequest.</summary>
    /// <param name="username">Used only by Apple ARD auth (security type 30, macOS Screen Sharing).</param>
    public async Task HandshakeAsync(string? username, string? password, CancellationToken ct)
    {
        // 1) ProtocolVersion: server sends "RFB 003.00x\n". Answer with min(server, 3.8) so a legacy 3.3
        // server uses the 3.3 security flow (single u32 type) and a 3.7/3.8 server uses the type list.
        _logger.LogInformation("RFB: awaiting server ProtocolVersion banner…");
        var serverVersion = await ReadExactAsync(12, ct);
        var vstr = Encoding.ASCII.GetString(serverVersion);
        _logger.LogInformation("RFB: server version '{V}'", vstr.TrimEnd('\n'));
        int serverMinor = ParseMinor(vstr);
        bool legacy33 = serverMinor < 7;
        await WriteAsync(Encoding.ASCII.GetBytes(legacy33 ? "RFB 003.003\n" : "RFB 003.008\n"), ct);

        // 2) Security.
        byte chosen;
        if (legacy33)
        {
            // RFB 3.3: server dictates a single security type as a u32 (0=fail, 1=None, 2=VncAuth).
            uint sec = ReadU32be(await ReadExactAsync(4, ct));
            _logger.LogInformation("RFB: 3.3 server-chosen security type {T}", sec);
            if (sec == 0)
            {
                var reason = await ReadFailureReasonAsync(ct);
                throw new RdpHostConnection.ConnectException($"VNC server rejected connection: {reason}");
            }
            chosen = (byte)sec;
            // In 3.3 the client does NOT echo the type back.
        }
        else
        {
            // RFB 3.7/3.8: server sends count(1) then that many security types; client picks one.
            int count = (await ReadExactAsync(1, ct))[0];
            if (count == 0)
            {
                var reason = await ReadFailureReasonAsync(ct);
                throw new RdpHostConnection.ConnectException($"VNC server rejected connection: {reason}");
            }
            var types = await ReadExactAsync(count, ct);
            _logger.LogInformation("RFB: server offered security types [{Types}]",
                string.Join(",", types.Select(t => (int)t)));
            chosen = PickSecurity(types, password);
            await WriteAsync(new[] { chosen }, ct);
        }
        _logger.LogInformation("RFB: using security type {T}", chosen);

        if (chosen == 2) await VncAuthAsync(password ?? "", ct);
        else if (chosen == 30) await AppleDhAuthAsync(username ?? "", password ?? "", ct);

        // 3) SecurityResult (u32): 0 = OK. In RFB 3.3 with security type None there is NO SecurityResult
        // message; every other case (3.3 VncAuth, all of 3.7/3.8) sends one. Only 3.8 appends a reason
        // string on failure.
        if (!(legacy33 && chosen == 1))
        {
            uint secResult = ReadU32be(await ReadExactAsync(4, ct));
            if (secResult != 0)
            {
                var reason = legacy33 ? "(auth failed)" : await ReadFailureReasonAsync(ct);
                throw new RdpHostConnection.ConnectException($"VNC authentication failed: {reason}");
            }
        }

        // 4) ClientInit (shared=1 so we don't disconnect other viewers).
        await WriteAsync(new byte[] { 1 }, ct);

        // 5) ServerInit: width(2) height(2) pixelFormat(16) nameLength(4) name.
        var si = await ReadExactAsync(24, ct);
        FramebufferWidth = (si[0] << 8) | si[1];
        FramebufferHeight = (si[2] << 8) | si[3];
        int nameLen = (int)ReadU32be(si.AsSpan(20, 4));
        DesktopName = nameLen > 0 ? Encoding.UTF8.GetString(await ReadExactAsync(nameLen, ct)) : "";
        _srcFb = new byte[FramebufferWidth * FramebufferHeight * 4];
        _logger.LogInformation("RFB: ServerInit {W}x{H} '{Name}'", FramebufferWidth, FramebufferHeight, DesktopName);

        // 6) SetPixelFormat → 32bpp little-endian true-colour BGRX (blue in the low byte), so incoming Raw
        // pixels are B,G,R,x per pixel — a straight source for RGB565 conversion.
        await WriteAsync(SetPixelFormatBgrx32(), ct);
        // 7) SetEncodings → prefer Tight (compressed, big win on slow links), then CopyRect, then Raw as the
        // universal fallback, plus the DesktopSize + Tight quality/compression pseudo-encodings. The Tight
        // decoder needs a JPEG decoder for photographic subrects; reuse the SPICE ImageSharp path.
        _tight = new TightDecoder(
            (n, c) => ReadExactAsync(n, c),
            jpeg =>
            {
                var d = Spice.SpiceJpeg.DecodeToBgra(jpeg, null);
                return d ?? (0, 0, Array.Empty<byte>());
            });
        await SendEncodingsAsync(ct);
    }

    // Advertises our encoding preferences plus the current adaptive Tight quality/compression levels. Called
    // at handshake and again whenever the backpressure controller changes quality (the server honours the
    // most recent SetEncodings).
    private Task SendEncodingsAsync(CancellationToken ct)
    {
        int q = Math.Clamp(_jpegQuality, 0, 9);
        int cl = Math.Clamp(_compressLevel, 0, 9);
        return WriteAsync(SetEncodings(new[]
        {
            EncTight, EncCopyRect, EncRaw,
            EncTightQualityBase - q,     // JPEG quality level
            EncTightCompressBase - cl,   // zlib compression level
            EncDesktopSize,
        }), ct);
    }

    /// <summary>
    /// Sets the adaptive Tight quality (0..9) and zlib compression level (0..9) and re-advertises them to
    /// the server. Lower quality / higher compression trade image fidelity for bandwidth on slow links.
    /// Called by the encoder's backpressure controller. No-op before the handshake completes.
    /// </summary>
    public Task SetQualityAsync(int jpegQuality, int compressLevel, CancellationToken ct)
    {
        int q = Math.Clamp(jpegQuality, 0, 9), cl = Math.Clamp(compressLevel, 0, 9);
        if (q == _jpegQuality && cl == _compressLevel) return Task.CompletedTask;
        _jpegQuality = q; _compressLevel = cl;
        _logger.LogDebug("VNC: adaptive quality → JPEG {Q}, zlib {C}", q, cl);
        return SendEncodingsAsync(ct);
    }

    // Congestion tier → Tight knobs. Tier 0 (link healthy): high JPEG quality, moderate zlib effort. As the
    // tier rises we drop JPEG quality hard (biggest bandwidth lever) and lean harder on zlib. This is the
    // host-to-gateway adaptation: we ask the VNC server itself to send us cheaper frames.
    public Task SetQualityTierAsync(int tier, CancellationToken ct) => tier switch
    {
        <= 0 => SetQualityAsync(8, 6, ct),   // best
        1 => SetQualityAsync(6, 7, ct),
        2 => SetQualityAsync(4, 8, ct),
        _ => SetQualityAsync(2, 9, ct),      // worst / smallest
    };

    /// <summary>Requests a framebuffer update over the whole screen. incremental=false forces a full frame.</summary>
    public Task RequestUpdateAsync(bool incremental, CancellationToken ct)
        => RequestUpdateAsync(incremental, 0, 0, FramebufferWidth, FramebufferHeight, ct);

    public Task RequestUpdateAsync(bool incremental, int x, int y, int w, int h, CancellationToken ct)
    {
        var m = new byte[10];
        m[0] = 3;                               // FramebufferUpdateRequest
        m[1] = (byte)(incremental ? 1 : 0);
        m[2] = (byte)(x >> 8); m[3] = (byte)x;
        m[4] = (byte)(y >> 8); m[5] = (byte)y;
        m[6] = (byte)(w >> 8); m[7] = (byte)w;
        m[8] = (byte)(h >> 8); m[9] = (byte)h;
        return WriteAsync(m, ct);
    }

    // ── input (RFB client→server) ─────────────────────────────────────────────────────────────────
    /// <summary>RFB PointerEvent (type 5): buttonMask(1), x(2 BE), y(2 BE).</summary>
    public Task PointerAsync(int x, int y, int buttonMask, CancellationToken ct)
    {
        var m = new byte[6];
        m[0] = 5;
        m[1] = (byte)buttonMask;
        m[2] = (byte)(x >> 8); m[3] = (byte)x;
        m[4] = (byte)(y >> 8); m[5] = (byte)y;
        return WriteAsync(m, ct);
    }

    /// <summary>RFB KeyEvent (type 4): downFlag(1), padding(2), keysym(4 BE).</summary>
    public Task KeyAsync(uint keysym, bool down, CancellationToken ct)
    {
        var m = new byte[8];
        m[0] = 4;
        m[1] = (byte)(down ? 1 : 0);
        m[4] = (byte)(keysym >> 24); m[5] = (byte)(keysym >> 16);
        m[6] = (byte)(keysym >> 8); m[7] = (byte)keysym;
        return WriteAsync(m, ct);
    }

    /// <summary>
    /// Reads and dispatches inbound server messages forever (FramebufferUpdate → <see cref="OnRectangle"/>).
    /// Other server messages (bell, cut-text, colour-map) are consumed and ignored for M2.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int msgType = (await ReadExactAsync(1, ct))[0];
            switch (msgType)
            {
                case 0:
                    await ReadFramebufferUpdateAsync(ct);
                    // End-to-end frame-ack gate: wait until the encoder says the client acknowledged the
                    // previous frame (closed-loop back-pressure) before pulling the next delta from the host.
                    // Unset (tests / no encoder) → no wait.
                    if (BeforeNextFrame is { } gate) await gate(ct);
                    // RFB is request-driven: ask for the next incremental update so changed regions keep
                    // flowing. The server replies only when something changes, so this self-clocks the
                    // live stream without polling. Coalescing (one outstanding request) avoids flooding.
                    await RequestUpdateAsync(incremental: true, ct);
                    break;
                case 1: await ReadColourMapAsync(ct); break;
                case 2: break; // Bell — no payload.
                case 3: await ReadServerCutTextAsync(ct); break;
                default:
                    throw new IOException($"RFB: unknown server message type {msgType}");
            }
        }
    }

    private async Task ReadFramebufferUpdateAsync(CancellationToken ct)
    {
        var hdr = await ReadExactAsync(3, ct);           // padding(1) + numberOfRectangles(2)
        int rects = (hdr[1] << 8) | hdr[2];
        for (int i = 0; i < rects; i++)
        {
            var rh = await ReadExactAsync(12, ct);       // x(2) y(2) w(2) h(2) encoding(4)
            int x = (rh[0] << 8) | rh[1];
            int y = (rh[2] << 8) | rh[3];
            int w = (rh[4] << 8) | rh[5];
            int h = (rh[6] << 8) | rh[7];
            int enc = (int)ReadU32be(rh.AsSpan(8, 4));
            switch (enc)
            {
                case EncRaw:
                {
                    // Raw: w*h pixels, 4 bytes each (our SetPixelFormat), top-down.
                    var pixels = await ReadExactAsync(w * h * 4, ct);
                    StoreAndEmit(x, y, w, h, pixels);
                    break;
                }
                case EncTight:
                {
                    var pixels = await _tight!.DecodeRectAsync(w, h, ct);
                    StoreAndEmit(x, y, w, h, pixels);
                    break;
                }
                case EncCopyRect:
                {
                    // CopyRect: srcX(2) srcY(2) — copy an already-received region to (x,y). Serve it from
                    // our source framebuffer and re-emit the destination box.
                    var sp = await ReadExactAsync(4, ct);
                    int srcX = (sp[0] << 8) | sp[1], srcY = (sp[2] << 8) | sp[3];
                    var pixels = CopyRegion(srcX, srcY, w, h);
                    StoreAndEmit(x, y, w, h, pixels);
                    break;
                }
                case EncDesktopSize:
                {
                    // DesktopSize pseudo-encoding: the (w,h) in the rect header is the NEW framebuffer size.
                    ResizeFramebuffer(w, h);
                    break;
                }
                default:
                    throw new IOException($"RFB: unsupported encoding {enc}");
            }
        }
    }

    // Writes a decoded rect into the source framebuffer (for later CopyRect) and emits it downstream.
    private void StoreAndEmit(int x, int y, int w, int h, byte[] pixels)
    {
        if (w <= 0 || h <= 0) return;
        int fbw = FramebufferWidth;
        if (_srcFb.Length == fbw * FramebufferHeight * 4)
        {
            for (int row = 0; row < h; row++)
            {
                int dy = y + row;
                if (dy < 0 || dy >= FramebufferHeight) continue;
                int copyW = Math.Min(w, fbw - x);
                if (copyW <= 0) continue;
                Buffer.BlockCopy(pixels, row * w * 4, _srcFb, (dy * fbw + x) * 4, copyW * 4);
            }
        }
        OnRectangle?.Invoke(x, y, w, h, pixels);
    }

    // Extracts a w×h top-down BGRX region from the source framebuffer (for CopyRect).
    private byte[] CopyRegion(int srcX, int srcY, int w, int h)
    {
        var outb = new byte[w * h * 4];
        int fbw = FramebufferWidth;
        if (_srcFb.Length != fbw * FramebufferHeight * 4) return outb;
        for (int row = 0; row < h; row++)
        {
            int sy = srcY + row;
            if (sy < 0 || sy >= FramebufferHeight) continue;
            int copyW = Math.Min(w, fbw - srcX);
            if (copyW <= 0) continue;
            Buffer.BlockCopy(_srcFb, (sy * fbw + srcX) * 4, outb, row * w * 4, copyW * 4);
        }
        return outb;
    }

    private void ResizeFramebuffer(int w, int h)
    {
        if (w <= 0 || h <= 0 || (w == FramebufferWidth && h == FramebufferHeight)) return;
        _logger.LogInformation("RFB: DesktopSize → {W}x{H}", w, h);
        FramebufferWidth = w; FramebufferHeight = h;
        _srcFb = new byte[w * h * 4];
        OnGeometryChanged?.Invoke(w, h);
    }

    private async Task ReadColourMapAsync(CancellationToken ct)
    {
        var h = await ReadExactAsync(5, ct);             // padding(1) firstColour(2) numColours(2)
        int n = (h[3] << 8) | h[4];
        if (n > 0) await ReadExactAsync(n * 6, ct);      // n * (R,G,B) u16 each
    }

    private async Task ReadServerCutTextAsync(CancellationToken ct)
    {
        var h = await ReadExactAsync(7, ct);             // padding(3) length(4)
        int len = (int)ReadU32be(h.AsSpan(3, 4));
        if (len > 0) await ReadExactAsync(len, ct);
    }

    // ── security ────────────────────────────────────────────────────────────────────────────────
    private static byte PickSecurity(ReadOnlySpan<byte> types, string? password)
    {
        bool hasVnc = false, hasNone = false, hasAppleDh = false;
        foreach (var t in types)
        {
            if (t == 2) hasVnc = true;
            if (t == 1) hasNone = true;
            if (t == 30) hasAppleDh = true;   // Apple ARD / macOS Screen Sharing (DH + AES-128)
        }
        // Prefer Apple DH when offered (macOS hosts only offer Apple types + expect credentials).
        if (hasAppleDh) return 30;
        if (hasVnc && !string.IsNullOrEmpty(password)) return 2;
        if (hasNone) return 1;
        if (hasVnc) return 2; // try VNC auth even with empty password (some servers accept it)
        throw new RdpHostConnection.ConnectException("VNC server offers no supported security type");
    }

    /// <summary>VNC Authentication: server sends a 16-byte challenge; we return DES(challenge) keyed by
    /// the (bit-reversed) password, per the classic VNC quirk.</summary>
    private async Task VncAuthAsync(string password, CancellationToken ct)
    {
        var challenge = await ReadExactAsync(16, ct);
        var key = VncDesKey(password);
        var response = new byte[16];
        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key;
        using var enc = des.CreateEncryptor();
        enc.TransformBlock(challenge, 0, 8, response, 0);
        enc.TransformBlock(challenge, 8, 8, response, 8);
        await WriteAsync(response, ct);
    }

    /// <summary>
    /// Apple Remote Desktop / macOS Screen Sharing authentication (security type 30). The server sends a
    /// Diffie-Hellman group + its public key; we compute the shared secret, take MD5 of it as an AES-128
    /// key, AES-ECB-encrypt a 128-byte [username(64) || password(64)] blob, and return that plus our DH
    /// public key. Wire format ([RFB] Apple extension):
    ///   server → generator(2) keyLen(2) prime(keyLen) serverPub(keyLen)
    ///   client → cipherText(128) clientPub(keyLen)
    /// </summary>
    private async Task AppleDhAuthAsync(string username, string password, CancellationToken ct)
    {
        var hdr = await ReadExactAsync(4, ct);
        int generator = (hdr[0] << 8) | hdr[1];
        int keyLen = (hdr[2] << 8) | hdr[3];
        var primeBytes = await ReadExactAsync(keyLen, ct);
        var serverPubBytes = await ReadExactAsync(keyLen, ct);
        _logger.LogInformation("RFB: Apple DH auth (generator={G}, keyLen={K})", generator, keyLen);

        var prime = ToPositive(primeBytes);
        var serverPub = ToPositive(serverPubBytes);
        var g = new System.Numerics.BigInteger(generator);

        // Our private exponent: keyLen random bytes, reduced mod prime.
        var privBytes = new byte[keyLen];
        RandomNumberGenerator.Fill(privBytes);
        var priv = ToPositive(privBytes) % prime;

        var clientPub = System.Numerics.BigInteger.ModPow(g, priv, prime);
        var shared = System.Numerics.BigInteger.ModPow(serverPub, priv, prime);

        // AES-128 key = MD5(sharedSecret), where the shared secret is keyLen bytes big-endian.
        var sharedFixed = ToFixedBigEndian(shared, keyLen);
        byte[] aesKey = MD5.HashData(sharedFixed);

        // Credentials blob: 64 bytes username (NUL-terminated) + 64 bytes password (NUL-terminated),
        // remaining bytes random. Total 128 bytes.
        var creds = new byte[128];
        RandomNumberGenerator.Fill(creds);
        WriteCString(creds, 0, 64, username);
        WriteCString(creds, 64, 64, password);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = aesKey;
        byte[] cipher = aes.EncryptEcb(creds, PaddingMode.None);

        var clientPubFixed = ToFixedBigEndian(clientPub, keyLen);
        var response = new byte[cipher.Length + clientPubFixed.Length];
        Buffer.BlockCopy(cipher, 0, response, 0, cipher.Length);
        Buffer.BlockCopy(clientPubFixed, 0, response, cipher.Length, clientPubFixed.Length);
        await WriteAsync(response, ct);
    }

    // Big-endian bytes → non-negative BigInteger (BigInteger ctor is little-endian + signed).
    private static System.Numerics.BigInteger ToPositive(byte[] bigEndian)
    {
        var le = new byte[bigEndian.Length + 1];   // extra 0 byte keeps it positive
        for (int i = 0; i < bigEndian.Length; i++) le[i] = bigEndian[bigEndian.Length - 1 - i];
        return new System.Numerics.BigInteger(le);
    }

    // BigInteger → fixed-length big-endian, left-padded/truncated to `len`.
    private static byte[] ToFixedBigEndian(System.Numerics.BigInteger v, int len)
    {
        var le = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        var outb = new byte[len];
        for (int i = 0; i < len && i < le.Length; i++) outb[len - 1 - i] = le[i];
        return outb;
    }

    private static void WriteCString(byte[] dst, int offset, int field, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(b.Length, field - 1);       // leave room for the NUL terminator
        Array.Copy(b, 0, dst, offset, n);
        dst[offset + n] = 0;                          // NUL-terminate (overwrites one random byte)
    }

    /// <summary>Builds the DES key from the VNC password: first 8 chars, each byte bit-reversed.</summary>
    private static byte[] VncDesKey(string password)
    {
        var key = new byte[8];
        var pw = Encoding.ASCII.GetBytes(password);
        for (int i = 0; i < 8; i++)
        {
            byte b = i < pw.Length ? pw[i] : (byte)0;
            // Reverse the bit order within the byte (the VNC DES key quirk).
            byte r = 0;
            for (int bit = 0; bit < 8; bit++) if ((b & (1 << bit)) != 0) r |= (byte)(1 << (7 - bit));
            key[i] = r;
        }
        return key;
    }

    // ── message builders ──────────────────────────────────────────────────────────────────────────
    private static byte[] SetPixelFormatBgrx32()
    {
        // SetPixelFormat (type 0) + 3 padding + 16-byte PIXEL_FORMAT.
        var m = new byte[20];
        m[0] = 0;
        // PIXEL_FORMAT at offset 4: bpp, depth, bigEndian, trueColour, redMax(2), greenMax(2), blueMax(2),
        // redShift, greenShift, blueShift, pad(3).
        m[4] = 32;      // bitsPerPixel
        m[5] = 24;      // depth
        m[6] = 0;       // bigEndianFlag = little-endian
        m[7] = 1;       // trueColourFlag
        m[8] = 0; m[9] = 255;   // redMax = 255
        m[10] = 0; m[11] = 255; // greenMax = 255
        m[12] = 0; m[13] = 255; // blueMax = 255
        // Little-endian byte order B,G,R,x → blue at shift 0, green 8, red 16.
        m[14] = 16;     // redShift
        m[15] = 8;      // greenShift
        m[16] = 0;      // blueShift
        return m;
    }

    private static byte[] SetEncodings(int[] encodings)
    {
        var m = new byte[4 + encodings.Length * 4];
        m[0] = 2;                               // SetEncodings
        m[2] = (byte)(encodings.Length >> 8); m[3] = (byte)encodings.Length;
        int o = 4;
        foreach (var e in encodings)
        {
            m[o++] = (byte)(e >> 24); m[o++] = (byte)(e >> 16); m[o++] = (byte)(e >> 8); m[o++] = (byte)e;
        }
        return m;
    }

    // ── stream helpers ────────────────────────────────────────────────────────────────────────────
    private Task<byte[]> ReadExactAsync(int n, CancellationToken ct) => RdpHostConnection.ReadExactAsync(_s, n, ct);
    // Serialize all client→server writes: the frame loop (incremental requests) and the input path
    // (pointer/key events) both write to the same socket from different tasks.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await _s.WriteAsync(bytes, ct); }
        finally { _writeLock.Release(); }
    }

    private async Task<string> ReadFailureReasonAsync(CancellationToken ct)
    {
        try
        {
            int len = (int)ReadU32be(await ReadExactAsync(4, ct));
            return len > 0 ? Encoding.UTF8.GetString(await ReadExactAsync(len, ct)) : "(no reason)";
        }
        catch { return "(unreadable reason)"; }
    }

    private static uint ReadU32be(ReadOnlySpan<byte> b) => (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);

    /// <summary>Parses the minor version from an "RFB 003.008\n" banner; defaults to 3 (most conservative).</summary>
    private static int ParseMinor(string banner)
    {
        // Format: "RFB xxx.yyy\n" — minor is the 3 digits after the dot (offset 8..10).
        if (banner.Length >= 11 && banner[7] == '.' && int.TryParse(banner.Substring(8, 3), out int minor))
            return minor;
        return 3;
    }
}
