using System.Text.Json;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Captures the media extracted from the decrypted RDP stream (via <see cref="IRdpMediaSink"/>) to RAW
/// elementary-stream files during the session — no ffmpeg, no pipes, no process. A separate background
/// job (<see cref="RecordingMuxService"/>) muxes these into MP4 after the session ends, using the
/// <c>manifest.json</c> sidecar this writes (audio format, camera geometry, observed codec).
///
/// Rationale: muxing two live streams through one ffmpeg required FIFOs + a launch-timing race (ffmpeg
/// fixes input formats at parse time and probes the video input before opening audio). Capturing raw
/// files and muxing afterward removes all of that: the live path is just buffered file appends (cheap,
/// crash-resilient — partial raws still mux), and the mux step reads real seekable files with the audio
/// format already known.
///
/// Files under the recording's base dir:
///   desktop.h264  — concatenated H.264 Annex-B access units (AVC420 / AVC444 main view)
///   desktop.pcm   — remote-sound PCM (rdpsnd)
///   camera.nv12   — raw NV12 camera frames (RDPECAM)
///   mic.pcm       — microphone PCM (audin)
///   manifest.json — formats/geometry/codec for the mux step
///
/// Writes run on the recorder's single decode task (off the relay hot path). Each stream is buffered
/// (FileStream) and flushed on dispose.
/// </summary>
public sealed class SessionRecorder : IRdpMediaSink, IDisposable
{
    private readonly string _baseDir;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private bool _disposed;

    // Lazily-opened raw output streams.
    private FileStream? _desktopVideo;
    private FileStream? _desktopAudio;
    private FileStream? _cameraVideo;
    private FileStream? _micAudio;

    // Monotonic arrival clock. The GFX START_FRAME wire timestamp (passed as timestampMs) is NOT a
    // reliable monotonic PTS — it can repeat/regress across frames (observed: first two frames both
    // ts=0), which mkvmerge's v2 timestamp format rejects ("timestamps not ordered"). The relay only ever
    // sees ARRIVAL time anyway, so we stamp every sample with our own stopwatch — monotonic by
    // construction — and use that for both the per-frame video timeline and the audio offsets.
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // Per-frame video timestamp sidecars (mkvmerge "v2" format: one ms timestamp per frame, strictly
    // increasing). The mux applies these to the copied elementary streams → true variable frame rate.
    private StreamWriter? _desktopVideoTs;
    private StreamWriter? _cameraVideoTs;
    private long _desktopVideoFirstMs = -1, _desktopVideoLastTs = -1;
    private long _cameraVideoFirstMs = -1, _cameraVideoLastTs = -1;

    // Audio timing (first arrival ms) so the mux can silence-pad the front to align with video start.
    private long _epochMs = -1;                          // first media arrival across the whole session
    private readonly Timing _desktopAudioT = new();
    private readonly Timing _micAudioT = new();

    private sealed class Timing
    {
        public long FirstMs = -1, LastMs = -1, Count, Bytes;
        public void Note(long ms, int len) { if (FirstMs < 0) FirstMs = ms; LastMs = ms; Count++; Bytes += len; }
    }

    // Observed stream parameters (written to the manifest for the mux step).
    private PcmFormat? _remoteAudioFmt;
    private PcmFormat? _micFmt;
    private (int w, int h, int fps)? _cameraGeom;
    // Black NV12 frame (Y=0x00, UV=0x80) sized to the camera geometry, lazily built, reused to pad
    // inactive periods. Cadence: one black frame per ~200ms of gap (5 fps) — plenty for "no camera".
    private byte[]? _blackNv12;
    private long _cameraLastEpochMs = -1;     // epoch-relative ms of the last camera frame WRITTEN (real or black)
    private const long BlackPadCadenceMs = 200;
    private bool _loggedFirstVideo;
    private bool _anyAvc;            // got at least one H.264 (AVC) desktop frame
    private bool _anyOtherCodec;     // saw at least one non-AVC desktop frame
    private bool _loggedUnsupported;

    /// <summary>
    /// True only when the desktop had video but NONE of it was H.264 (e.g. pure ClearCodec/Progressive) —
    /// i.e. we can't produce desktop video. A mix of AVC + non-AVC is NOT unsupported (we keep the AVC).
    /// </summary>
    public bool UnsupportedVideoCodec => _anyOtherCodec && !_anyAvc;
    /// <summary>The desktop video codec observed (for the Recording row), or null if none.</summary>
    public string? VideoCodec { get; private set; }
    /// <summary>True if any desktop media (video or audio) was captured.</summary>
    public bool HasDesktop { get; private set; }
    /// <summary>True if any camera media (video or mic) was captured.</summary>
    public bool HasCamera { get; private set; }

