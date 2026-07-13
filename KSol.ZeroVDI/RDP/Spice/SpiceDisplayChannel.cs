namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// The SPICE display channel (type 2). SPICE is a drawing-command protocol: the server sends
/// SURFACE_CREATE (the primary surface = the screen) then DRAW_* ops (COPY, FILL, …) carrying images in
/// various codecs. The bridge keeps a primary-surface BGRX framebuffer, composites each draw into it, and
/// emits the changed box as a rectangle via <see cref="OnRectangle"/> — which the shared RDP encoder turns
/// into RDP output for the browser. This mirrors how <see cref="Vnc.RfbClient"/> emits rectangles.
///
/// Codecs handled here (M3): raw BITMAP (32bpp), LZ-RGB, solid FILL, and the image cache. QUIC and JPEG
/// draws are logged and skipped until their decoders land (M4/M5).
/// </summary>
internal sealed class SpiceDisplayChannel : SpiceChannel
{
    // Primary surface (id + geometry + BGRX pixels). SPICE surface ids can be non-zero; the primary is
    // flagged SURFACE_FLAGS_PRIMARY. We track it and ignore off-screen surfaces for now.
    private int _primaryId = -1;
    private int _width, _height;
    private byte[] _fb = Array.Empty<byte>();   // width*height*4 BGRX, top-down

    // Image cache: SPICE tags images with CACHE_ME so later FROM_CACHE draws reuse them. Keyed by the
    // 64-bit descriptor id; value is a decoded (w,h,BGRA) image.
    private readonly Dictionary<ulong, (int w, int h, byte[] bgra)> _cache = new();

    // Draw ops (QUIC/JPEG/LZ decode + composite) are CPU-heavy; running them inline on the channel read
    // loop makes us fall progressively behind the guest's stream. Instead the read loop just enqueues draw
    // messages here and a SINGLE worker processes them IN ORDER (ordering is required — draws are stateful),
    // so network reads stay fast and decode overlaps with I/O. SURFACE_CREATE also rides the queue so the
    // primary-surface framebuffer is created before the draws that target it.
    private readonly System.Threading.Channels.Channel<(int type, byte[] data)> _drawQueue =
        System.Threading.Channels.Channel.CreateUnbounded<(int, byte[])>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private Task? _drawWorker;

    // Active video streams. The SPICE server sends moving-image regions (e.g. a playing video) as a codec
    // stream instead of per-frame DRAW_COPY: STREAM_CREATE announces the region + codec, STREAM_DATA carries
    // each encoded frame, STREAM_DESTROY ends it. We decode MJPEG frames and composite them into the shared
    // framebuffer exactly like a draw, so the region updates without any client input. Keyed by stream id.
    private readonly Dictionary<int, StreamInfo> _streams = new();
    private readonly record struct StreamInfo(int Codec, int Left, int Top, int Width, int Height);
    // Per-stream persistent JPEG decoder — retains the Huffman/quant tables that abbreviated MJPEG frames
    // omit after the first frame. Keyed by stream id; created on first frame, dropped on STREAM_DESTROY.
    private readonly Dictionary<int, SpiceJpeg> _streamDecoders = new();

    public int Width => _width;
    public int Height => _height;

    /// <summary>Raised when a box on the primary surface changes: (x, y, w, h, top-down BGRX).</summary>
    public event Action<int, int, int, int, byte[]>? OnRectangle;

    /// <summary>Raised when the primary surface geometry is established or changes.</summary>
    public event Action<int, int>? OnGeometry;

    public SpiceDisplayChannel(IHostTransport transport, string host, int port, string? password,
        uint connectionId, byte channelId, ILogger logger, Func<string?>? passwordProvider = null)
        : base(transport, host, port, password, SpiceConst.CHANNEL_DISPLAY, channelId, connectionId, logger, passwordProvider) { }

    protected override uint ChannelCaps() => 0; // no MJPEG/VP8 caps yet → server sends drawing ops

