using System.Diagnostics;
using System.Text.Json;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Background job that muxes the RAW elementary streams captured by <see cref="SessionRecorder"/> into
/// the final files. Polls for recordings in <see cref="RecordingStatus.Processing"/>, reads the
/// per-recording <c>manifest.json</c>, and produces up to FOUR independent, individually-seekable
/// faststart files that share one session-epoch timeline (reading real seekable files, so no FIFOs /
/// launch-timing races) — one per track so the web player can load and sync them separately:
///   • desktop video — desktop.h264 → desktop.mp4 (-c:v copy, VFR via per-frame timestamps)
///   • camera video  — camera.nv12 → camera.mp4  (-c:v libx264, VFR via per-frame timestamps)
///   • remote audio  — desktop.pcm → audio.m4a   (→ aac, silence-padded to its session offset)
///   • mic audio     — mic.pcm     → mic.m4a      (→ aac, silence-padded to its session offset)
/// On success the raw files are deleted and the row is set Completed (or UnsupportedCodec when the
/// desktop codec wasn't H.264 — audio only). Failures set Failed and KEEP the raws for retry/debugging.
/// </summary>
public sealed class RecordingMuxService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<RecordingMuxService> _logger;
    private readonly string _ffmpeg;
    private readonly string _mkvmerge;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public RecordingMuxService(IServiceScopeFactory scopes, IConfiguration config, ILogger<RecordingMuxService> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
        _ffmpeg = config["Recording:FfmpegPath"] ?? "ffmpeg";
        _mkvmerge = config["Recording:MkvmergePath"] ?? "mkvmerge";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Small poll loop. Recordings are produced only when sessions end, so a few-second cadence is
        // plenty; processing is idempotent per recording (claimed by flipping status off Processing).
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessPendingAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "RecordingMuxService: poll iteration failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Claim one at a time (keeps memory flat; muxing is CPU-bound and we don't want N ffmpegs at once).
        var rec = await db.Recordings.FirstOrDefaultAsync(r => r.Status == RecordingStatus.Processing, ct);
        if (rec == null) return;

        var baseDir = rec.DesktopFilePath != null ? Path.GetDirectoryName(rec.DesktopFilePath) : null;
        baseDir ??= rec.CameraFilePath != null ? Path.GetDirectoryName(rec.CameraFilePath) : null;
        if (baseDir == null || !File.Exists(Path.Combine(baseDir, "manifest.json")))
        {
            _logger.LogWarning("RecordingMuxService: recording {Id} has no manifest — marking Failed", rec.Id);
            rec.Status = RecordingStatus.Failed;
            await db.SaveChangesAsync(ct);
            return;
        }

        RecordingManifest manifest;
        try { manifest = JsonSerializer.Deserialize<RecordingManifest>(await File.ReadAllTextAsync(Path.Combine(baseDir, "manifest.json"), ct), JsonOpts)!; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RecordingMuxService: bad manifest for {Id} — marking Failed", rec.Id);
            rec.Status = RecordingStatus.Failed;
            await db.SaveChangesAsync(ct);
            return;
        }

        _logger.LogInformation("RecordingMuxService: muxing recording {Id}", rec.Id);
        bool anyFailed = false;
        var rawFiles = new List<string>();
        var tmpFiles = new List<string>();

        // Build the (up to) two VFR video tracks as MKVs (H.264 copy + per-frame timestamps), then mux
        // everything — both videos + remote audio + mic audio — into ONE MP4 with four separate tracks.

        // --- desktop video → VFR MKV (copy) ---
        string? desktopMkv = null;
        if (manifest.DesktopVideo != null)
        {
            string vIn = Path.Combine(baseDir, manifest.DesktopVideo);
            string? tsIn = manifest.DesktopVideoTs != null ? Path.Combine(baseDir, manifest.DesktopVideoTs) : null;
            if (File.Exists(vIn))
            {
                rawFiles.Add(vIn);
                if (tsIn != null) rawFiles.Add(tsIn);
                desktopMkv = await BuildVfrMkvAsync(vIn, tsIn, rec.Id, "desktop", tmpFiles, ct);
                if (desktopMkv == null) anyFailed = true;
            }
        }

        // --- camera video (NV12 → encoded H.264) → VFR MKV ---
        string? cameraMkv = null;
        if (manifest.CameraVideo != null && manifest.CameraWidth > 0 && manifest.CameraHeight > 0)
        {
            string vIn = Path.Combine(baseDir, manifest.CameraVideo);
            string? tsIn = manifest.CameraVideoTs != null ? Path.Combine(baseDir, manifest.CameraVideoTs) : null;
            if (File.Exists(vIn))
            {
                rawFiles.Add(vIn);
                if (tsIn != null) rawFiles.Add(tsIn);
                // NV12 has no encoded form mkvmerge can take, so encode to elementary H.264 first (frame
                // order preserved, 1:1), then apply the timestamps for VFR.
                string camH264 = Path.Combine(baseDir, "camera.enc.h264");
                tmpFiles.Add(camH264);
                var enc = new List<string> { "-hide_banner", "-loglevel", "warning",
                    "-f", "rawvideo", "-pix_fmt", "nv12", "-s", $"{manifest.CameraWidth}x{manifest.CameraHeight}",
                    "-i", vIn, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-bsf:v", "h264_mp4toannexb",
                    "-f", "h264", "-y", camH264 };
                if (!await RunFfmpegAsync(enc, rec.Id, "camera-enc", ct)) anyFailed = true;
                else { cameraMkv = await BuildVfrMkvAsync(camH264, tsIn, rec.Id, "camera", tmpFiles, ct); if (cameraMkv == null) anyFailed = true; }
            }
        }

        // --- audio raws ---
        string? remotePcm = manifest.DesktopAudio != null && manifest.RemoteAudio != null
            ? Path.Combine(baseDir, manifest.DesktopAudio) : null;
        if (remotePcm != null && File.Exists(remotePcm)) rawFiles.Add(remotePcm); else remotePcm = null;
        string? micPcm = manifest.MicAudio != null && manifest.MicFormat != null
            ? Path.Combine(baseDir, manifest.MicAudio) : null;
        if (micPcm != null && File.Exists(micPcm)) rawFiles.Add(micPcm); else micPcm = null;

        // --- four independent, individually-seekable outputs sharing one session-epoch timeline ---
        // desktop.mp4 / camera.mp4 (video copy from the VFR MKVs), audio.m4a / mic.m4a (PCM → aac,
        // each silence-padded by its session-epoch offset so playback starts at the right moment). The
        // web player loads each as its own element and syncs them by currentTime.
        string? desktopOut = null, cameraOut = null, audioOut = null, micOut = null;
        if (!anyFailed)
        {
            if (desktopMkv != null)
            {
                desktopOut = Path.Combine(baseDir, "desktop.mp4");
                if (!await MuxVideoFileAsync(desktopMkv, desktopOut, rec.Id, "desktop", ct)) { anyFailed = true; desktopOut = null; }
            }
            if (!anyFailed && cameraMkv != null)
            {
                cameraOut = Path.Combine(baseDir, "camera.mp4");
                if (!await MuxVideoFileAsync(cameraMkv, cameraOut, rec.Id, "camera", ct)) { anyFailed = true; cameraOut = null; }
            }
            if (!anyFailed && remotePcm != null)
            {
                audioOut = Path.Combine(baseDir, "audio.m4a");
                if (!await MuxAudioFileAsync(remotePcm, manifest.RemoteAudio!.Value, manifest.DesktopAudioOffsetMs, audioOut, rec.Id, "audio", ct)) { anyFailed = true; audioOut = null; }
            }
            if (!anyFailed && micPcm != null)
            {
                micOut = Path.Combine(baseDir, "mic.m4a");
                if (!await MuxAudioFileAsync(micPcm, manifest.MicFormat!.Value, manifest.MicOffsetMs, micOut, rec.Id, "mic", ct)) { anyFailed = true; micOut = null; }
            }
        }

        // Update the row: the four per-track paths (or null), status reflects codec/result. On failure,
        // keep DesktopFilePath pointing into baseDir (file may not exist) so Delete can still find and
        // clean up the directory; clear the other tracks.
        rec.DesktopFilePath = anyFailed ? Path.Combine(baseDir, "desktop.mp4") : desktopOut;
        rec.CameraFilePath = anyFailed ? null : cameraOut;
        rec.AudioFilePath = anyFailed ? null : audioOut;
        rec.MicFilePath = anyFailed ? null : micOut;
        rec.Status = anyFailed ? RecordingStatus.Failed
            : manifest.UnsupportedVideoCodec ? RecordingStatus.UnsupportedCodec
            : RecordingStatus.Completed;
        await db.SaveChangesAsync(ct);

        // Intermediates (encoded camera h264, VFR mkvs) are always removed. Raws are removed only on
        // success (kept on failure for retry/debugging).
        foreach (var f in tmpFiles) { try { File.Delete(f); } catch { } }
        if (!anyFailed)
        {
            foreach (var f in rawFiles) { try { File.Delete(f); } catch { } }
            try { File.Delete(Path.Combine(baseDir, "manifest.json")); } catch { }
        }
        _logger.LogInformation("RecordingMuxService: recording {Id} → {Status}", rec.Id, rec.Status);
    }

    // Apply per-frame timestamps to an elementary H.264 stream via mkvmerge → a true-VFR MKV (copy, no
    // re-encode). Returns the MKV path, or null on failure. When no timestamp sidecar exists, mkvmerge
    // still produces a playable (CFR-ish) MKV so recording isn't lost.
    private async Task<string?> BuildVfrMkvAsync(string h264, string? tsFile, string recId, string tag,
        List<string> tmpFiles, CancellationToken ct)
    {
        string mkv = h264 + ".vfr.mkv";
        tmpFiles.Add(mkv);
        var args = new List<string> { "-o", mkv };
        if (tsFile != null && File.Exists(tsFile)) { args.Add("--timestamps"); args.Add("0:" + tsFile); }
        args.Add(h264);
        if (await RunAsync(_mkvmerge, args, recId, tag + "-mkv", ct, allowExit1: true)) return mkv;
        return null;
    }

    // Remux a single VFR video MKV → its own faststart MP4 (-c:v copy, no re-encode). +faststart moves
    // the moov atom to the front so the browser can seek without downloading the whole file.
    private Task<bool> MuxVideoFileAsync(string mkv, string outPath, string recId, string tag, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning",
            "-i", mkv, "-map", "0:v:0", "-c:v", "copy",
            "-movflags", "+faststart", "-y", outPath };
        return RunFfmpegAsync(args, recId, tag, ct);
    }

    // Encode a single raw PCM stream → its own faststart M4A (aac), silence-padded at the front by
    // offsetMs so it starts at its real session-epoch time (adelay 0 is a harmless no-op).
    private Task<bool> MuxAudioFileAsync(string pcm, PcmFormat fmt, long offsetMs, string outPath, string recId, string tag, CancellationToken ct)
    {
        long ms = Math.Max(0, offsetMs);
        var args = new List<string> { "-hide_banner", "-loglevel", "warning",
            "-f", fmt.BitsPerSample == 8 ? "u8" : "s16le",
            "-ar", fmt.SampleRate.ToString(), "-ac", Math.Max(1, fmt.Channels).ToString(),
            "-i", pcm, "-af", $"adelay={ms}:all=1", "-c:a", "aac",
            "-movflags", "+faststart", "-y", outPath };
        return RunFfmpegAsync(args, recId, tag, ct);
    }

    private Task<bool> RunFfmpegAsync(List<string> args, string recId, string tag, CancellationToken ct)
        => RunAsync(_ffmpeg, args, recId, tag, ct);

    // Run a child process, capturing stderr. allowExit1: mkvmerge returns 1 for non-fatal warnings (still
    // produces valid output), so treat exit 1 as success but log it.
    private async Task<bool> RunAsync(string exe, List<string> args, string recId, string tag,
        CancellationToken ct, bool allowExit1 = false)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = exe, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) { _logger.LogWarning("RecordingMuxService: {Exe} failed to start ({Id}/{Tag})", exe, recId, tag); return false; }
            // Read both streams concurrently (avoids a pipe-buffer deadlock). mkvmerge writes its errors to
            // STDOUT, not stderr, so capture both.
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var diag = ((await outTask) + " " + (await errTask)).Trim();
            if (p.ExitCode == 0 || (allowExit1 && p.ExitCode == 1))
            {
                if (p.ExitCode == 1) _logger.LogDebug("RecordingMuxService: {Exe} warned ({Id}/{Tag}): {Diag}", exe, recId, tag, diag);
                return true;
            }
            _logger.LogWarning("RecordingMuxService: {Exe} exit {Code} for {Id}/{Tag}: {Diag}", exe, p.ExitCode, recId, tag, diag);
            return false;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RecordingMuxService: {Exe} run failed ({Id}/{Tag})", exe, recId, tag); return false; }
    }
}
