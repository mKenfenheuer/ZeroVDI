using System.Diagnostics;

namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// Real-time H.264 encoder for the RDPEGFX AVC420 path, backed by a per-session <c>ffmpeg</c>/libx264
/// subprocess (the gateway already ships ffmpeg for recording; macRDP's VideoToolbox encoder is macOS-
/// only). Feeds full-frame BGRA into ffmpeg's stdin as rawvideo and reads Annex-B H.264 access units from
/// stdout, one per input frame (SPS/PPS are prepended on IDR frames by the <c>global_header</c>-free
/// Annex-B muxer). Tuned <c>zerolatency</c> so one input frame ⇒ one output frame with no reorder delay.
///
/// AVC420 wants YUV420p at even dimensions; the caller sizes the surface to even width/height. Each output
/// access unit is handed to <see cref="OnFrame"/> ready to wrap in an RFX_AVC420_METABLOCK.
///
/// This is the default <see cref="IH264Encoder"/>; the codec backend is exchangeable via
/// <see cref="IH264EncoderFactory"/> (see <see cref="FfmpegH264EncoderFactory"/>).
/// </summary>
internal sealed class FfmpegH264Encoder : IH264Encoder
{
    private readonly int _width, _height, _fps, _crf;
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;
    private Process? _proc;
    private Task? _readTask;
    private readonly int _frameBytes;

    /// <summary>Raised with one Annex-B access unit (start-code delimited NAL units) per encoded frame.</summary>
    public event Action<byte[]>? OnFrame;

    /// <param name="crf">x264 constant-rate-factor (quality/size knob): ~18 = visually lossless, ~28 =
    /// smaller/softer. The adaptive controller raises it on slow links. Clamped to a sane 16..40.</param>
    public FfmpegH264Encoder(int width, int height, int fps, string ffmpegPath, ILogger logger, int crf = 23)
    {
        _width = width & ~1; _height = height & ~1; _fps = Math.Clamp(fps, 5, 60);
        _crf = Math.Clamp(crf, 16, 40);
        _ffmpegPath = ffmpegPath; _logger = logger;
        _frameBytes = _width * _height * 4;
    }

    public int Width => _width;
    public int Height => _height;
    public int Crf => _crf;

