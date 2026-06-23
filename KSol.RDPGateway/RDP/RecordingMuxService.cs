using System.Diagnostics;
using System.Text.Json;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Background job that muxes the RAW elementary streams captured by <see cref="SessionRecorder"/> into
/// the final MP4(s). Polls for recordings in <see cref="RecordingStatus.Processing"/>, reads the
/// per-recording <c>manifest.json</c>, and runs ffmpeg once per output (reading real seekable files, so
/// no FIFOs / launch-timing races):
///   • desktop.mp4 = desktop.h264 (-c:v copy) [+ desktop.pcm → aac]
///   • camera.mp4  = camera.nv12 (-c:v libx264) [+ mic.pcm → aac]
/// On success the raw files are deleted and the row is set Completed (or UnsupportedCodec when the
/// desktop codec wasn't H.264 — audio only). Failures set Failed and KEEP the raws for retry/debugging.
/// </summary>
public sealed class RecordingMuxService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<RecordingMuxService> _logger;
    private readonly string _ffmpeg;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public RecordingMuxService(IServiceScopeFactory scopes, IConfiguration config, ILogger<RecordingMuxService> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
        _ffmpeg = config["Recording:FfmpegPath"] ?? "ffmpeg";
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

        // --- desktop.mp4: H.264 (copy) + remote PCM (→ aac) ---
        string? desktopOut = null;
        if (manifest.DesktopVideo != null || manifest.DesktopAudio != null)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "warning" };
            string? vIn = manifest.DesktopVideo != null ? Path.Combine(baseDir, manifest.DesktopVideo) : null;
            string? aIn = manifest.DesktopAudio != null ? Path.Combine(baseDir, manifest.DesktopAudio) : null;
            if (vIn != null && File.Exists(vIn)) { args.AddRange(new[] { "-r", "30", "-f", "h264", "-i", vIn }); rawFiles.Add(vIn); } else vIn = null;
            if (aIn != null && File.Exists(aIn) && manifest.RemoteAudio is { } rf)
            { args.AddRange(PcmInput(rf, aIn)); rawFiles.Add(aIn); } else aIn = null;
            if (vIn != null || aIn != null)
            {
                if (vIn != null) args.AddRange(new[] { "-c:v", "copy" });
                if (aIn != null) { args.AddRange(new[] { "-c:a", "aac" }); args.AddRange(AudioDelay(manifest.DesktopAudioOffsetMs)); }
                args.AddRange(new[] { "-movflags", "+faststart", "-y", rec.DesktopFilePath! });
                if (await RunFfmpegAsync(args, rec.Id, "desktop", ct)) desktopOut = rec.DesktopFilePath;
                else anyFailed = true;
            }
        }

        // --- camera.mp4: NV12 (→ libx264) + mic PCM (→ aac) ---
        string? cameraOut = null;
        if (manifest.CameraVideo != null && manifest.CameraWidth > 0 && manifest.CameraHeight > 0)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "warning" };
            string vIn = Path.Combine(baseDir, manifest.CameraVideo);
            string? aIn = manifest.MicAudio != null ? Path.Combine(baseDir, manifest.MicAudio) : null;
            if (File.Exists(vIn))
            {
                args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "nv12", "-s",
                    $"{manifest.CameraWidth}x{manifest.CameraHeight}", "-r",
                    manifest.CameraFps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", vIn });
                rawFiles.Add(vIn);
                if (aIn != null && File.Exists(aIn) && manifest.MicFormat is { } mf) { args.AddRange(PcmInput(mf, aIn)); rawFiles.Add(aIn); } else aIn = null;
                args.AddRange(new[] { "-c:v", "libx264", "-pix_fmt", "yuv420p" });
                if (aIn != null) { args.AddRange(new[] { "-c:a", "aac" }); args.AddRange(AudioDelay(manifest.MicOffsetMs)); }
                args.AddRange(new[] { "-movflags", "+faststart", "-y", rec.CameraFilePath! });
                if (await RunFfmpegAsync(args, rec.Id, "camera", ct)) cameraOut = rec.CameraFilePath;
                else anyFailed = true;
            }
        }

        // Update the row: paths reflect what was actually produced; status reflects codec/result.
        rec.DesktopFilePath = desktopOut;
        rec.CameraFilePath = cameraOut;
        rec.Status = anyFailed ? RecordingStatus.Failed
            : manifest.UnsupportedVideoCodec ? RecordingStatus.UnsupportedCodec
            : RecordingStatus.Completed;
        await db.SaveChangesAsync(ct);

        // Delete the raws on success (keep them on failure for retry/debugging).
        if (!anyFailed)
        {
            foreach (var f in rawFiles) { try { File.Delete(f); } catch { } }
            try { File.Delete(Path.Combine(baseDir, "manifest.json")); } catch { }
        }
        _logger.LogInformation("RecordingMuxService: recording {Id} → {Status}", rec.Id, rec.Status);
    }

    private static string[] PcmInput(PcmFormat f, string path)
        => new[] { "-f", f.BitsPerSample == 8 ? "u8" : "s16le", "-ar", f.SampleRate.ToString(),
                   "-ac", Math.Max(1, f.Channels).ToString(), "-i", path };

    // Audio gap/offset alignment: prepend real silence to the audio so a sample that played at t=offsetMs
    // lands there instead of bunched at t=0. adelay inserts silence samples (robust across players), and
    // is applied during the AAC re-encode (incompatible with stream-copy, but audio is always encoded).
    private static string[] AudioDelay(long offsetMs)
        => offsetMs > 0 ? new[] { "-af", $"adelay={offsetMs}:all=1" } : Array.Empty<string>();

    private async Task<bool> RunFfmpegAsync(List<string> args, string recId, string tag, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = _ffmpeg, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) { _logger.LogWarning("RecordingMuxService: ffmpeg failed to start ({Id}/{Tag})", recId, tag); return false; }
            var err = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                _logger.LogWarning("RecordingMuxService: ffmpeg exit {Code} for {Id}/{Tag}: {Err}", p.ExitCode, recId, tag, err);
                return false;
            }
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RecordingMuxService: ffmpeg run failed ({Id}/{Tag})", recId, tag); return false; }
    }
}
