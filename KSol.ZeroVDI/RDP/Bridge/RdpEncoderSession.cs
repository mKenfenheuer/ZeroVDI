namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// The <b>shared RDP encoder</b>: drives any <see cref="IProtocolSource"/> (VNC or SPICE) into the browser
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
        _frameCredits.Dispose();
    }

    private readonly IProtocolSource _source;
    private readonly RdpServerFrontEnd _frontEnd;
    private readonly KeysymMap _keymap;
    private readonly IH264EncoderFactory _h264Factory;
    private readonly ILogger _logger;
    private readonly Func<int>? _bitmapCongestion;   // browser-side pending PDU depth, or null if unavailable

    // ── end-to-end frame-ack gate ───────────────────────────────────────────────────────────────
    // Closes the loop: target frame → server → client → client ACK → server → target → next frame/delta,
    // so the target never runs ahead of the client by more than _maxFramesInFlight frames (1 = strict
    // lockstep). The mechanism differs per output path because source-delta ↔ client-frame cardinality does:
    //   • Bitmap path (request-driven, 1:1): the source (VNC, self-clocking) awaits BeforeNextFrame before
    //     pulling its next incremental. That wait is this frame-credit semaphore, replenished when the
    //     just-sent bitmap frame drains out of the server→browser queue (the synthetic "client received it"
    //     signal — RDP bitmap fastpath has no per-frame client ACK). See WaitForFrameCreditAsync / the
    //     bitmap clock. Credits are capped at _maxFramesInFlight so a burst can't over-credit the window.
    //   • GFX/H.264 path (clock-driven, N:1): back-pressure is the RDPEGFX unacked-frame window inside
    //     RdpGfxServer (CanSubmitFrame, also sized to _maxFramesInFlight, driven by the real client
    //     FRAME_ACKNOWLEDGE). The source runs credit-free there — see WaitForFrameCreditAsync.
    private readonly int _maxFramesInFlight;
    private readonly SemaphoreSlim _frameCredits;

    // TEMP first-frame timing tracer (chasing the "first frame only after mouse move" delay). Δms since
    // the session started running; one line per milestone. Remove once the delay is confirmed fixed.
    private readonly System.Diagnostics.Stopwatch _ff = System.Diagnostics.Stopwatch.StartNew();
    private readonly HashSet<string> _ffSeen = new();
    private void FF(string tag) => _logger.LogInformation("[ff {Wall}] +{Ms}ms {Tag}",
        DateTime.Now.ToString("HH:mm:ss.fff"), _ff.ElapsedMilliseconds, tag);
    private void FFOnce(string tag) { lock (_ffSeen) { if (!_ffSeen.Add(tag)) return; } FF(tag); }

    // GFX / H.264 pipeline (engaged only when the browser advertises AVC over the GFX channel).
    private DvcServer? _dvc;
    private RdpGfxServer? _gfx;
    private IH264Encoder? _h264;
    private RfxProgressiveEncoder? _rfxProg;   // RemoteFX Progressive path (fallback for no-AVC clients)
    private volatile bool _gfxActive;   // true once GFX streaming (AVC or progressive) is negotiated → stop bitmap output
    // GFX-path dirty region: the union of every source rect touched since the last GFX encode, in session
    // coords. Unlike the bitmap `_dirty` box (consumed by BitmapClockAsync), this one is consumed by the
    // frame clock so the GFX encoders can (a) skip encoding entirely when nothing changed and (b) restrict
    // work to the changed area (progressive tile scan).
    private readonly object _gfxDirtyLock = new();
    private bool _gfxDirty;
    private int _gx0, _gy0, _gx1, _gy1;   // inclusive dirty bounding box (session coords)

    // H.264 in-flight accounting. ffmpeg encodes asynchronously: the frame clock FEEDS a frame, and its AU
    // emerges later on the reader thread → SubmitFrame. The GFX unacked-frame window (_frameId, consumed in
    // SubmitFrame) therefore isn't consumed at feed time, so a naive feed-time CanSubmitFrame check would let
    // the clock overrun the window while frames sit in ffmpeg. We bridge the gap with a fed-not-yet-submitted
    // counter: the effective in-flight depth is (GFX unacked) + (frames in the ffmpeg pipeline). Incremented
    // when we feed, decremented when the AU is submitted.
    private int _h264Inflight;

    // TEMP rate tracer (chasing "video stutters unless the mouse moves"). Counts, per one-second window:
    // source rectangles received, frames fed to the encoder, and frames submitted to the client — plus the
    // window/inflight state at report time. If _rectCount goes to ~0 during a stutter the SERVER stopped
    // pushing (input-gated upstream); if rects keep coming but fed/submitted stall, the bottleneck is our
    // window/ack gate. Remove once diagnosed.
    private int _rectCount, _fedCount, _submitCount;
    private long _rateWindowStart;
    private long _lastFbSample;   // TEMP encoder-fb color sampler
    // Shared OnFrame handler for every H.264 encoder instance. ALWAYS submits the AU (the window is gated at
    // feed time, not here — dropping a late AU would freeze the stream until the next source change), and
    // releases the fed-time in-flight reservation. Held as a field so it can be detached from a retiring
    // encoder on restart, keeping that encoder's trailing AUs off the new pipeline's counter.
    private void H264Submit(byte[] annexB)
    {
        try { _gfx!.SubmitFrame(annexB); Interlocked.Increment(ref _submitCount); FFOnce("first GFX SubmitFrame (frame sent to client)"); }
        catch (Exception ex) { _logger.LogDebug(ex, "Bridge: GFX submit failed"); }
        finally { Interlocked.Decrement(ref _h264Inflight); }
    }
    private Action<byte[]>? _h264SubmitCache;
    private Action<byte[]> _h264Submit => _h264SubmitCache ??= H264Submit;

    // Bitmap-path dirty accumulator. Source rectangles NEVER flush inline (that coupled display updates to
    // the source receive loop and, for VNC, stalled the next FramebufferUpdateRequest — the "display lags
    // behind input, wiggle to advance" bug). Instead they union their touched session box into `_dirtyBox`
    // and a steady bitmap frame clock (BitmapClockAsync) flushes the accumulated region. This decouples
    // delivery from input and coalesces a burst of small rects into one send.
    private readonly object _dirtyLock = new();
    private bool _dirty;
    private int _dx0, _dy0, _dx1, _dy1;   // inclusive dirty bounding box in session coords

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

    public RdpEncoderSession(IProtocolSource source, Stream serverSide, KeysymMap keymap, IH264EncoderFactory h264Factory,
        ILogger logger, Func<int>? bitmapCongestion = null, int? maxFramesInFlight = null)
    {
        _source = source;
        _keymap = keymap;
        _h264Factory = h264Factory;
        _logger = logger;
        _bitmapCongestion = bitmapCongestion;
        // Frames-in-flight window (end-to-end ack gate). Precedence: explicit ctor arg → ZEROVDI_MAX_FRAMES_IN_FLIGHT
        // env var → default 1 (strict lockstep). Clamped to ≥ 1.
        _maxFramesInFlight = Math.Max(1, maxFramesInFlight ?? ResolveMaxFramesInFlightEnv());
        _frameCredits = new SemaphoreSlim(_maxFramesInFlight, _maxFramesInFlight);
        // Fallback size = source native size; overridden by the browser's requested size at handshake.
        _frontEnd = new RdpServerFrontEnd(serverSide, source.Width, source.Height, logger);
        // Self-clocking sources await this before pulling their next delta — closed-loop on the client ack.
        _source.BeforeNextFrame = WaitForFrameCreditAsync;
        _source.OnRectangle += OnSourceRectangle;
        _source.OnGeometryChanged += OnSourceGeometryChanged;
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
        _ff.Restart();
        FF("RunAsync: starting RDP handshake");
        await _frontEnd.RunHandshakeAsync(ct);
        FF("RDP handshake done (session ACTIVE)");

        // Never upscale. The browser fixes a requested session size in its Connect-Initial, but if the source
        // desktop is smaller (e.g. macOS ARD serves a fixed 1280x720 and can't be resized over RFB), stretching
        // it up to the request is both blurry and a per-rect CPU scale-blit tax. So clamp the session down to the
        // source when the source is smaller in either axis — the letterbox fit is then identity (1:1, sharp) and
        // the browser client letterboxes the smaller canvas in its own window. Downscaling a larger source is
        // still allowed (the fit shrinks it). Applied BEFORE SetupGfx so the GFX surface is built at the final size.
        {
            int reqW = _frontEnd.Width, reqH = _frontEnd.Height;
            int sesW = Math.Min(reqW, _source.Width) & ~1;
            int sesH = Math.Min(reqH, _source.Height) & ~1;
            if (sesW != reqW || sesH != reqH)
            {
                _logger.LogInformation("Bridge: clamping session {ReqW}x{ReqH} down to source {SW}x{SH} (no upscaling)",
                    reqW, reqH, sesW, sesH);
                _frontEnd.SetSize(sesW, sesH);
            }
        }

        // Letterbox (now identity when we clamped to the source, or a downscale for a larger source).
        lock (_fbLock)
        {
            _fbW = _frontEnd.Width; _fbH = _frontEnd.Height;
            _fb = new byte[_fbW * _fbH * 4]; // opaque black (BGRX, x=0) — the letterbox bars
            _fit = PixelConvert.Letterbox(_source.Width, _source.Height, _fbW, _fbH);
        }
        _logger.LogInformation("Bridge: session {SW}x{SH}, source {W}x{H} fit @({X},{Y}) {FW}x{FH}",
            _fbW, _fbH, _source.Width, _source.Height, _fit.x, _fit.y, _fit.w, _fit.h);

        // Bring up the GFX/H.264 pipeline over drdynvc. If the browser advertises AVC we switch to H.264;
        // otherwise the bitmap fastpath below keeps working. Negotiation runs asynchronously.
        SetupGfx(ct);

        // Ask the source to match the browser's session size 1:1 from the start (no letterbox scaling).
        // Best-effort — sources without a resizable guest ignore it and we keep the letterbox above.
        try { await _source.RequestResizeAsync(_fbW, _fbH, ct); } catch (Exception ex) { _logger.LogDebug(ex, "Bridge: initial resize request failed"); }

        var sourcePump = _source.RunAsync(ct);
        await _source.RequestFullFrameAsync(ct);

        // Paint the initial letterbox bars so the browser doesn't show uninitialized canvas around the fit.
        await FlushRegionAsync(0, 0, _fbW, _fbH, ct);

        var drain = DrainBrowserInputAsync(ct);
        var frameClock = FrameClockAsync(ct);   // drives H.264 frames when GFX is active
        var bitmapClock = BitmapClockAsync(ct); // drives coalesced bitmap flushes when GFX is inactive
        var congestion = CongestionControlAsync(ct); // adapts quality (client + host) to link backpressure
        await Task.WhenAny(sourcePump, drain, frameClock, bitmapClock, congestion);
    }

    // Reads ZEROVDI_MAX_FRAMES_IN_FLIGHT (a positive int) if set; otherwise 1 (strict lockstep).
    private static int ResolveMaxFramesInFlightEnv()
    {
        var s = Environment.GetEnvironmentVariable("ZEROVDI_MAX_FRAMES_IN_FLIGHT");
        return int.TryParse(s, out var n) && n >= 1 ? n : 1;
    }

    // ── end-to-end frame-ack gate ───────────────────────────────────────────────────────────────
    // The gate a self-clocking source (VNC) awaits before pulling its next delta.
    //
    // Path-dependent, because source-delta ↔ client-frame cardinality differs:
    //   • Bitmap path (request-driven, 1:1): a source pull produces exactly one bitmap frame that drains to
    //     the client and releases exactly one credit. Blocking here on a credit gives true end-to-end
    //     lockstep (window == _maxFramesInFlight). This is where the closed loop lives.
    //   • GFX/H.264 path (clock-driven, N:1): source deltas are accumulated non-destructively into the
    //     shared framebuffer and the frame clock re-encodes whole-frame snapshots on its own cadence, so
    //     deltas and client frames are NOT 1:1 (several deltas can fold into one encoded frame). Gating the
    //     source pull per client-ack would desync (credits consumed per-delta, released per-frame) and
    //     stall. Client back-pressure in this path is instead applied where it belongs — RdpGfxServer's
    //     unacked-frame window (also sized to _maxFramesInFlight) gates CanSubmitFrame. So here the source
    //     runs free: we do not consume a credit.
    // A source parked on a credit when GFX activates is released by WakeSourceForGfxHandover().
    private async Task WaitForFrameCreditAsync(CancellationToken ct)
    {
        if (_gfxActive) return;
        await _frameCredits.WaitAsync(ct);
        // Re-check: GFX may have activated while we were parked. If so, don't hold the credit hostage —
        // hand it straight back so the bitmap-drain accounting stays balanced for a later GFX→bitmap fallback.
        if (_gfxActive) ReleaseFrameCredit();
    }

    // Called the moment GFX activates. If the source is parked on a frame credit (it pulled a bitmap-phase
    // frame and is waiting for that frame's drain-ack, which won't come now that the bitmap clock has stood
    // down), release one credit to wake it; it re-checks _gfxActive and proceeds credit-free from here on.
    private void WakeSourceForGfxHandover() => ReleaseFrameCredit();

    // Returns one frame credit — called when a bitmap frame drains to the client (the bitmap path's
    // end-to-end ack). Capped at the window size so a burst can't inflate the in-flight budget.
    private void ReleaseFrameCredit()
    {
        // SemaphoreSlim has no "current count" ceiling of its own beyond maxCount; we constructed it with
        // maxCount == _maxFramesInFlight, so Release past that throws. Guard by only releasing when below cap.
        try { if (_frameCredits.CurrentCount < _maxFramesInFlight) _frameCredits.Release(); }
        catch (SemaphoreFullException) { /* already at cap — ignore */ }
    }

    // ── GFX / H.264 ─────────────────────────────────────────────────────────────────────────────
    private void SetupGfx(CancellationToken ct)
    {
        if (_frontEnd.DrdynvcChannelId == 0)
        {
            _logger.LogInformation("Bridge: client did not request drdynvc — bitmap path only");
            return;
        }
        // DVC over the drdynvc static channel. Its send callback wraps the drdynvc payload in a
        // Send-Data-Indication on the drdynvc channel.
        _dvc = new DvcServer(
            payload => { try { _frontEnd.SendOnChannelAsync(_frontEnd.DrdynvcChannelId, payload, ct).GetAwaiter().GetResult(); } catch { } },
            _logger);
        _frontEnd.OnDrdynvcData += data => { try { _dvc!.HandleChannelChunk(data); } catch (Exception ex) { _logger.LogDebug(ex, "Bridge: drdynvc handling failed"); } };

        _gfx = new RdpGfxServer(_frontEnd.Width, _frontEnd.Height,
            msg => { try { _dvc!.SendData(_gfxChannelId, msg); } catch { } }, _logger, (uint)_maxFramesInFlight);
        _gfx.OnActivated = () => OnGfxActivated(ct);
        // Note: the GFX path's client-ack back-pressure is applied inside RdpGfxServer (CanSubmitFrame gates
        // sends at the unacked-frame window). It deliberately does NOT touch the source-side frame-credit
        // semaphore, which is the bitmap path's 1:1 end-to-end gate — see WaitForFrameCreditAsync.

        _dvc.Start();
        // Open the Graphics dynamic channel; when the client accepts, route its data to the GFX server.
        _gfxChannelId = _dvc.CreateChannel("Microsoft::Windows::RDS::Graphics",
            onOpen: () => _logger.LogInformation("Bridge: GFX channel open"),
            onData: data => { try { _gfx!.OnChannelData(data); } catch (Exception ex) { _logger.LogDebug(ex, "Bridge: GFX data failed"); } });

        // Open the DisplayControl channel (MS-RDPEDISP). The browser sends MONITOR_LAYOUT on it to tell us
        // the resolution it wants; we relay that to the source (guest resize). We must send the CAPS PDU
        // first so the client marks the channel active and starts sending layouts.
        _dispChannelId = _dvc.CreateChannel(DisplayControlChannelName,
            onOpen: () => { try { SendDisplayControlCaps(); } catch (Exception ex) { _logger.LogDebug(ex, "Bridge: RDPEDISP caps failed"); } },
            onData: data => { try { OnDisplayControlData(data); } catch (Exception ex) { _logger.LogDebug(ex, "Bridge: RDPEDISP data failed"); } });
    }

    private uint _gfxChannelId;
    private uint _dispChannelId;
    private const string DisplayControlChannelName = "Microsoft::Windows::RDS::DisplayControl";

    // MS-RDPEDISP PDU types.
    private const uint DISPLAYCONTROL_PDU_TYPE_CAPS = 0x00000005;
    private const uint DISPLAYCONTROL_PDU_TYPE_MONITOR_LAYOUT = 0x00000002;

    // Sends DISPLAYCONTROL_CAPS: header(type,length) + maxNumMonitors, maxMonitorAreaFactorA/B. Advertising
    // caps marks the channel active on the client, unlocking its MONITOR_LAYOUT sends.
    private void SendDisplayControlCaps()
    {
        var body = new W();
        body.U32le(1);          // maxNumMonitors
        body.U32le(8192);       // maxMonitorAreaFactorA
        body.U32le(8192);       // maxMonitorAreaFactorB
        var b = body.ToArray();
        var pdu = new W();
        pdu.U32le(DISPLAYCONTROL_PDU_TYPE_CAPS);
        pdu.U32le((uint)(8 + b.Length));
        pdu.Bytes(b);
        _dvc!.SendData(_dispChannelId, pdu.ToArray());
        _logger.LogInformation("Bridge: RDPEDISP caps sent");
    }

    // Parses a DISPLAYCONTROL_MONITOR_LAYOUT_PDU from the client and relays the requested primary-monitor
    // size to the source. [MS-RDPEDISP] 2.2.2.2: header(type,length), MonitorLayoutSize u32(=40),
    // NumMonitors u32, then per-monitor: Flags, Left, Top, Width, Height, ... (we take monitor 0's W/H).
    private void OnDisplayControlData(byte[] data)
    {
        if (data.Length < 8) return;
        var r = new Cur(data);
        uint type = r.U32le();
        r.U32le();  // length
        if (type != DISPLAYCONTROL_PDU_TYPE_MONITOR_LAYOUT) return;
        if (r.Remaining < 8) return;
        r.U32le();                       // MonitorLayoutSize (40)
        uint numMon = r.U32le();
        if (numMon == 0 || r.Remaining < 40) return;
        r.U32le();                       // Flags
        r.U32le(); r.U32le();            // Left, Top
        int w = (int)r.U32le();
        int h = (int)r.U32le();
        if (w <= 0 || h <= 0) return;
        _logger.LogInformation("Bridge: client requested resolution {W}x{H} (MONITOR_LAYOUT)", w, h);
        // Relay to the source; a successful guest resize returns via OnGeometryChanged → ResizeSession.
        try { _source.RequestResizeAsync(w, h, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Bridge: resize request failed"); }
    }

    // The source's native desktop changed size (e.g. SPICE guest re-created its surface). Two things must
    // update: (1) the letterbox mapping `_fit` — ALWAYS, since the source dimensions changed; when the
    // source now matches the session this makes `_fit` identity (no bars). (2) the session/GFX surface — only
    // if we want the session to follow the source (we drove the guest to the session size, so they usually
    // match already and no GFX rebuild is needed). Recompute the fit first, then resize the session if the
    // source size differs from it.
    private void OnSourceGeometryChanged(int w, int h)
    {
        try
        {
            int targetW = w & ~1, targetH = h & ~1;
            bool sessionChanged;
            lock (_fbLock)
            {
                sessionChanged = targetW != _fbW || targetH != _fbH;
                if (!sessionChanged)
                {
                    // Session unchanged (source now matches it): just refresh the fit — it becomes identity
                    // for a 1:1 source, clearing the stale letterbox bars from the initial mismatched size.
                    _fit = PixelConvert.Letterbox(_source.Width, _source.Height, _fbW, _fbH);
                    // Repaint the whole framebuffer black first so any old letterbox bars are overwritten,
                    // then the source's next full frame fills the (now full-bleed) fit region.
                    Array.Clear(_fb, 0, _fb.Length);
                }
            }
            if (sessionChanged) { ResizeSession(targetW, targetH); return; }
            _logger.LogInformation("Bridge: source resized to {W}x{H}; fit @({X},{Y}) {FW}x{FH} (session {SW}x{SH})",
                w, h, _fit.x, _fit.y, _fit.w, _fit.h, _fbW, _fbH);
            _ = _source.RequestFullFrameAsync(CancellationToken.None);
            MarkGfxDirty(0, 0, _fbW - 1, _fbH - 1); // GFX path: repaint whole (now full-bleed) framebuffer
            MarkDirty(0, 0, _fbW - 1, _fbH - 1);    // bitmap path: same
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Bridge: source resize handling failed"); }
    }

    // Resize the whole output chain to a new session size. Runs the RDP client through a GFX
    // RESET_GRAPHICS + surface recreate (its canvas follows), rebuilds the letterbox framebuffer (now 1:1),
    // and restarts the H.264 encoder at the new dimensions.
    private void ResizeSession(int newW, int newH)
    {
        newW &= ~1; newH &= ~1; // even dims for H.264/GFX
        if (newW <= 0 || newH <= 0) return;
        lock (_fbLock)
        {
            if (newW == _fbW && newH == _fbH) return;
            _fbW = newW; _fbH = newH;
            _fb = new byte[_fbW * _fbH * 4];
            _fit = PixelConvert.Letterbox(_source.Width, _source.Height, _fbW, _fbH);
        }
        _frontEnd.SetSize(newW, newH);
        _logger.LogInformation("Bridge: resized session to {W}x{H}, source {SW}x{SH} fit @({X},{Y}) {FW}x{FH}",
            newW, newH, _source.Width, _source.Height, _fit.x, _fit.y, _fit.w, _fit.h);

        // Rebuild the GFX surface at the new size (RESET_GRAPHICS + DELETE/CREATE/MAP → client canvas follows).
        _gfx?.Resize(newW, newH);

        // Restart the H.264 encoder sized to the new surface (progressive re-sizes itself on next Encode).
        if (_h264 != null)
        {
            var old = _h264;
            old.OnFrame -= _h264Submit;      // old encoder's trailing AUs must NOT touch the new pipeline's counter
            _ = old.DisposeAsync();
            Interlocked.Exchange(ref _h264Inflight, 0);  // pipeline rebuilt: reset the fed-not-submitted count
            var enc = _h264Factory.Create(newW, newH, fps: 30, crf: TierCrf[_tier], maxKbps: TierKbps(_tier, 30), _logger);
            enc.OnFrame += _h264Submit;
            try { enc.Start(); _h264 = enc; } catch (Exception ex) { _logger.LogWarning(ex, "Bridge: H.264 restart failed"); _h264 = null; }
        }
        if (_rfxProg != null) _rfxProg = new RfxProgressiveEncoder(newW, newH);
        MarkGfxDirty(0, 0, newW - 1, newH - 1); // force a full re-encode against the rebuilt surface
    }

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
            WakeSourceForGfxHandover();
            // Prime a full-frame dirty so the frame clock encodes the initial screen immediately (the clock
            // now gates progressive on the dirty box too — a fresh handover would otherwise wait for the next
            // source rect). Reset() already forces every tile on that first encode.
            MarkGfxDirty(0, 0, _fbW - 1, _fbH - 1);
            _logger.LogInformation("Bridge: switched to RemoteFX Progressive/GFX output");
            return;
        }
        // AVC420/444: stand up the ffmpeg H.264 encoder sized to the (even) session dimensions, at the
        // current congestion tier's CRF and (resolution-scaled) bitrate ceiling.
        var enc = _h264Factory.Create(_frontEnd.Width, _frontEnd.Height, fps: 30, crf: TierCrf[_tier], maxKbps: TierKbps(_tier, 30), _logger);
        enc.OnFrame += _h264Submit;
        try
        {
            Interlocked.Exchange(ref _h264Inflight, 0);
            enc.Start(); _h264 = enc; _gfxActive = true;
            WakeSourceForGfxHandover();
            // Prime the FIRST frame. The framebuffer already holds the source's initial full frame (painted
            // during the bitmap phase), but H.264 only encodes when the GFX dirty box is set — which otherwise
            // waits for the NEXT source rectangle. On an idle desktop that could be many seconds (the
            // "first frame takes 20s" bug), so mark the whole frame dirty now to encode+send immediately.
            MarkGfxDirty(0, 0, _fbW - 1, _fbH - 1);
            FF("GFX activated → H.264 (primed first frame dirty)");
            _logger.LogInformation("Bridge: switched to H.264/GFX output");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Bridge: H.264 encoder failed to start; staying on bitmap"); }
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

                // TEMP: once-per-second rate report — see _rectCount comment.
                long nowMs = Environment.TickCount64;
                if (nowMs - _rateWindowStart >= 1000)
                {
                    _rateWindowStart = nowMs;
                    int rects = Interlocked.Exchange(ref _rectCount, 0);
                    int fed = Interlocked.Exchange(ref _fedCount, 0);
                    int sub = Interlocked.Exchange(ref _submitCount, 0);
                    _logger.LogInformation("[rate] rects={Rects}/s fed={Fed}/s submitted={Sub}/s inflight={IF} unacked={UA} canSubmit={CS}",
                        rects, fed, sub, _h264Inflight, _gfx?.UnackedDepth ?? -1, _gfx?.CanSubmitH264(_h264Inflight) ?? false);
                }

                bool progressive = _rfxProg != null;

                if (progressive)
                {
                    // Progressive submits synchronously, so the GFX unacked window (CanSubmitFrame) is the whole
                    // story. Check it BEFORE consuming the dirty box: if we cleared dirty then found we couldn't
                    // submit, the signal would be lost until the next source change ("first frame only after a
                    // mouse move"). Bail with the box intact and retry next tick.
                    if (!_gfx!.CanSubmitFrame) { lock (_gfxDirtyLock) { if (_gfxDirty) FFOnce("frame-clock: dirty but CanSubmitFrame=false (waiting)"); } continue; }
                    bool pdirty = TakeGfxDirty(out int px0, out int py0, out int px1, out int py1);
                    // Must keep ticking while any tile is still below lossless (to refine idle tiles), even
                    // with an unchanged framebuffer — so run on dirty OR pending refinement.
                    if (!pdirty && !_rfxProg!.HasPendingRefinement) continue;
                    FFOnce("frame-clock: encoding first frame");
                    byte[] pframe;
                    lock (_fbLock) { if (_fb.Length == 0) continue; pframe = (byte[])_fb.Clone(); }
                    // Pass the dirty box so only changed tiles are re-hashed (idle screen hashes nothing);
                    // refinement of already-sent tiles happens regardless inside the encoder.
                    (int, int, int, int)? box = pdirty ? (px0, py0, px1, py1) : null;
                    var streams = _rfxProg!.Encode(pframe, box);
                    if (streams.Count > 0) _gfx.SubmitProgressiveFrame(streams);
                    continue;
                }

                // H.264: ffmpeg encodes asynchronously, so the effective in-flight depth is the GFX unacked
                // window PLUS frames sitting in the ffmpeg pipeline (fed but not yet submitted). Gate the FEED
                // on that combined depth — otherwise the clock overruns the window while frames are in-flight
                // in ffmpeg, and the AUs that emerge past the window used to be dropped, freezing the stream
                // until the next source change (the "video plays a second then freezes" stall). We reserve a
                // slot here (++_h264Inflight) and release it in the OnFrame submit.
                if (!_gfx!.CanSubmitH264(_h264Inflight))
                {
                    lock (_gfxDirtyLock) { if (_gfxDirty) FFOnce("frame-clock: dirty but window full (waiting)"); }
                    continue;
                }
                FFOnce("frame-clock: CanSubmitFrame=true");
                // Take the dirty box (clears it) only once we know we'll feed — purely change-driven.
                bool dirty = TakeGfxDirty(out _, out _, out _, out _);
                if (!dirty) continue;
                FFOnce("frame-clock: encoding first frame");
                if (_h264 == null) continue;
                byte[] frame;
                lock (_fbLock)
                {
                    if (_fb.Length == 0) continue;
                    frame = (byte[])_fb.Clone();   // top-down BGRA, session-sized
                }
                // TEMP: sample the encoder-fb pixel at the video center (surface (595,478)-ish inside the
                // 1160x653@(15,152) stream) to see if COLOR reaches the encoder input, isolating grey to
                // before vs after the encoder.
                if (_fbW > 600 && _fbH > 480 && Environment.TickCount64 - _lastFbSample >= 2000)
                {
                    _lastFbSample = Environment.TickCount64;
                    int ci = (478 * _fbW + 595) * 4;
                    _logger.LogInformation("[fb-sample] encoderFB center BGRA=({B},{G},{R}) fbW={W} fbH={H}",
                        frame[ci], frame[ci + 1], frame[ci + 2], _fbW, _fbH);
                }
                Interlocked.Increment(ref _h264Inflight);   // reserve the in-flight slot; released on submit
                Interlocked.Increment(ref _fedCount);
                await _h264.EncodeAsync(frame, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── adaptive quality / congestion control ───────────────────────────────────────────────────
    // Current congestion tier (0 = link healthy … 3 = badly congested). Drives BOTH the client-facing
    // encoder (H.264 CRF / progressive already self-adapts) AND the host-facing source
    // (IProtocolSource.SetQualityTierAsync — e.g. VNC Tight quality). Hysteresis avoids oscillation.
    private int _tier;
    // H.264 CRF per tier (higher = smaller/softer). Rebuilding ffmpeg is disruptive, so we only do it on a
    // committed tier change.
    private static readonly int[] TierCrf = { 23, 27, 31, 35 };
    // H.264 bitrate ceiling per tier, expressed as bits-per-pixel-per-second so it scales with the session
    // resolution and framerate — a 1080p desktop gets a proportionally higher cap than a 720p one at the same
    // tier. The cap turns the CRF stream into capped-CRF (VBV): quality-driven when the scene is cheap, but the
    // instantaneous bitrate never runs away on a busy/animated desktop, which is what made the 5264 stream's
    // bitrate spike on bridged sessions. Tier 0 ≈ 0.10 bpp (≈6.2 Mbit/s @1080p30), tightening down to tier 3.
    private static readonly double[] TierBpp = { 0.10, 0.06, 0.035, 0.02 };

    // The bitrate ceiling (kbit/s) for a tier at the current session size/fps, from TierBpp. Clamped to a sane
    // floor so a tiny session still gets a usable cap.
    private int TierKbps(int tier, int fps)
    {
        long bitsPerSec = (long)(TierBpp[tier] * _frontEnd.Width * _frontEnd.Height * fps);
        return Math.Max(800, (int)(bitsPerSec / 1000));   // kbit/s, ≥800
    }

    private async Task CongestionControlAsync(CancellationToken ct)
    {
        try
        {
            int badStreak = 0, goodStreak = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct); // sample twice a second — slow enough not to thrash ffmpeg
                if (!_frontEnd.IsActive) continue;

                // Congestion signal, normalised to 0..1. GFX path: unacked-frame window fill. Bitmap path:
                // browser-side pending PDU queue depth (outrunning the relay/link).
                double load;
                if (_gfxActive && _gfx != null)
                {
                    int win = Math.Max(1, _gfx.UnackedWindow);
                    load = Math.Min(1.0, _gfx.UnackedDepth / (double)win);
                }
                else
                {
                    int pending = _bitmapCongestion?.Invoke() ?? 0;
                    load = Math.Min(1.0, pending / 24.0); // ~24 queued PDUs = saturated
                }

                // Map load → desired tier with hysteresis: need a few consecutive bad/good samples to move.
                int desired = load switch { >= 0.75 => 3, >= 0.5 => 2, >= 0.25 => 1, _ => 0 };
                if (desired > _tier) { if (++badStreak >= 2) { await SetTierAsync(_tier + 1, ct); badStreak = 0; goodStreak = 0; } }
                else if (desired < _tier) { if (++goodStreak >= 4) { await SetTierAsync(_tier - 1, ct); goodStreak = 0; badStreak = 0; } }
                else { badStreak = 0; goodStreak = 0; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SetTierAsync(int tier, CancellationToken ct)
    {
        tier = Math.Clamp(tier, 0, 3);
        if (tier == _tier) return;
        _tier = tier;
        int kbps = TierKbps(tier, 30);
        _logger.LogInformation("Bridge: congestion tier → {Tier} (CRF {Crf}, cap {Kbps} kbit/s)", tier, TierCrf[tier], kbps);

        // Host-facing: ask the source for cheaper frames (VNC Tight quality; SPICE no-op today).
        try { await _source.SetQualityTierAsync(tier, ct); } catch (Exception ex) { _logger.LogDebug(ex, "Bridge: source tier failed"); }

        // Client-facing H.264: rebuild the encoder at the new CRF and bitrate ceiling (only when AVC is the
        // active GFX codec). Rebuild when either the quality target or the cap changed for this tier.
        if (_gfxActive && _h264 != null && (_h264.Crf != TierCrf[tier] || _h264.MaxKbps != kbps))
        {
            var old = _h264;
            old.OnFrame -= _h264Submit;
            _ = old.DisposeAsync();
            Interlocked.Exchange(ref _h264Inflight, 0);
            var enc = _h264Factory.Create(_frontEnd.Width, _frontEnd.Height, fps: 30, crf: TierCrf[tier], maxKbps: kbps, _logger);
            enc.OnFrame += _h264Submit;
            try { enc.Start(); _h264 = enc; MarkGfxDirty(0, 0, _fbW - 1, _fbH - 1); }
            catch (Exception ex) { _logger.LogWarning(ex, "Bridge: H.264 CRF change restart failed"); _h264 = null; }
        }
    }

    // A source rectangle arrived (native coords). Scale-blit it into the letterboxed session framebuffer,
    // then emit the touched session region as a bitmap update. Runs on the source's receive loop.
    private void OnSourceRectangle(int x, int y, int w, int h, byte[] bgrx)
    {
        if (!_frontEnd.IsActive || _fb.Length == 0) return;
        FFOnce("first source rectangle");   // dedup key is size-independent → logs exactly once, not per-size
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
        // When GFX/H.264 is active the frame clock encodes whole frames; accumulate the touched session box
        // into the GFX dirty region (so the encoders can skip idle frames and scope work to what changed) and
        // skip the legacy bitmap update (sending both would double-paint / fight the surface).
        if (_gfxActive) { Interlocked.Increment(ref _rectCount); MarkGfxDirty(dx, dy, dx + dw - 1, dy + dh - 1); return; }
        // Otherwise the bitmap fastpath: union this rect into the pending dirty box. We do NOT flush here —
        // BitmapClockAsync flushes on its own cadence so display delivery is independent of the source
        // receive loop (and, for VNC, of the incremental-request round-trip).
        MarkDirty(dx, dy, dx + dw - 1, dy + dh - 1);
    }

    // Union an inclusive session-space box into the pending GFX dirty region (H.264 / progressive path).
    private void MarkGfxDirty(int x0, int y0, int x1, int y1)
    {
        lock (_gfxDirtyLock)
        {
            if (!_gfxDirty) { _gx0 = x0; _gy0 = y0; _gx1 = x1; _gy1 = y1; _gfxDirty = true; }
            else
            {
                if (x0 < _gx0) _gx0 = x0; if (y0 < _gy0) _gy0 = y0;
                if (x1 > _gx1) _gx1 = x1; if (y1 > _gy1) _gy1 = y1;
            }
        }
    }

    // Atomically takes the accumulated GFX dirty box and clears it. Returns false when nothing changed since
    // the last take. The returned box is inclusive session coords, clamped to the framebuffer by the caller.
    private bool TakeGfxDirty(out int x0, out int y0, out int x1, out int y1)
    {
        lock (_gfxDirtyLock)
        {
            if (!_gfxDirty) { x0 = y0 = x1 = y1 = 0; return false; }
            x0 = _gx0; y0 = _gy0; x1 = _gx1; y1 = _gy1;
            _gfxDirty = false;
            return true;
        }
    }

    // Union an inclusive session-space box into the pending bitmap dirty region.
    private void MarkDirty(int x0, int y0, int x1, int y1)
    {
        lock (_dirtyLock)
        {
            if (!_dirty) { _dx0 = x0; _dy0 = y0; _dx1 = x1; _dy1 = y1; _dirty = true; }
            else
            {
                if (x0 < _dx0) _dx0 = x0; if (y0 < _dy0) _dy0 = y0;
                if (x1 > _dx1) _dx1 = x1; if (y1 > _dy1) _dy1 = y1;
            }
        }
    }

    // Bitmap frame clock: flushes the coalesced dirty region at a steady cadence whenever GFX is NOT active.
    // This is what makes the bitmap path advance on its own timer instead of only when a source rectangle
    // arrives — fixing the input-coupled lag. It skips work when nothing changed and when GFX takes over.
    //
    // Closed-loop ack: after flushing a frame it waits for that frame to drain out of the server→browser
    // queue (the synthetic "client received it" signal — RDP bitmap fastpath has no per-frame ACK), then
    // hands a frame credit back to the source so the next delta is pulled. With _maxFramesInFlight == 1 this
    // is strict lockstep: one bitmap frame is in flight to the client before the source produces the next.
    private async Task BitmapClockAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(16, ct); // ~60fps ceiling; only sends when there's a dirty region
                if (_gfxActive || !_frontEnd.IsActive) continue;
                int x0, y0, x1, y1;
                lock (_dirtyLock)
                {
                    if (!_dirty) continue;
                    x0 = _dx0; y0 = _dy0; x1 = _dx1; y1 = _dy1;
                    _dirty = false;
                }
                await FlushRegionAsync(x0, y0, x1 - x0 + 1, y1 - y0 + 1, ct);
                await AwaitBitmapDrainThenCreditAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Waits for the just-flushed bitmap frame to drain from the server→browser queue, then releases one frame
    // credit. This is the bitmap path's stand-in for a client FRAME_ACKNOWLEDGE: the RDP bitmap fastpath is
    // not frame-acked, so "the frame left the server toward the client" (queue back to ~empty) is the closest
    // observable delivered signal. A time cap prevents a wedged/slow reader from stalling the pipeline forever
    // (we then credit anyway and let the congestion controller shed quality). No-op when GFX is active.
    private async Task AwaitBitmapDrainThenCreditAsync(CancellationToken ct)
    {
        if (_gfxActive) return;
        if (_bitmapCongestion == null) { ReleaseFrameCredit(); return; } // no signal → don't stall the loop
        const int capMs = 1000;
        long start = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            if (_bitmapCongestion() <= 0) break;
            if (Environment.TickCount64 - start >= capMs) break;
            await Task.Delay(4, ct);
        }
        ReleaseFrameCredit();
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
        catch (Exception ex) { _logger.LogDebug(ex, "Bridge: region flush failed"); }
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
        catch (Exception ex) { _logger.LogDebug(ex, "Bridge: pointer forward failed"); }
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
        // Sources that speak the same AT set-1 scancode set as the browser (SPICE) take scancodes
        // directly — no keysym round-trip, which avoids the layout re-derivation the VNC path needs.
        if (_source.PrefersScancodes)
        {
            try { _source.KeyScancodeAsync(scancode, extended, !released, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogDebug(ex, "SPICE: scancode forward failed"); }
            return;
        }
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
        catch (Exception ex) { _logger.LogDebug(ex, "Bridge: key forward failed"); }
    }
}
