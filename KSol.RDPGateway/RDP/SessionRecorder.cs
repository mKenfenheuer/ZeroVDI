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

    // Per-stream timing, used by the mux step to reconstruct the real timeline (silence-pad audio gaps,
    // derive the true camera frame rate) instead of packing samples back-to-back. We track first/last
    // timestamps and counts; per-chunk audio gap handling uses firstTs as the offset and total span.
    private long _epochMs = -1;                          // first media timestamp across the whole session
    private readonly Timing _desktopAudioT = new();
    private readonly Timing _micAudioT = new();
    private readonly Timing _cameraT = new();

    private sealed class Timing
    {
        public long FirstMs = -1, LastMs = -1, Count, Bytes;
        public void Note(long ms, int len) { if (FirstMs < 0) FirstMs = ms; LastMs = ms; Count++; Bytes += len; }
    }

    // Observed stream parameters (written to the manifest for the mux step).
    private PcmFormat? _remoteAudioFmt;
    private PcmFormat? _micFmt;
    private (int w, int h, int fps)? _cameraGeom;
    private bool _loggedFirstVideo;

    /// <summary>Set when a desktop video unit used a codec we can't remux (non-H.264).</summary>
    public bool UnsupportedVideoCodec { get; private set; }
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
            if (!UnsupportedVideoCodec)
            {
                UnsupportedVideoCodec = true;
                VideoCodec ??= "unsupported";
                _logger.LogWarning("SessionRecorder: non-H.264 GFX codec — desktop video not recorded (audio only)");
            }
            return;
        }
        if (bitstream.IsEmpty) return;
        VideoCodec ??= codec.ToString();
        if (!_loggedFirstVideo)
        {
            _loggedFirstVideo = true;
            var head = bitstream.Slice(0, Math.Min(16, bitstream.Length));
            _logger.LogInformation("SessionRecorder: first {Codec} AU {Len}B head={Head}", codec, bitstream.Length, Convert.ToHexString(head));
        }
        // v1: record only the main (4:2:0) view; the AVC444 chroma-aux substream is dropped.
        NoteEpoch(timestampMs);
        if (Append(ref _desktopVideo, "desktop.h264", bitstream)) HasDesktop = true;
    }

    public void OnRemoteAudio(PcmFormat format, ReadOnlySpan<byte> pcm, long timestampMs)
    {
        if (_disposed || pcm.IsEmpty) return;
        _remoteAudioFmt ??= format;
        NoteEpoch(timestampMs);
        if (Append(ref _desktopAudio, "desktop.pcm", pcm)) { HasDesktop = true; _desktopAudioT.Note(timestampMs, pcm.Length); }
    }

    public void OnMicAudio(PcmFormat format, ReadOnlySpan<byte> pcm, long timestampMs)
    {
        if (_disposed || pcm.IsEmpty) return;
        _micFmt ??= format;
        NoteEpoch(timestampMs);
        if (Append(ref _micAudio, "mic.pcm", pcm)) { HasCamera = true; _micAudioT.Note(timestampMs, pcm.Length); }
    }

    public void OnCameraFrame(int width, int height, ReadOnlySpan<byte> frame, string mediaFormat, long timestampMs)
    {
        if (_disposed || frame.IsEmpty) return;
        // Only raw NV12 is muxable without a decoder. Other formats (mjpeg/h264/i420/rgb) are skipped in v1.
        if (mediaFormat != "nv12") return;
        _cameraGeom ??= (width, height, 30);
        NoteEpoch(timestampMs);
        if (Append(ref _cameraVideo, "camera.nv12", frame)) { HasCamera = true; _cameraT.Note(timestampMs, frame.Length); }
    }

    private void NoteEpoch(long ms) { if (_epochMs < 0) _epochMs = ms; }

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
            foreach (var s in new[] { _desktopVideo, _desktopAudio, _cameraVideo, _micAudio })
            { try { s?.Flush(); s?.Dispose(); } catch { } }
        }

        try
        {
            // Real camera frame rate from the timeline (the host samples the camera at irregular, often
            // sub-30 intervals; muxing those at a fixed 30fps plays the video too fast). fps = (n-1)/span.
            double camFps = _cameraGeom?.fps ?? 30;
            if (_cameraT.Count > 1 && _cameraT.LastMs > _cameraT.FirstMs)
            {
                camFps = (_cameraT.Count - 1) * 1000.0 / (_cameraT.LastMs - _cameraT.FirstMs);
                camFps = Math.Clamp(camFps, 1, 60);
            }

            var manifest = new RecordingManifest
            {
                VideoCodec = VideoCodec,
                UnsupportedVideoCodec = UnsupportedVideoCodec,
                DesktopVideo = _desktopVideo != null ? "desktop.h264" : null,
                DesktopAudio = _desktopAudio != null ? "desktop.pcm" : null,
                CameraVideo = _cameraVideo != null ? "camera.nv12" : null,
                MicAudio = _micAudio != null ? "mic.pcm" : null,
                RemoteAudio = _remoteAudioFmt,
                MicFormat = _micFmt,
                CameraWidth = _cameraGeom?.w ?? 0,
                CameraHeight = _cameraGeom?.h ?? 0,
                CameraFps = camFps,
                // Audio start offsets so the mux can silence-pad the front (and gaps): desktop audio is
                // aligned to the session epoch (== desktop video start); mic is aligned to the camera
                // video start (the two go in the camera file together).
                DesktopAudioOffsetMs = _desktopAudioT.FirstMs >= 0 && _epochMs >= 0 ? Math.Max(0, _desktopAudioT.FirstMs - _epochMs) : 0,
                MicOffsetMs = _micAudioT.FirstMs >= 0 && _cameraT.FirstMs >= 0 ? Math.Max(0, _micAudioT.FirstMs - _cameraT.FirstMs)
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
    public string? DesktopAudio { get; set; }
    public string? CameraVideo { get; set; }
    public string? MicAudio { get; set; }
    public PcmFormat? RemoteAudio { get; set; }
    public PcmFormat? MicFormat { get; set; }
    public int CameraWidth { get; set; }
    public int CameraHeight { get; set; }
    /// <summary>Real (measured) camera frame rate, derived from frame timestamps.</summary>
    public double CameraFps { get; set; }
    /// <summary>Milliseconds of silence to prepend to the desktop audio so it aligns with the video start.</summary>
    public long DesktopAudioOffsetMs { get; set; }
    /// <summary>Milliseconds of silence to prepend to the mic audio so it aligns with the camera video start.</summary>
    public long MicOffsetMs { get; set; }
}
