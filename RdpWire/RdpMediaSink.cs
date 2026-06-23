// IRdpMediaSink — media-extraction hook for session recording.
//
// The structural decoder (RdpSession / RdpChannels) emits a Node tree for diagnostics, but the Node
// tree deliberately does NOT carry codec bitstreams or PCM samples (WIRE_TO_SURFACE_1 logs only the
// opaque length; rdpsnd logs only msgType/bodySize). A recorder needs the actual bytes, so RdpSession
// optionally takes an IRdpMediaSink and the decoders push extracted media through it as they parse —
// reusing the existing DVC reassembly + ZGFX inflation, without disturbing the diagnostic path.
//
// All callbacks carry a monotonic timestamp in milliseconds (RdpSession.ElapsedMs). The implementation
// (the gateway's SessionRecorder) muxes these to MP4 via ffmpeg. RdpWire itself stays a pure decoder:
// no ffmpeg, no file or process I/O.

namespace KSol.RDPGateway.RDP;

/// <summary>Negotiated uncompressed-PCM audio format (rdpsnd / audin advertise WAVE_FORMAT_PCM only).</summary>
public readonly record struct PcmFormat(int SampleRate, int BitsPerSample, int Channels);

/// <summary>
/// GFX video codecs we can extract. Recording remuxes the H.264 variants (AVC*) without decoding; other
/// codecs (ClearCodec, Progressive, planar, uncompressed) have no server-side pixel decoder and are
/// surfaced as "unsupported" so the recorder records audio only.
/// </summary>
public enum GfxVideoCodec
{
    Other = 0,
    Avc420 = 0x0b,
    Avc444 = 0x0e,
    Avc444v2 = 0x0f,
}

/// <summary>
/// Receives media extracted from the decrypted RDP stream during a recorded session. Implementations
/// must be cheap/non-blocking on these calls (they run on the recorder's single decode task); heavy
/// work (ffmpeg writes) should be queued.
/// </summary>
public interface IRdpMediaSink
{
    /// <summary>
    /// A desktop video access unit from a GFX WIRE_TO_SURFACE_1 PDU (server→client). For AVC420 this is
    /// a complete H.264 Annex-B access unit; for AVC444/AVC444v2 it is the main (luma, 4:2:0) view —
    /// <paramref name="auxBitstream"/> carries the chroma-aux sub-stream when present (v1 records the
    /// main view only). <paramref name="frameId"/>/<paramref name="frameTimestampMs"/> come from the
    /// enclosing START_FRAME when known.
    /// </summary>
    void OnDesktopVideo(GfxVideoCodec codec, int surfaceId, ReadOnlySpan<byte> bitstream,
        ReadOnlySpan<byte> auxBitstream, long frameId, long timestampMs);

    /// <summary>Remote desktop sound: a chunk of uncompressed PCM (rdpsnd, server→client).</summary>
    void OnRemoteAudio(PcmFormat format, ReadOnlySpan<byte> pcm, long timestampMs);

    /// <summary>Microphone PCM redirected to the host (audin, client→server).</summary>
    void OnMicAudio(PcmFormat format, ReadOnlySpan<byte> pcm, long timestampMs);

    /// <summary>A camera video frame redirected to the host (RDPECAM, client→server).</summary>
    void OnCameraFrame(int width, int height, ReadOnlySpan<byte> frame, string mediaFormat, long timestampMs);

    /// <summary>
    /// Low-volume diagnostic breadcrumbs from the media decoders (DVC channel creates, audio message
    /// types) so a recording that captured no audio/camera can be diagnosed from the logs. Default no-op;
    /// must stay cheap (the recorder just LogDebug's it). NOT for per-frame data.
    /// </summary>
    void OnDiagnostic(string message) { }
}
