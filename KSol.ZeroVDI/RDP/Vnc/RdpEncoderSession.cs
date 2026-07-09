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
internal sealed class RdpEncoderSession
{
    private readonly IProtocolSource _source;
    private readonly RdpServerFrontEnd _frontEnd;
    private readonly ILogger _logger;

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

    public RdpEncoderSession(IProtocolSource source, Stream serverSide, ILogger logger)
    {
        _source = source;
        _logger = logger;
        // Fallback size = source native size; overridden by the browser's requested size at handshake.
        _frontEnd = new RdpServerFrontEnd(serverSide, source.Width, source.Height, logger);
        _source.OnRectangle += OnSourceRectangle;
        _frontEnd.OnMouse += OnBrowserMouse;
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

        var sourcePump = _source.RunAsync(ct);
        await _source.RequestFullFrameAsync(ct);

        // Paint the initial letterbox bars so the browser doesn't show uninitialized canvas around the fit.
        await FlushRegionAsync(0, 0, _fbW, _fbH, ct);

        var drain = DrainBrowserInputAsync(ct);
        await Task.WhenAny(sourcePump, drain);
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
        // Fire the bitmap update outside the lock (SendBitmap serializes on the pipe).
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
}