    /// <summary>After link, the display channel must send DISPLAY_INIT before the server streams.</summary>
    public async Task SendDisplayInitAsync(CancellationToken ct)
    {
        // SpiceMsgcDisplayInit: pixmap_cache_id u8, pixmap_cache_size i64, glz_dictionary_id u8,
        // glz_dictionary_window_size i32. A conservative default set (no GLZ) is fine.
        var w = new SpiceWriter()
            .U8(1).U32(10 * 1024 * 1024).U32(0)   // cache id, cache size (i64 low/high)
            .U8(0).U32(0);                          // glz dict id, window size
        await SendAsync(SpiceConst.MSGC_DISPLAY_INIT, w.ToArray(), ct);
    }

    protected override Task HandleMessageAsync(int type, byte[] data, CancellationToken ct)
    {
        switch (type)
        {
            // Surface + draw ops are decoded/composited on the worker (off the read loop), in order.
            case SpiceConst.MSG_DISPLAY_SURFACE_CREATE:
            case SpiceConst.MSG_DISPLAY_DRAW_COPY:
            case SpiceConst.MSG_DISPLAY_DRAW_FILL:
                _drawWorker ??= Task.Run(() => DrawWorkerAsync(ct), ct);
                _drawQueue.Writer.TryWrite((type, data));
                break;
            // Video streams (STREAM_CREATE/DATA/DESTROY): the server's codec path for moving-image regions.
            // Route through the same ordered draw queue as draws — they composite into the shared framebuffer
            // and must stay ordered relative to draws that touch the same pixels.
            case SpiceConst.MSG_DISPLAY_STREAM_CREATE:
            case SpiceConst.MSG_DISPLAY_STREAM_DATA:
            case SpiceConst.MSG_DISPLAY_STREAM_DATA_SIZED:
            case SpiceConst.MSG_DISPLAY_STREAM_DESTROY:
            case SpiceConst.MSG_DISPLAY_STREAM_DESTROY_ALL:
                _drawWorker ??= Task.Run(() => DrawWorkerAsync(ct), ct);
                _drawQueue.Writer.TryWrite((type, data));
                break;
            case SpiceConst.MSG_DISPLAY_SURFACE_DESTROY:
            case SpiceConst.MSG_DISPLAY_RESET:
            case SpiceConst.MSG_DISPLAY_STREAM_CLIP:
                // Clip changes not tracked (we always composite the full stream dest rect); surface destroy /
                // reset handled elsewhere. Ignored.
                break;
            default:
                // TEMP: surface unhandled draw/display message types once each — a large unhandled paint (e.g.
                // DRAW_OPAQUE=303, DRAW_BLEND=305, COPY_BITS=104) would leave the desktop grey behind the video.
                if (_unhandledLogged.Add(type))
                    Logger.LogInformation("SPICE display: UNHANDLED msg type={Type} ({Size}B)", type, data.Length);
                break;
        }
        return Task.CompletedTask;
    }
    private readonly HashSet<int> _unhandledLogged = new();