    public void Start()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Raw BGRA in → baseline-ish H.264 Annex-B out, low-latency.
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "info",   // TEMP: was "warning" — see ffmpeg encode progress
            "-f", "rawvideo", "-pix_fmt", "bgra",
            "-s", $"{_width}x{_height}", "-r", _fps.ToString(),
            "-i", "pipe:0",
            "-pix_fmt", "yuv420p",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            // The browser's WebCodecs decoder is picky about the SPS the client derives its codec string
            // from ("avc1.PPCCLL" = profile/constraints/level):
            //   • Constrained Baseline (avc1.42c0..) → "encoding not supported". cabac=1 forces MAIN.
            //   • Main@L3.1 (avc1.4d401f) is ALSO rejected; only Main@L4.2 (avc1.4d402a) decodes.
            // So pin CABAC (→ Main profile) and level 4.2. `profile=` is not an x264-params key (it's an
            // ffmpeg option) — cabac=1 achieves Main without it.
            // slices=1: one VCL slice per picture. ultrafast/zerolatency otherwise enable multi-slicing,
            // which breaks our "next VCL slice = access-unit boundary" framing (it would cut mid-picture).
            // aud=1: emit an Access Unit Delimiter (NAL type 9) before each frame. This makes an AU boundary
            // detectable from a SINGLE frame (the trailing bytes up to the NEXT AUD, or end-of-stream on an
            // idle flush), so a lone frame on an idle desktop still ships instead of waiting for a 2nd frame.
            // threads=1: CRITICAL for latency. libx264 frame-threading buffers ~nproc frames before emitting
            // output; on a many-core box that means the first frame doesn't come out until ~nproc frames have
            // been fed — seconds of delay when we feed a slowly-changing desktop (the "first frame takes ~3s /
            // only after mouse move" bug). Single-threaded encode emits each frame immediately and is easily
            // fast enough (>500fps for this size here). Replaces the old sliced-threads=0 (which was itself
            // frame-threading and part of the same stall).
            "-x264-params", $"cabac=1:level=4.2:slices=1:threads=1:keyint=120:scenecut=0:bframes=0:ref=1:aud=1:crf={_crf}",
            "-bsf:v", "dump_extra",             // ensure SPS/PPS precede each IDR in the Annex-B stream
            "-f", "h264", "pipe:1",
        }) psi.ArgumentList.Add(a);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ffmpeg");
        _logger.LogInformation("Bridge/H264: ffmpeg encoder started {W}x{H}@{F}", _width, _height, _fps);
        _ = DrainStderrAsync(_proc);
        _readTask = ReadAnnexBAsync(_proc);
    }

    /// <summary>Feeds one full-frame top-down BGRA buffer (length must be width*height*4).</summary>
    private int _fedCount;
    public async Task EncodeAsync(byte[] bgra, CancellationToken ct)
    {
        var p = _proc;
        if (p == null || p.HasExited) { _logger.LogInformation("Bridge/H264: EncodeAsync skipped (proc null/exited)"); return; }
        if (bgra.Length != _frameBytes) { _logger.LogInformation("Bridge/H264: EncodeAsync skipped (len {L} != {E})", bgra.Length, _frameBytes); return; }
        try
        {
            await p.StandardInput.BaseStream.WriteAsync(bgra, ct);
            await p.StandardInput.BaseStream.FlushAsync(ct);
            int c = ++_fedCount;
            if (c <= 5) _logger.LogInformation("Bridge/H264: fed frame #{C} ({B}B) to ffmpeg stdin", c, bgra.Length);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Bridge/H264: encode write failed"); }
    }

    // Reads Annex-B from ffmpeg stdout and splits it into access units, emitting one per frame. libx264 at
    // zerolatency + bframes=0 produces exactly one AU per input frame; we delimit AUs by the NAL sequence
    // (an access-unit boundary is the next SPS(7)/IDR(5)/non-IDR(1) slice at a start code). Simpler and
    // robust: accumulate and flush on each Access Unit Delimiter or when a new frame's first VCL NAL after
    // a picture is seen. We use a pragmatic split: emit whenever we see a start code preceded by a complete
    // prior AU — here we treat each read chunk that ends on a start-code boundary as frame-aligned, and
    // additionally cut at SPS (new IDR GOP). To keep latency at one frame, we flush on every start code
    // that begins a coded-slice NAL following at least one prior slice.
    // Guards `_acc` and `_lastDataAt`, shared between the blocking reader and the idle-flush timer.
    private readonly object _accLock = new();
    private readonly List<byte> _acc = new(1 << 20);
    private long _lastDataAt;   // Environment.TickCount64 of the last stdout bytes

    private async Task ReadAnnexBAsync(Process p)
    {
        var stream = p.StandardOutput.BaseStream;
        var buf = new byte[64 * 1024];
        // The reader BLOCKS on ReadAsync (no per-read cancellation → no exceptions). A separate idle-flush
        // timer ships a lone complete AU when ffmpeg pauses (idle desktop), so the first/last frame of a
        // quiet period still reaches the client without waiting for a 2nd frame's start code.
        var idleFlush = IdleFlushLoopAsync(p);
        try
        {
            int reads = 0;
            while (true)
            {
                int n = await stream.ReadAsync(buf);
                if (++reads <= 5) _logger.LogInformation("Bridge/H264: stdout read #{R} = {N}B", reads, n);
                if (n == 0) break;
                lock (_accLock)
                {
                    _acc.AddRange(buf.AsSpan(0, n).ToArray());
                    _lastDataAt = Environment.TickCount64;
                    FlushCompleteFrames(_acc);
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Bridge/H264: reader ended"); }
        try { await idleFlush; } catch { }
        // Emit any trailing AU.
        lock (_accLock) { if (_acc.Count > 0 && HasVcl(_acc.ToArray())) { OnFrame?.Invoke(_acc.ToArray()); _acc.Clear(); } }
    }

    // Ships a pending complete AU when stdout has been quiet for >~50ms. Runs until ffmpeg exits.
    private async Task IdleFlushLoopAsync(Process p)
    {
        try
        {
            while (!p.HasExited)
            {
                await Task.Delay(30);
                lock (_accLock)
                {
                    if (_acc.Count == 0) continue;
                    if (Environment.TickCount64 - _lastDataAt < 50) continue; // still receiving this frame
                    var au = _acc.ToArray();
                    if (!HasVcl(au)) continue;   // partial / parameter-sets only — wait for the VCL slice
                    if (_logNal) _logger.LogInformation("Bridge/H264: emit AU (idle) {N}B, NALs=[{Nals}]", au.Length, NalTypes(au));
                    OnFrame?.Invoke(au);
                    _acc.Clear();
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Bridge/H264: idle-flush ended"); }
    }

    // Splits accumulated Annex-B into complete access units. Rule ([ITU-T H.264] 7.4.1.2.4): an access
    // unit boundary is a VCL slice NAL (type 1/5) that FOLLOWS a previous VCL slice — the leading
    // SPS(7)/PPS(8)/SEI(6)/AUD(9) NALs belong to the AU of the VCL slice that comes after them. So we emit
    // everything up to (not including) the 2nd-and-later VCL slice's start code, keeping SPS+PPS+IDR-slice
    // together as one AU. Anything after the last boundary stays buffered until its full AU arrives.
    private void FlushCompleteFrames(List<byte> data)
    {
        while (true)
        {
            // Find start-code offsets in the buffer.
            var starts = new List<(int off, int hdr)>();
            for (int i = 0; i + 3 < data.Count; i++)
            {
                bool sc3 = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1;
                bool sc4 = i + 4 < data.Count && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1;
                if (sc3) starts.Add((i, i + 3));
                else if (sc4) starts.Add((i, i + 4));
            }

            // The AU we emit must START at a start code. Find the first, and the boundary = the 2nd VCL
            // slice after it (start of the next access unit).
            if (starts.Count == 0 || starts[0].off > 0)
            {
                // Drop any leading garbage before the first start code (keeps us aligned).
                if (starts.Count == 0) return;
                data.RemoveRange(0, starts[0].off);
                continue;
            }
            int vclSeen = 0;
            int cut = -1;
            foreach (var (off, hdr) in starts)
            {
                if (hdr >= data.Count) break;
                int type = data[hdr] & 0x1f;
                bool isVcl = type == 1 || type == 5;
                if (isVcl)
                {
                    vclSeen++;
                    if (vclSeen == 2) { cut = off; break; }
                }
            }
            if (cut <= 0) return;                       // no complete AU yet

            var frame = data.GetRange(0, cut).ToArray();
            // Only emit AUs that actually contain a VCL slice; skip parameter-set-only or stub fragments.
            if (HasVcl(frame))
            {
                if (_logNal) _logger.LogInformation("Bridge/H264: emit AU {N}B, NALs=[{Nals}]", frame.Length, NalTypes(frame));
                OnFrame?.Invoke(frame);
            }
            data.RemoveRange(0, cut);                    // loop again in case several AUs are buffered
        }
    }

    private static bool HasVcl(byte[] au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
        {
            int hdr = -1;
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) hdr = i + 3;
            else if (i + 4 < au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1) hdr = i + 4;
            if (hdr >= 0 && hdr < au.Length) { int t = au[hdr] & 0x1f; if (t == 1 || t == 5) return true; }
        }
        return false;
    }

    private int _emitted;
    private bool _logNal => _emitted++ < 12;
    private static string NalTypes(byte[] au)
    {
        var types = new List<int>();
        for (int i = 0; i + 3 < au.Length; i++)
        {
            int hdr = -1;
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) hdr = i + 3;
            else if (i + 4 < au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1) hdr = i + 4;
            if (hdr >= 0 && hdr < au.Length) types.Add(au[hdr] & 0x1f);
        }
        return string.Join(",", types);
    }

    private async Task DrainStderrAsync(Process p)
    {
        try
        {
            string? line;
            // TEMP: surface ffmpeg's own output at Information so a fatal encoder error (which would explain
            // "nothing reached the client") is actually visible. Revert to LogDebug once diagnosed.
            while ((line = await p.StandardError.ReadLineAsync()) != null)
                _logger.LogInformation("Bridge/H264 ffmpeg: {Line}", line);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        try { _proc?.StandardInput.Close(); } catch { }
        if (_readTask != null) { try { await _readTask; } catch { } }
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
    }
}

/// <summary>
/// Default <see cref="IH264EncoderFactory"/>: hands out <see cref="FfmpegH264Encoder"/> instances backed by
/// the configured <c>ffmpeg</c> binary (reused from the recording pipeline). Registered in DI so the codec
/// backend is a one-line swap.
/// </summary>
internal sealed class FfmpegH264EncoderFactory : IH264EncoderFactory
{
    private readonly string _ffmpegPath;

    public FfmpegH264EncoderFactory(IConfiguration config)
    {
        // Reuse the recording ffmpeg for real-time H.264 encoding of the GFX path.
        _ffmpegPath = config["Recording:FfmpegPath"] ?? "ffmpeg";
    }

    public IH264Encoder Create(int width, int height, int fps, int crf, ILogger logger)
        => new FfmpegH264Encoder(width, height, fps, _ffmpegPath, logger, crf);
}
