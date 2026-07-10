namespace KSol.ZeroVDI.RDP.Vnc;

/// <summary>
/// The <b>shared RDP encoder</b>: drives any <see cref="IProtocolSource"/> (VNC today) into the browser
/// RDP client, independent of the source protocol. It owns the RDP server handshake
/// (<see cref="RdpServerFrontEnd"/>), presents the browser-requested desktop size, and letterbox-scales
/// the source's native desktop into that size, emitting RDP output (bitmap today; H.264/GFX etc. later).
///
///   IProtocolSource → <b>RdpEncoderSession</b> → DuplexPipeStream → RdpRelaySession → web client
///
/// This is the seam that lets new host protocols reuse all RDP/codec logic — a new protocol only writes
/// an <see cref="IProtocolSource"/>.
/// </summary>
internal sealed class RdpEncoderSession : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        if (_h264 != null) await _h264.DisposeAsync();
    }

    private readonly IProtocolSource _source;
    private readonly RdpServerFrontEnd _frontEnd;
    private readonly KeysymMap _keymap;
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;

    // GFX / H.264 pipeline (engaged only when the browser advertises AVC over the GFX channel).
    private DvcServer? _dvc;
    private RdpGfxServer? _gfx;
    private H264Encoder? _h264;
    private RfxProgressiveEncoder? _rfxProg;   // RemoteFX Progressive path (fallback for no-AVC clients)
    private volatile bool _gfxActive;   // true once GFX streaming (AVC or progressive) is negotiated → stop bitmap output
    private int _frameDirty;            // set when the framebuffer changed since the last H.264 encode

    // Session-sized top-down 32bpp BGRX framebuffer (the letterboxed composition target). Sized once the
    // RDP handshake fixes the browser-requested session size.
    private byte[] _fb = Array.Empty<byte>();
    private int _fbW, _fbH;
    // Letterbox mapping of source → session space (set once geometry is known).
    private (int x, int y, int w, int h) _fit;
    private readonly object _fbLock = new();

    // RDP PTRFLAGS ([MS-RDPBCGR] 2.2.8.1.2.2.3).
    private const int PTRFLAGS_MOVE = 0x0800, PTRFLAGS_DOWN = 0x8000;
    private const int PTRFLAGS_BUTTON1 = 0x1000, PTRFLAGS_BUTTON2 = 0x2000, PTRFLAGS_BUTTON3 = 0x4000;
    private const int PTRFLAGS_WHEEL = 0x0200, PTRFLAGS_WHEEL_NEGATIVE = 0x0100;
    // RFB pointer button mask bits: 0=left,1=middle,2=right,3=wheel-up,4=wheel-down.
    private int _rfbButtons;
    private int _lastSrcX, _lastSrcY;

    public RdpEncoderSession(IProtocolSource source, Stream serverSide, KeysymMap keymap, string ffmpegPath, ILogger logger)
    {
        _source = source;
        _keymap = keymap;
        _ffmpegPath = ffmpegPath;
        _logger = logger;
        // Fallback size = source native size; overridden by the browser's requested size at handshake.
        _frontEnd = new RdpServerFrontEnd(serverSide, source.Width, source.Height, logger);
        _source.OnRectangle += OnSourceRectangle;
        _frontEnd.OnMouse += OnBrowserMouse;
        _frontEnd.OnScancode += OnBrowserScancode;
        _frontEnd.OnUnicode += OnBrowserUnicode;
    }

    /// <summary>
    /// Runs the whole bridge: RDP handshake → size the session framebuffer + letterbox → request a full
    /// source frame → pump source rectangles and drain browser input until cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await _frontEnd.RunHandshakeAsync(ct);

        // The browser fixed the session size in its Connect-Initial; letterbox the source into it.
        lock (_fbLock)
        {
            _fbW = _frontEnd.Width; _fbH = _frontEnd.Height;
            _fb = new byte[_fbW * _fbH * 4]; // opaque black (BGRX, x=0) — the letterbox bars
            _fit = PixelConvert.Letterbox(_source.Width, _source.Height, _fbW, _fbH);
        }
        _logger.LogInformation("VNC: session {SW}x{SH}, source {W}x{H} fit @({X},{Y}) {FW}x{FH}",
            _fbW, _fbH, _source.Width, _source.Height, _fit.x, _fit.y, _fit.w, _fit.h);

        // Bring up the GFX/H.264 pipeline over drdynvc. If the browser advertises AVC we switch to H.264;
        // otherwise the bitmap fastpath below keeps working. Negotiation runs asynchronously.
        SetupGfx(ct);

        var sourcePump = _source.RunAsync(ct);
        await _source.RequestFullFrameAsync(ct);

        // Paint the initial letterbox bars so the browser doesn't show uninitialized canvas around the fit.
        await FlushRegionAsync(0, 0, _fbW, _fbH, ct);

        var drain = DrainBrowserInputAsync(ct);
        var frameClock = FrameClockAsync(ct);   // drives H.264 frames when GFX is active
        await Task.WhenAny(sourcePump, drain, frameClock);
    }

    // ── GFX / H.264 ─────────────────────────────────────────────────────────────────────────────
    private void SetupGfx(CancellationToken ct)
    {
        if (_frontEnd.DrdynvcChannelId == 0)
        {
            _logger.LogInformation("VNC: client did not request drdynvc — bitmap path only");
            return;
        }
        // DVC over the drdynvc static channel. Its send callback wraps the drdynvc payload in a
        // Send-Data-Indication on the drdynvc channel.
        _dvc = new DvcServer(
            payload => { try { _frontEnd.SendOnChannelAsync(_frontEnd.DrdynvcChannelId, payload, ct).GetAwaiter().GetResult(); } catch { } },
            _logger);
        _frontEnd.OnDrdynvcData += data => { try { _dvc!.HandleChannelChunk(data); } catch (Exception ex) { _logger.LogDebug(ex, "VNC: drdynvc handling failed"); } };

        _gfx = new RdpGfxServer(_frontEnd.Width, _frontEnd.Height,
            msg => { try { _dvc!.SendData(_gfxChannelId, msg); } catch { } }, _logger);
        _gfx.OnActivated = () => OnGfxActivated(ct);

        _dvc.Start();
        // Open the Graphics dynamic channel; when the client accepts, route its data to the GFX server.
        _gfxChannelId = _dvc.CreateChannel("Microsoft::Windows::RDS::Graphics",
            onOpen: () => _logger.LogInformation("VNC: GFX channel open"),
            onData: data => { try { _gfx!.OnChannelData(data); } catch (Exception ex) { _logger.LogDebug(ex, "VNC: GFX data failed"); } });
    }

    private uint _gfxChannelId;

    private void OnGfxActivated(CancellationToken ct)
    {
        if (_gfx!.IsProgressive)
        {
            // RemoteFX Progressive: the browser advertised no AVC decoder. Stand up the CPU progressive
            // encoder, sized to the session; the frame clock drives it exactly like H.264 (whole-frame,
            // change-detected internally). Reset() forces a full-frame first send against the fresh surface.
            _rfxProg = new RfxProgressiveEncoder(_frontEnd.Width, _frontEnd.Height);
            _rfxProg.Reset();
            _gfxActive = true;
            _logger.LogInformation("VNC: switched to RemoteFX Progressive/GFX output");
            return;
        }
        // AVC420/444: stand up the ffmpeg H.264 encoder sized to the (even) session dimensions.
        var enc = new H264Encoder(_frontEnd.Width, _frontEnd.Height, fps: 30, _ffmpegPath, _logger);
        enc.OnFrame += annexB =>
        {
            try { if (_gfx!.CanSubmitFrame) _gfx.SubmitFrame(annexB); }
            catch (Exception ex) { _logger.LogDebug(ex, "VNC: GFX submit failed"); }
        };
        try { enc.Start(); _h264 = enc; _gfxActive = true; _logger.LogInformation("VNC: switched to H.264/GFX output"); }
        catch (Exception ex) { _logger.LogWarning(ex, "VNC: H.264 encoder failed to start; staying on bitmap"); }
    }

    // Encodes the session framebuffer to H.264 at a steady cadence whenever GFX is active and the frame
    // changed since the last encode. H.264 needs whole frames (unlike the event-driven bitmap path).
    private async Task FrameClockAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(33, ct); // ~30fps
                if (!_gfxActive) continue;
                bool progressive = _rfxProg != null;
                // Progressive refines idle tiles over successive frames, so it must keep ticking even when
                // the framebuffer is unchanged (until every tile reaches lossless). H.264 only encodes on
                // change. Either way we need room in the GFX unacked window.
                if (!progressive && Interlocked.Exchange(ref _frameDirty, 0) == 0) continue;
                if (!_gfx!.CanSubmitFrame) continue;
                byte[] frame;
                lock (_fbLock)
                {
                    if (_fb.Length == 0) continue;
                    frame = (byte[])_fb.Clone();   // top-down BGRA, session-sized
                }
                if (progressive)
                {
                    // Consume the dirty flag so we don't spin; the encoder's own per-tile hashing decides
                    // what actually changed and what to refine.
                    Interlocked.Exchange(ref _frameDirty, 0);
                    var streams = _rfxProg!.Encode(frame);
                    if (streams.Count > 0) _gfx.SubmitProgressiveFrame(streams);
                    continue;
                }
                if (_h264 == null) continue;
                await _h264.EncodeAsync(frame, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // A source rectangle arrived (native coords). Scale-blit it into the letterboxed session framebuffer,
    // then emit the touched session region as a bitmap update. Runs on the source's receive loop.
    private void OnSourceRectangle(int x, int y, int w, int h, byte[] bgrx)
    {
        if (!_frontEnd.IsActive || _fb.Length == 0) return;
        int dx, dy, dw, dh;
        lock (_fbLock)
        {
            // Map the source-space rect into the letterbox region via the global fit scale.
            double sx = (double)_fit.w / _source.Width;
            double sy = (double)_fit.h / _source.Height;
            dx = _fit.x + (int)Math.Floor(x * sx);
            dy = _fit.y + (int)Math.Floor(y * sy);
            dw = Math.Max(1, (int)Math.Ceiling(w * sx));
            dh = Math.Max(1, (int)Math.Ceiling(h * sy));
            // Clamp to the fit region.
            dw = Math.Min(dw, _fit.x + _fit.w - dx);
            dh = Math.Min(dh, _fit.y + _fit.h - dy);
            if (dw <= 0 || dh <= 0) return;
            PixelConvert.ScaleBlitBgrx(bgrx, w, h, _fb, _fbW, dx, dy, dw, dh);
        }
        // When GFX/H.264 is active the frame clock encodes whole frames; just mark the framebuffer dirty
        // and skip the legacy bitmap update (sending both would double-paint / fight the surface).
        if (_gfxActive) { Interlocked.Exchange(ref _frameDirty, 1); return; }
        // Otherwise the bitmap fastpath: emit the touched session region (outside the lock).
        FlushRegionAsync(dx, dy, dw, dh, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task FlushRegionAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        BitmapRect rect;
        lock (_fbLock)
        {
            if (_fb.Length == 0) return;
            x = Math.Clamp(x, 0, _fbW); y = Math.Clamp(y, 0, _fbH);
            w = Math.Clamp(w, 0, _fbW - x); h = Math.Clamp(h, 0, _fbH - y);
            if (w <= 0 || h <= 0) return;
            rect = PixelConvert.FramebufferRegionToRect(_fb, _fbW, x, y, w, h);
        }
        try { await _frontEnd.SendBitmapAsync(new[] { rect }, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "VNC: region flush failed"); }
    }

    // Browser input → source. RunInputAsync decodes fastpath events and raises OnMouse (+ OnScancode/
    // OnUnicode in M4); we map coordinates and forward to the source.
    private Task DrainBrowserInputAsync(CancellationToken ct) => _frontEnd.RunInputAsync(ct);

    // A browser MOUSE event in SESSION coordinates. Map into source space (inverse letterbox), update the
    // RFB button mask from the RDP pointer flags, and forward an RFB PointerEvent.
    private void OnBrowserMouse(ushort ptrFlags, int sx, int sy)
    {
        int srcX, srcY;
        lock (_fbLock)
        {
            if (_fit.w <= 0 || _fit.h <= 0) return;
            // Session point → fit-region-relative → source pixel.
            double relX = (sx - _fit.x) / (double)_fit.w;
            double relY = (sy - _fit.y) / (double)_fit.h;
            srcX = Math.Clamp((int)(relX * _source.Width), 0, Math.Max(0, _source.Width - 1));
            srcY = Math.Clamp((int)(relY * _source.Height), 0, Math.Max(0, _source.Height - 1));
        }
        _lastSrcX = srcX; _lastSrcY = srcY;

        // Wheel: momentary button 4 (up) / 5 (down) press+release; RFB has no persistent wheel state.
        if ((ptrFlags & PTRFLAGS_WHEEL) != 0)
        {
            bool negative = (ptrFlags & PTRFLAGS_WHEEL_NEGATIVE) != 0;
            int wheelBit = negative ? (1 << 4) : (1 << 3);
            Forward(srcX, srcY, _rfbButtons | wheelBit);
            Forward(srcX, srcY, _rfbButtons);
            return;
        }

        // Button state: DOWN sets, its absence clears — for whichever button bit the event names. A MOVE
        // event names no button and just repositions.
        bool down = (ptrFlags & PTRFLAGS_DOWN) != 0;
        if ((ptrFlags & PTRFLAGS_BUTTON1) != 0) SetButton(0, down);
        else if ((ptrFlags & PTRFLAGS_BUTTON2) != 0) SetButton(1, down);   // middle
        else if ((ptrFlags & PTRFLAGS_BUTTON3) != 0) SetButton(2, down);   // right

        Forward(srcX, srcY, _rfbButtons);
    }

    private void SetButton(int rfbBit, bool down)
    {
        if (down) _rfbButtons |= (1 << rfbBit);
        else _rfbButtons &= ~(1 << rfbBit);
    }

    private void Forward(int x, int y, int mask)
    {
        try { _source.PointerAsync(x, y, mask, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogDebug(ex, "VNC: pointer forward failed"); }
    }

    private bool _shiftDown;
    // The keysym sent on key-DOWN for each (extended, scancode), so key-UP releases the SAME keysym even
    // if Shift changed meanwhile — otherwise a symbol like '?' would stick down on the host.
    private readonly Dictionary<int, uint> _downKeysym = new();

    // A browser SCANCODE event → X11 keysym → RFB KeyEvent. We track Shift so symbol keys can send their
    // exact shifted keysym directly (the host otherwise re-derives symbols under its own layout — the
    // Shift+'/'→'-' bug). Left/Right Shift = scancodes 0x2A/0x36 (non-extended).
    private void OnBrowserScancode(byte scancode, bool released, bool extended)
    {
        if (!extended && (scancode == 0x2A || scancode == 0x36)) _shiftDown = !released;
        int slot = (extended ? 0x100 : 0) | scancode;
        uint keysym;
        if (released)
        {
            // Release the exact keysym we pressed; fall back to a fresh lookup if we never saw the down.
            if (!_downKeysym.Remove(slot, out keysym))
                keysym = _keymap.ScancodeToKeysym(scancode, extended, _shiftDown);
        }
        else
        {
            keysym = _keymap.ScancodeToKeysym(scancode, extended, _shiftDown);
            if (keysym != 0) _downKeysym[slot] = keysym;
        }
        if (keysym != 0) ForwardKey(keysym, !released);
    }

    // The browser client sends only scancode events; a Unicode path is not used, but keep the hook so a
    // future client that sends Unicode still types (Latin-1 direct / X11 Unicode keysym).
    private void OnBrowserUnicode(ushort codeUnit, bool released)
    {
        uint keysym = codeUnit is >= 0x20 and <= 0xFF ? codeUnit : (0x01000000u | codeUnit);
        if (keysym != 0) ForwardKey(keysym, !released);
    }

    private void ForwardKey(uint keysym, bool down)
    {
        try { _source.KeyAsync(keysym, down, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogDebug(ex, "VNC: key forward failed"); }
    }
}