    public SessionRecorder(string baseDir, ILogger logger)
    {
        _baseDir = baseDir;
        _logger = logger;
        Directory.CreateDirectory(baseDir);
    }

    private string Path_(string name) => System.IO.Path.Combine(_baseDir, name);

    public void OnDesktopVideo(GfxVideoCodec codec, int surfaceId, ReadOnlySpan<byte> bitstream,
        ReadOnlySpan<byte> auxBitstream, long frameId, long timestampMs)
    {
        if (_disposed) return;
        if (codec == GfxVideoCodec.Other)
        {
            // This host interleaves AVC444v2 keyframes with codecId=0x0000 UNCOMPRESSED (and other
            // non-AVC) delta rects we can't remux. Only treat the session as unsupported if we NEVER get
            // an AVC frame; once we have H.264, just skip the non-AVC frames and keep recording (the
            // recording is then slightly incomplete on those deltas, but usable — far better than nothing).
            _anyOtherCodec = true;
            if (!_anyAvc && !_loggedUnsupported)
            {
                _loggedUnsupported = true;
                _logger.LogWarning("SessionRecorder: non-H.264 GFX codec (no AVC yet) — desktop video skipped so far");
            }
            return;
        }
        if (bitstream.IsEmpty) return;
        _anyAvc = true;
        VideoCodec ??= codec.ToString();
        if (!_loggedFirstVideo)
        {
            _loggedFirstVideo = true;
            var head = bitstream.Slice(0, Math.Min(16, bitstream.Length));
            _logger.LogInformation("SessionRecorder: first {Codec} AU {Len}B head={Head}", codec, bitstream.Length, Convert.ToHexString(head));
        }
        // v1: record only the main (4:2:0) view; the AVC444 chroma-aux substream is dropped. Use our
        // monotonic arrival clock (the passed wire timestamp isn't a reliable monotonic PTS).
        long now = _clock.ElapsedMilliseconds;
        NoteEpoch(now);
        if (Append(ref _desktopVideo, "desktop.h264", bitstream))
        {
            HasDesktop = true;
            WriteFrameTs(ref _desktopVideoTs, "desktop.ts.txt", ref _desktopVideoFirstMs, ref _desktopVideoLastTs, now);
        }
    }

    public void OnRemoteAudio(PcmFormat format, ReadOnlySpan<byte> pcm, long timestampMs)
    {
        if (_disposed || pcm.IsEmpty) return;
        _remoteAudioFmt ??= format;
        long now = _clock.ElapsedMilliseconds;
        NoteEpoch(now);
        if (Append(ref _desktopAudio, "desktop.pcm", pcm)) { HasDesktop = true; _desktopAudioT.Note(now, pcm.Length); }
    }

    public void OnMicAudio(PcmFormat format, ReadOnlySpan<byte> pcm, long timestampMs)
    {
        if (_disposed || pcm.IsEmpty) return;
        _micFmt ??= format;
        long now = _clock.ElapsedMilliseconds;
        NoteEpoch(now);
        if (Append(ref _micAudio, "mic.pcm", pcm)) { HasCamera = true; _micAudioT.Note(now, pcm.Length); }
    }

    public void OnCameraFrame(int width, int height, ReadOnlySpan<byte> frame, string mediaFormat, long timestampMs)
    {
        if (_disposed || frame.IsEmpty) return;
        // Only raw NV12 is muxable without a decoder. Other formats (mjpeg/h264/i420/rgb) are skipped in v1.
        if (mediaFormat != "nv12") return;
        _cameraGeom ??= (width, height, 30);
        long now = _clock.ElapsedMilliseconds;
        NoteEpoch(now);
        if (Append(ref _cameraVideo, "camera.nv12", frame))
        {
            HasCamera = true;
            WriteFrameTs(ref _cameraVideoTs, "camera.ts.txt", ref _cameraVideoFirstMs, ref _cameraVideoLastTs, now);
        }
    }

    // Diagnostic breadcrumbs from the decoder (DVC creates, audio msg types). Deduped to "first prefix"
    // so a high-rate stream (audin DATA) logs once, not per packet — enough to tell us a channel was
    // created and a message type arrived when a recording captured no audio.
    private readonly HashSet<string> _seenDiag = new();
    public void OnDiagnostic(string message)
    {
        if (_disposed) return;
        // Key on text up to the first ' len=' so per-packet length variation collapses to one log line.
        int cut = message.IndexOf(" len=", StringComparison.Ordinal);
        string key = cut > 0 ? message.Substring(0, cut) : message;
        bool isNew;
        lock (_lock) { isNew = _seenDiag.Add(key); }
        if (isNew) _logger.LogDebug("SessionRecorder media diag: {Message}", message);
    }

    private void NoteEpoch(long ms) { if (_epochMs < 0) _epochMs = ms; }

