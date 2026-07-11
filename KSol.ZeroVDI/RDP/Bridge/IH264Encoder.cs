namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// A real-time H.264 encoder for the RDPEGFX AVC420 path. Implementations turn a stream of full-frame
/// top-down BGRA buffers into Annex-B H.264 access units (one per input frame, SPS/PPS on IDR), each raised
/// on <see cref="OnFrame"/> ready to wrap in an RFX_AVC420_METABLOCK.
///
/// This is the seam that makes the codec exchangeable: the shared <see cref="RdpEncoderSession"/> talks only
/// to this interface and obtains instances from an <see cref="IH264EncoderFactory"/>, so an alternative
/// backend (VideoToolbox, NVENC, an in-process libx264 binding, …) can be swapped in via DI without touching
/// the session. <see cref="FfmpegH264Encoder"/> is the default, ffmpeg/libx264-backed implementation.
/// </summary>
public interface IH264Encoder : IAsyncDisposable
{
    /// <summary>Raised with one Annex-B access unit (start-code delimited NAL units) per encoded frame.</summary>
    event Action<byte[]>? OnFrame;

    /// <summary>Encoded picture width (always even).</summary>
    int Width { get; }
    /// <summary>Encoded picture height (always even).</summary>
    int Height { get; }
    /// <summary>The quality knob this encoder was created with (x264 CRF), for the adaptive controller.</summary>
    int Crf { get; }

    /// <summary>Spins up the encoder and begins draining its output to <see cref="OnFrame"/>.</summary>
    void Start();

    /// <summary>Feeds one full-frame top-down BGRA buffer (length must be <see cref="Width"/>*<see cref="Height"/>*4).</summary>
    Task EncodeAsync(byte[] bgra, CancellationToken ct);
}

/// <summary>
/// Creates <see cref="IH264Encoder"/> instances for a target size/framerate/quality. Registered in DI so the
/// concrete codec backend is a one-line swap; the shared <see cref="RdpEncoderSession"/> depends on this
/// factory rather than on any concrete encoder or on the ffmpeg path.
/// </summary>
public interface IH264EncoderFactory
{
    IH264Encoder Create(int width, int height, int fps, int crf, ILogger logger);
}
