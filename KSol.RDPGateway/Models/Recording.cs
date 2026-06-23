using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.RDPGateway.Models;

public enum RecordingStatus
{
    /// <summary>Capture in progress (raw elementary streams being written).</summary>
    Recording = 0,
    /// <summary>Session ended, files finalized.</summary>
    Completed = 1,
    /// <summary>The recorder failed (ffmpeg unavailable, write error).</summary>
    Failed = 2,
    /// <summary>
    /// The desktop GFX codec was not H.264 (e.g. ClearCodec/Progressive/legacy bitmaps), which the
    /// gateway cannot remux without a pixel decoder. Audio (and camera/mic) were still recorded.
    /// </summary>
    UnsupportedCodec = 3,
    /// <summary>Session ended; raw streams captured and awaiting/undergoing background muxing to MP4.</summary>
    Processing = 4,
}

/// <summary>
/// One recorded console session. Produces up to four separate, individually-seekable files — desktop
/// video (desktop.mp4), camera video (camera.mp4), remote audio (audio.m4a), and microphone audio
/// (mic.m4a) — that share one session-epoch timeline so the web player can sync them. The files live
/// under the configured recordings directory, outside wwwroot, and are served only through the
/// authorized RecordingsController.GetFile action (one per <c>track</c>).
/// </summary>
public class Recording
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string? UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public ApplicationUser? User { get; set; }

    public string? RDPResourceId { get; set; }
    [ForeignKey(nameof(RDPResourceId))]
    public RDPResource? RDPResource { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }

    public RecordingStatus Status { get; set; } = RecordingStatus.Recording;

    /// <summary>Path to the desktop video MP4 (h264), or null if not produced.</summary>
    public string? DesktopFilePath { get; set; }

    /// <summary>Path to the camera video MP4 (h264), or null if no camera was recorded.</summary>
    public string? CameraFilePath { get; set; }

    /// <summary>Path to the remote (desktop) audio M4A (aac), or null if no remote audio was recorded.</summary>
    public string? AudioFilePath { get; set; }

    /// <summary>Path to the microphone audio M4A (aac), or null if no mic audio was recorded.</summary>
    public string? MicFilePath { get; set; }

    /// <summary>The desktop video codec observed (e.g. "AVC420", "AVC444v2", or "none").</summary>
    public string? VideoCodec { get; set; }
}
