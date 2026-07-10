using System.Diagnostics;

namespace KSol.ZeroVDI.RDP.Vnc;

/// <summary>
/// Real-time H.264 encoder for the RDPEGFX AVC420 path, backed by a per-session <c>ffmpeg</c>/libx264
/// subprocess (the gateway already ships ffmpeg for recording; macRDP's VideoToolbox encoder is macOS-
/// only). Feeds full-frame BGRA into ffmpeg's stdin as rawvideo and reads Annex-B H.264 access units from
/// stdout, one per input frame (SPS/PPS are prepended on IDR frames by the <c>global_header</c>-free
/// Annex-B muxer). Tuned <c>zerolatency</c> so one input frame ⇒ one output frame with no reorder delay.
///
/// AVC420 wants YUV420p at even dimensions; the caller sizes the surface to even width/height. Each output
/// access unit is handed to <see cref="OnFrame"/> ready to wrap in an RFX_AVC420_METABLOCK.
/// </summary>
internal sealed class H264Encoder : IAsyncDisposable
{
    private readonly int _width, _height, _fps;
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;
    private Process? _proc;
    private Task? _readTask;
    private readonly int _frameBytes;

    /// <summary>Raised with one Annex-B access unit (start-code delimited NAL units) per encoded frame.</summary>
    public event Action<byte[]>? OnFrame;

    public H264Encoder(int width, int height, int fps, string ffmpegPath, ILogger logger)
    {
        _width = width & ~1; _height = height & ~1; _fps = Math.Clamp(fps, 5, 60);
        _ffmpegPath = ffmpegPath; _logger = logger;
        _frameBytes = _width * _height * 4;
    }

    public int Width => _width;
    public int Height => _height;

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
            "-hide_banner", "-loglevel", "warning",
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
            "-x264-params", "cabac=1:level=4.2:slices=1:sliced-threads=0:keyint=120:scenecut=0:bframes=0:ref=1",
            "-bsf:v", "dump_extra",             // ensure SPS/PPS precede each IDR in the Annex-B stream
            "-f", "h264", "pipe:1",
        }) psi.ArgumentList.Add(a);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ffmpeg");
        _logger.LogInformation("VNC/H264: ffmpeg encoder started {W}x{H}@{F}", _width, _height, _fps);
        _ = DrainStderrAsync(_proc);
        _readTask = ReadAnnexBAsync(_proc);
    }

    /// <summary>Feeds one full-frame top-down BGRA buffer (length must be width*height*4).</summary>
    public async Task EncodeAsync(byte[] bgra, CancellationToken ct)
    {
        var p = _proc;
        if (p == null || p.HasExited) return;
        if (bgra.Length != _frameBytes) return;   // wrong geometry; skip
        try { await p.StandardInput.BaseStream.WriteAsync(bgra, ct); await p.StandardInput.BaseStream.FlushAsync(ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "VNC/H264: encode write failed"); }
    }

    // Reads Annex-B from ffmpeg stdout and splits it into access units, emitting one per frame. libx264 at
    // zerolatency + bframes=0 produces exactly one AU per input frame; we delimit AUs by the NAL sequence
    // (an access-unit boundary is the next SPS(7)/IDR(5)/non-IDR(1) slice at a start code). Simpler and
    // robust: accumulate and flush on each Access Unit Delimiter or when a new frame's first VCL NAL after
    // a picture is seen. We use a pragmatic split: emit whenever we see a start code preceded by a complete
    // prior AU — here we treat each read chunk that ends on a start-code boundary as frame-aligned, and
    // additionally cut at SPS (new IDR GOP). To keep latency at one frame, we flush on every start code
    // that begins a coded-slice NAL following at least one prior slice.
    private async Task ReadAnnexBAsync(Process p)
    {
        var stream = p.StandardOutput.BaseStream;
        var buf = new byte[64 * 1024];
        var acc = new List<byte>(1 << 20);
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf);
                if (n == 0) break;
                acc.AddRange(buf.AsSpan(0, n).ToArray());
                FlushCompleteFrames(acc);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "VNC/H264: reader ended"); }
        // Emit any trailing AU.
        if (acc.Count > 0) OnFrame?.Invoke(acc.ToArray());
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
                if (_logNal) _logger.LogInformation("VNC/H264: emit AU {N}B, NALs=[{Nals}]", frame.Length, NalTypes(frame));
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
            while ((line = await p.StandardError.ReadLineAsync()) != null)
                _logger.LogDebug("VNC/H264 ffmpeg: {Line}", line);
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