    // Single ordered consumer of the draw queue: decode + composite off the read loop. Ordering is
    // preserved (one reader), which is required because draws mutate the shared framebuffer.
    private async Task DrawWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (type, data) in _drawQueue.Reader.ReadAllAsync(ct))
            {
                switch (type)
                {
                    case SpiceConst.MSG_DISPLAY_SURFACE_CREATE: HandleSurfaceCreate(data); break;
                    case SpiceConst.MSG_DISPLAY_DRAW_COPY: HandleDrawCopy(data); break;
                    case SpiceConst.MSG_DISPLAY_DRAW_FILL: HandleDrawFill(data); break;
                    case SpiceConst.MSG_DISPLAY_STREAM_CREATE: HandleStreamCreate(data); break;
                    case SpiceConst.MSG_DISPLAY_STREAM_DATA: HandleStreamData(data, sized: false); break;
                    case SpiceConst.MSG_DISPLAY_STREAM_DATA_SIZED: HandleStreamData(data, sized: true); break;
                    case SpiceConst.MSG_DISPLAY_STREAM_DESTROY: HandleStreamDestroy(data); break;
                    case SpiceConst.MSG_DISPLAY_STREAM_DESTROY_ALL: _streams.Clear(); _streamDecoders.Clear(); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.LogWarning(ex, "SPICE: draw worker ended with error"); }
    }

    // ── surface ───────────────────────────────────────────────────────────────────────────────────

    private void HandleSurfaceCreate(byte[] data)
    {
        // SpiceSurface: surface_id u32, width u32, height u32, format u32, flags u32.
        var r = new SpiceReader(data);
        int id = (int)r.U32();
        int width = (int)r.U32();
        int height = (int)r.U32();
        r.U32();                       // format
        uint flags = r.U32();

        if ((flags & SpiceConst.SURFACE_FLAGS_PRIMARY) == 0)
            return; // off-screen surface — not tracked in the bridge yet

        _primaryId = id;
        _width = width; _height = height;
        _fb = new byte[width * height * 4];
        Logger.LogInformation("SPICE: primary surface {W}x{H}", width, height);
        OnGeometry?.Invoke(width, height);
    }

    // ── DisplayBase (surface_id, box, clip) ─────────────────────────────────────────────────────────

    private static (int surfaceId, int left, int top, int right, int bottom) ReadBase(ref SpiceReader r)
    {
        int surfaceId = (int)r.U32();
        // SpiceRect: top, left, bottom, right (all u32).
        int top = (int)r.U32();
        int left = (int)r.U32();
        int bottom = (int)r.U32();
        int right = (int)r.U32();
        // SpiceClip: type u8 (+ rects if RECTS — we only need to skip past for offset-based reads, but the
        // draw payload that follows is read sequentially so we must consume clip rects here).
        int clipType = r.U8();
        if (clipType == SpiceConst.CLIP_TYPE_RECTS)
        {
            uint n = r.U32();
            r.Skip((int)n * 16); // each SpiceRect is 4×u32
        }
        return (surfaceId, left, top, right, bottom);
    }

    // ── DRAW_FILL (solid brush) ─────────────────────────────────────────────────────────────────────

    private void HandleDrawFill(byte[] data)
    {
        var r = new SpiceReader(data);
        var (surfaceId, left, top, right, bottom) = ReadBase(ref r);
        if (surfaceId != _primaryId || _fb.Length == 0) return;

        // SpiceFill: brush(type u8, [solid color u32 | pattern]), rop_descriptor u16, mask(QMask).
        int brushType = r.U8();
        if (brushType != 1 /* SOLID */) return; // pattern fills unsupported
        uint color = r.U32();                    // 0x00RRGGBB (xRGB)

        int w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0) return;
        byte b = (byte)(color & 0xff);
        byte g = (byte)((color >> 8) & 0xff);
        byte rr = (byte)((color >> 16) & 0xff);
        // TEMP: log large fills — a grey fill over the stream box would explain "video shows grey".
        if (w > 200 && h > 200)
            Logger.LogInformation("SPICE DRAW_FILL color=0x{C:X6} rect=({L},{T}) {W}x{H}", color & 0xFFFFFF, left, top, w, h);

        byte[] rect = new byte[w * h * 4];
        for (int i = 0; i < rect.Length; i += 4)
        {
            rect[i] = b; rect[i + 1] = g; rect[i + 2] = rr; rect[i + 3] = 0xff;
        }
        Blit(rect, w, h, left, top, w, h);
        OnRectangle?.Invoke(left, top, w, h, rect);
    }

    // ── DRAW_COPY (image) ───────────────────────────────────────────────────────────────────────────

    private void HandleDrawCopy(byte[] data)
    {
        var r = new SpiceReader(data);
        var (surfaceId, left, top, right, bottom) = ReadBase(ref r);
        if (surfaceId != _primaryId || _fb.Length == 0) return;

        int boxW = right - left, boxH = bottom - top;
        if (boxW <= 0 || boxH <= 0) return;

        // SpiceCopy: src_bitmap offset u32 (absolute, from message start). We ignore src_area/scale for
        // the common 1:1 PUT case (scaling is rare in practice; assert-and-skip otherwise).
        uint bitmapOffset = r.U32();
        if (bitmapOffset == 0) return;

        var decoded = DecodeImageAt(data, (int)bitmapOffset);
        if (decoded is not { } img) return;

        // 1:1 blit of the decoded image into the destination box. If sizes differ we clamp.
        int w = Math.Min(boxW, img.w);
        int h = Math.Min(boxH, img.h);
        Blit(img.bgra, img.w, img.h, left, top, w, h);
        // Emit exactly the destination box we touched.
        byte[] outRect = ExtractBox(left, top, w, h);
        OnRectangle?.Invoke(left, top, w, h, outRect);
    }

    // ── video streams (STREAM_CREATE / STREAM_DATA / STREAM_DESTROY) ────────────────────────────────

    // SpiceMsgDisplayStreamCreate: surface_id u32, id u32, flags u8, codec_type u8, stamp u64,
    // stream_width u32, stream_height u32, src_width u32, src_height u32, dest SpiceRect{top,left,bottom,right
    // i32}, clip{...}. We record the stream's codec and destination box; frames arrive via STREAM_DATA.
    private void HandleStreamCreate(byte[] data)
    {
        var r = new SpiceReader(data);
        int surfaceId = (int)r.U32();
        int id = (int)r.U32();
        r.U8();                       // flags
        int codec = r.U8();
        r.U64();                      // stamp
        r.U32(); r.U32();             // stream_width, stream_height
        r.U32(); r.U32();             // src_width, src_height
        int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32();  // dest SpiceRect
        int w = right - left, h = bottom - top;
        if (surfaceId != _primaryId || w <= 0 || h <= 0) return;
        _streams[id] = new StreamInfo(codec, left, top, w, h);
        if (codec != SpiceConst.VIDEO_CODEC_TYPE_MJPEG)
            Logger.LogWarning("SPICE: stream {Id} codec {Codec} unsupported (only MJPEG decoded)", id, codec);
        else
            Logger.LogInformation("SPICE: stream {Id} created MJPEG {W}x{H} @({L},{T})", id, w, h, left, top);
    }

    // SpiceMsgDisplayStreamData: base{id u32, multi_media_time u32}, data_size u32, data[]. The SIZED variant
    // inserts {width u32, height u32, dest SpiceRect} before data_size, overriding the dest box for this frame.
    private void HandleStreamData(byte[] data, bool sized)
    {
        var r = new SpiceReader(data);
        int id = (int)r.U32();
        r.U32();                       // multi_media_time
        if (!_streams.TryGetValue(id, out var s)) return;
        int left = s.Left, top = s.Top, dw = s.Width, dh = s.Height;
        if (sized)
        {
            r.U32(); r.U32();          // width, height (of the encoded frame; we take size from JPEG)
            int t = r.I32(), l = r.I32(), b = r.I32(), rr = r.I32();
            left = l; top = t; dw = rr - l; dh = b - t;
        }
        int dataSize = (int)r.U32();
        if (dataSize <= 0 || r.Remaining < dataSize) return;
        if (s.Codec != SpiceConst.VIDEO_CODEC_TYPE_MJPEG) return;  // only MJPEG decoded today

        var jpeg = r.Span(dataSize);
        // Persistent per-stream decoder: SPICE MJPEG streams are abbreviated (Huffman tables sent only in the
        // first frame, omitted thereafter), so each stream keeps its own decoder that retains those tables
        // across frames. A fresh SpiceJpeg per frame (or ImageSharp) decodes a table-less frame to grey.
        if (!_streamDecoders.TryGetValue(id, out var dec)) { dec = new SpiceJpeg(); _streamDecoders[id] = dec; }
        var decoded = dec.Decode(jpeg, null);
        if (decoded is not { } img) return;

        // QEMU encodes video-stream JPEG frames bottom-up (from a GL/framebuffer source), so each decoded
        // frame is vertically flipped relative to the surface. Flip it back. (DRAW ops are already top-down —
        // when the video pauses the region is refreshed by a normal DRAW_COPY, which must NOT be flipped;
        // that draw-vs-stream alternation is why an unflipped stream looked "upside down, occasionally right".)
        FlipVertical(img.bgra, img.w, img.h);

        // Composite the decoded frame at the stream's destination box and emit it as a rectangle, exactly like
        // a draw — this is what feeds the video into the encoder without needing client input. Clamp the box to
        // the framebuffer so a stream that overhangs the surface can't blit/extract out of bounds.
        if (_fb.Length == 0 || left < 0 || top < 0 || left >= _width || top >= _height) return;
        int w = Math.Min(Math.Min(dw, img.w), _width - left);
        int h = Math.Min(Math.Min(dh, img.h), _height - top);
        if (w <= 0 || h <= 0) return;
        Blit(img.bgra, img.w, img.h, left, top, w, h);
        byte[] outRect = ExtractBox(left, top, w, h);
        OnRectangle?.Invoke(left, top, w, h, outRect);
    }

    // SpiceMsgDisplayStreamDestroy: id u32.
    private void HandleStreamDestroy(byte[] data)
    {
        var r = new SpiceReader(data);
        int id = (int)r.U32();
        _streams.Remove(id);
        _streamDecoders.Remove(id);
    }

    /// <summary>
    /// Parses a SpiceImage at an absolute offset in the message and decodes it to (w,h,BGRA). Handles
    /// BITMAP, LZ_RGB, and FROM_CACHE; caches images flagged CACHE_ME. QUIC/JPEG are skipped (return null).
    /// </summary>
    private (int w, int h, byte[] bgra)? DecodeImageAt(byte[] msg, int offset)
    {
        var r = new SpiceReader(msg, offset);
        // SpiceImageDescriptor: id u64, type u8, flags u8, width u32, height u32.
        ulong id = r.U64();
        int imgType = r.U8();
        int flags = r.U8();
        int width = (int)r.U32();
        int height = (int)r.U32();

        (int w, int h, byte[] bgra)? result = imgType switch
        {
            SpiceConst.IMAGE_TYPE_BITMAP => DecodeBitmap(ref r, msg),
            SpiceConst.IMAGE_TYPE_LZ_RGB => DecodeLzRgb(ref r, msg),
            SpiceConst.IMAGE_TYPE_QUIC => DecodeQuic(ref r, msg),
            SpiceConst.IMAGE_TYPE_JPEG => DecodeJpeg(ref r, msg),
            SpiceConst.IMAGE_TYPE_JPEG_ALPHA => DecodeJpegAlpha(ref r, msg),
            SpiceConst.IMAGE_TYPE_FROM_CACHE or SpiceConst.IMAGE_TYPE_FROM_CACHE_LOSSLESS =>
                _cache.TryGetValue(id, out var cached) ? cached : null,
            _ => LogUnhandled(imgType),
        };

        if (result is { } dec && (flags & SpiceConst.IMAGE_FLAGS_CACHE_ME) != 0)
            _cache[id] = dec;
        return result;
    }

    private (int, int, byte[])? LogUnhandled(int imgType)
    {
        Logger.LogDebug("SPICE: unhandled image type {T}", imgType);
        return null;
    }

    /// <summary>QUIC image: data_size u32, then the QUIC stream. Decodes to top-down BGRA.</summary>
    private (int w, int h, byte[] bgra)? DecodeQuic(ref SpiceReader r, byte[] msg)
    {
        int dataSize = (int)r.U32();
        int avail = Math.Min(dataSize, msg.Length - r.Pos);
        if (avail <= 0) return null;
        byte[] stream = new byte[avail];
        Array.Copy(msg, r.Pos, stream, 0, avail);
        try { return SpiceQuic.Decode(stream); }
        catch (Exception ex) { Logger.LogDebug(ex, "SPICE: QUIC decode failed"); return null; }
    }

    /// <summary>JPEG image: data_size u32, then JPEG bytes. Decoded to BGRA via ImageSharp.</summary>
    private (int w, int h, byte[] bgra)? DecodeJpeg(ref SpiceReader r, byte[] msg)
    {
        int dataSize = (int)r.U32();
        int avail = Math.Min(dataSize, msg.Length - r.Pos);
        if (avail <= 0) return null;
        return SpiceJpeg.DecodeToBgra(msg.AsSpan(r.Pos, avail), null);
    }

    /// <summary>
    /// JPEG_ALPHA: flags u8, jpeg_size u32, data_size u32, JPEG bytes (jpeg_size), then an LZ alpha plane
    /// (data_size - jpeg_size) with its own big-endian LZ header. The RGB comes from the JPEG; alpha from LZ.
    /// </summary>
    private (int w, int h, byte[] bgra)? DecodeJpegAlpha(ref SpiceReader r, byte[] msg)
    {
        r.U8();                       // flags
        int jpegSize = (int)r.U32();
        int dataSize = (int)r.U32();
        int jpegAvail = Math.Min(jpegSize, msg.Length - r.Pos);
        if (jpegAvail <= 0) return null;
        int jpegOff = r.Pos;

        // Parse the LZ alpha plane header (big-endian, same layout as LZ_RGB) that follows the JPEG.
        byte[]? alpha = null;
        int alphaStart = jpegOff + jpegSize;
        if (dataSize > jpegSize && alphaStart + 28 <= msg.Length)
        {
            var ar = new SpiceReader(msg, alphaStart);
            ar.Skip(4); // magic
            ar.Skip(4); // version (BE)
            int lzType = ReadBe32(msg, ar.Pos); ar.Skip(4);
            int aw = ReadBe32(msg, ar.Pos); ar.Skip(4);
            int ah = ReadBe32(msg, ar.Pos); ar.Skip(4);
            ar.Skip(4); // stride
            int topDown = ReadBe32(msg, ar.Pos); ar.Skip(4);
            int alphaLen = dataSize - jpegSize - (ar.Pos - alphaStart);
            if (alphaLen > 0 && ar.Pos + alphaLen <= msg.Length)
            {
                byte[] astream = new byte[alphaLen];
                Array.Copy(msg, ar.Pos, astream, 0, alphaLen);
                alpha = SpiceLz.Decode(astream, lzType, aw, ah, topDown != 0);
            }
        }
        return SpiceJpeg.DecodeToBgra(msg.AsSpan(jpegOff, jpegAvail), alpha);
    }

    private static (int w, int h, byte[] bgra)? DecodeBitmap(ref SpiceReader r, byte[] msg)
    {
        // SpiceBitmap: format u8, flags u8, x(width) u32, y(height) u32, stride u32, palette(offset u32
        // or palette_id u64), data (rest). We only need 32bpp/RGBA; palette formats are unsupported.
        int format = r.U8();
        int flags = r.U8();
        int w = (int)r.U32();
        int h = (int)r.U32();
        int stride = (int)r.U32();
        // No PAL_FROM_CACHE handling: assume a 4-byte palette offset field for RGB formats.
        r.U32(); // palette offset (0 for truecolor)

        if (format != SpiceConst.BITMAP_FMT_32BIT && format != SpiceConst.BITMAP_FMT_RGBA)
            return null;

        bool topDown = (flags & SpiceConst.BITMAP_FLAGS_TOP_DOWN) != 0;
        int dataOff = r.Pos;
        byte[] bgra = new byte[w * h * 4];
        // Source is BGRA (little-endian xRGB → B,G,R,x); we keep BGRX, forcing alpha for 32BIT.
        for (int y = 0; y < h; y++)
        {
            int srcRow = topDown ? y : (h - 1 - y);
            int src = dataOff + srcRow * stride;
            int dst = y * w * 4;
            for (int x = 0; x < w; x++, src += 4, dst += 4)
            {
                bgra[dst] = msg[src];         // B
                bgra[dst + 1] = msg[src + 1]; // G
                bgra[dst + 2] = msg[src + 2]; // R
                bgra[dst + 3] = format == SpiceConst.BITMAP_FMT_32BIT ? (byte)0xff : msg[src + 3];
            }
        }
        return (w, h, bgra);
    }

    private (int w, int h, byte[] bgra)? DecodeLzRgb(ref SpiceReader r, byte[] msg)
    {
        // LZ_RGB: length u32, then a BIG-ENDIAN header: magic(4) "  ZL", version u32, type u32, width u32,
        // height u32, stride u32, top_down u32, then the LZ stream.
        int length = (int)r.U32();
        int headerStart = r.Pos;
        r.Skip(4); // magic
        int version = ReadBe32(msg, r.Pos); r.Skip(4);
        int lzType = ReadBe32(msg, r.Pos); r.Skip(4);
        int width = ReadBe32(msg, r.Pos); r.Skip(4);
        int height = ReadBe32(msg, r.Pos); r.Skip(4);
        int stride = ReadBe32(msg, r.Pos); r.Skip(4);
        int topDown = ReadBe32(msg, r.Pos); r.Skip(4);
        _ = version; _ = stride;

        int headerSize = r.Pos - headerStart;
        int dataLen = length - headerSize;
        if (dataLen <= 0 || r.Pos + dataLen > msg.Length) dataLen = msg.Length - r.Pos;
        byte[] stream = new byte[dataLen];
        Array.Copy(msg, r.Pos, stream, 0, dataLen);

        byte[]? bgra = SpiceLz.Decode(stream, lzType, width, height, topDown != 0);
        return bgra == null ? null : (width, height, bgra);
    }

    private static int ReadBe32(byte[] b, int at) =>
        (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    // ── framebuffer compositing ─────────────────────────────────────────────────────────────────────

    /// <summary>Flips a top-down BGRA image in place along the horizontal axis (row order reversed).</summary>
    private static void FlipVertical(byte[] bgra, int w, int h)
    {
        int rowBytes = w * 4;
        var tmp = new byte[rowBytes];
        for (int y = 0; y < h / 2; y++)
        {
            int top = y * rowBytes;
            int bot = (h - 1 - y) * rowBytes;
            Array.Copy(bgra, top, tmp, 0, rowBytes);
            Array.Copy(bgra, bot, bgra, top, rowBytes);
            Array.Copy(tmp, 0, bgra, bot, rowBytes);
        }
    }

    /// <summary>Blits the top-left w×h region of a source BGRA image into the primary FB at (dx,dy).</summary>
    private void Blit(byte[] src, int srcW, int srcH, int dx, int dy, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int py = dy + y;
            if (py < 0 || py >= _height) continue;
            int srcRow = (y * srcW) * 4;
            int dstRow = (py * _width + dx) * 4;
            int copyW = Math.Min(w, _width - dx);
            if (copyW <= 0) continue;
            Array.Copy(src, srcRow, _fb, dstRow, copyW * 4);
        }
    }

    private byte[] ExtractBox(int x, int y, int w, int h)
    {
        byte[] rect = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int src = ((y + row) * _width + x) * 4;
            Array.Copy(_fb, src, rect, row * w * 4, w * 4);
        }
        return rect;
    }
}