    // Append one frame's presentation time (ms, relative to this stream's first frame) to its mkvmerge
    // v2 timestamp sidecar. v2 requires STRICTLY INCREASING timestamps, so if two frames land in the same
    // (or an earlier) ms we bump to lastTs+1 — monotonicity matters more than sub-ms precision here.
    private void WriteFrameTs(ref StreamWriter? ts, string name, ref long firstMs, ref long lastTs, long ms)
    {
        try
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (ts == null) { ts = new StreamWriter(Path_(name)); ts.WriteLine("# timestamp format v2"); firstMs = ms; lastTs = -1; }
                long rel = Math.Max(0, ms - firstMs);
                if (rel <= lastTs) rel = lastTs + 1;
                lastTs = rel;
                ts.WriteLine(rel);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "SessionRecorder: writing {Name} failed", name); }
    }

    // Append to a lazily-opened raw file. Errors fault that stream (set to null-ish) but never throw to
    // the decode task — recording is best-effort.
    private bool Append(ref FileStream? stream, string name, ReadOnlySpan<byte> data)
    {
        try
        {
            lock (_lock)
            {
                if (_disposed) return false;
                stream ??= new FileStream(Path_(name), FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
                stream.Write(data);
            }
            return true;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "SessionRecorder: append to {Name} failed", name); return false; }
    }

    /// <summary>Closes the raw streams and writes manifest.json describing what was captured.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in new Stream?[] { _desktopVideo, _desktopAudio, _cameraVideo, _micAudio })
            { try { s?.Flush(); s?.Dispose(); } catch { } }
            try { _desktopVideoTs?.Flush(); _desktopVideoTs?.Dispose(); } catch { }
            try { _cameraVideoTs?.Flush(); _cameraVideoTs?.Dispose(); } catch { }
        }

        try
        {
            var manifest = new RecordingManifest
            {
                VideoCodec = VideoCodec,
                UnsupportedVideoCodec = UnsupportedVideoCodec,
                DesktopVideo = _desktopVideo != null ? "desktop.h264" : null,
                DesktopVideoTs = _desktopVideoTs != null ? "desktop.ts.txt" : null,
                DesktopAudio = _desktopAudio != null ? "desktop.pcm" : null,
                CameraVideo = _cameraVideo != null ? "camera.nv12" : null,
                CameraVideoTs = _cameraVideoTs != null ? "camera.ts.txt" : null,
                MicAudio = _micAudio != null ? "mic.pcm" : null,
                RemoteAudio = _remoteAudioFmt,
                MicFormat = _micFmt,
                CameraWidth = _cameraGeom?.w ?? 0,
                CameraHeight = _cameraGeom?.h ?? 0,
                // Audio start offsets so the mux silence-pads the front to align with the video start:
                // desktop audio → session epoch (== desktop video start); mic → camera video start.
                DesktopAudioOffsetMs = _desktopAudioT.FirstMs >= 0 && _epochMs >= 0 ? Math.Max(0, _desktopAudioT.FirstMs - _epochMs) : 0,
                MicOffsetMs = _micAudioT.FirstMs >= 0 && _cameraVideoFirstMs >= 0 ? Math.Max(0, _micAudioT.FirstMs - _cameraVideoFirstMs)
                            : _micAudioT.FirstMs >= 0 && _epochMs >= 0 ? Math.Max(0, _micAudioT.FirstMs - _epochMs) : 0,
            };
            File.WriteAllText(Path_("manifest.json"), JsonSerializer.Serialize(manifest));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SessionRecorder: writing manifest failed"); }
    }
}

/// <summary>
/// Sidecar describing the raw streams a <see cref="SessionRecorder"/> captured, consumed by
/// <see cref="RecordingMuxService"/> to build the MP4(s).
/// </summary>
public sealed class RecordingManifest
{
    public string? VideoCodec { get; set; }
    public bool UnsupportedVideoCodec { get; set; }
    public string? DesktopVideo { get; set; }   // relative file name, or null if absent
    public string? DesktopVideoTs { get; set; } // mkvmerge v2 per-frame timestamp sidecar
    public string? DesktopAudio { get; set; }
    public string? CameraVideo { get; set; }
    public string? CameraVideoTs { get; set; }
    public string? MicAudio { get; set; }
    public PcmFormat? RemoteAudio { get; set; }
    public PcmFormat? MicFormat { get; set; }
    public int CameraWidth { get; set; }
    public int CameraHeight { get; set; }
    /// <summary>Milliseconds of silence to prepend to the desktop audio so it aligns with the video start.</summary>
    public long DesktopAudioOffsetMs { get; set; }
    /// <summary>Milliseconds of silence to prepend to the mic audio so it aligns with the camera video start.</summary>
    public long MicOffsetMs { get; set; }
}
