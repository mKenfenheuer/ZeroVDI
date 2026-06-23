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

        // --- desktop.mp4: H.264 (copy, true VFR via per-frame timestamps) + remote PCM (→ aac) ---
        string? desktopOut = null;
        if (manifest.DesktopVideo != null || manifest.DesktopAudio != null)
        {
            string? vIn = manifest.DesktopVideo != null ? Path.Combine(baseDir, manifest.DesktopVideo) : null;
            string? tsIn = manifest.DesktopVideoTs != null ? Path.Combine(baseDir, manifest.DesktopVideoTs) : null;
            string? aIn = manifest.DesktopAudio != null ? Path.Combine(baseDir, manifest.DesktopAudio) : null;
            if (vIn != null && File.Exists(vIn)) rawFiles.Add(vIn); else vIn = null;
            if (aIn != null && File.Exists(aIn) && manifest.RemoteAudio != null) rawFiles.Add(aIn); else aIn = null;
            if (tsIn != null) rawFiles.Add(tsIn);

            // Apply per-frame timestamps to the elementary H.264 → VFR MKV (copy), then ffmpeg muxes that
            // (copy) with the offset-aligned audio into the final MP4.
            string? vfrMkv = vIn != null ? await BuildVfrMkvAsync(vIn, tsIn, rec.Id, "desktop", tmpFiles, ct) : null;
            if (vIn != null && vfrMkv == null) anyFailed = true;
            else if (await MuxFinalAsync(vfrMkv, aIn, manifest.RemoteAudio, manifest.DesktopAudioOffsetMs,
                         rec.DesktopFilePath!, rec.Id, "desktop", ct)) desktopOut = rec.DesktopFilePath;
            else if (vfrMkv != null || aIn != null) anyFailed = true;
        }

        // --- camera.mp4: NV12 → H.264 (encode), VFR via timestamps, + mic PCM (→ aac) ---
        string? cameraOut = null;
        if (manifest.CameraVideo != null && manifest.CameraWidth > 0 && manifest.CameraHeight > 0)
        {
            string vIn = Path.Combine(baseDir, manifest.CameraVideo);
            string? tsIn = manifest.CameraVideoTs != null ? Path.Combine(baseDir, manifest.CameraVideoTs) : null;
            string? aIn = manifest.MicAudio != null ? Path.Combine(baseDir, manifest.MicAudio) : null;
            if (File.Exists(vIn))
            {
                rawFiles.Add(vIn);
                if (tsIn != null) rawFiles.Add(tsIn);
                if (aIn != null && File.Exists(aIn) && manifest.MicFormat != null) rawFiles.Add(aIn); else aIn = null;

                // NV12 has no encoded form mkvmerge can take, so encode to elementary H.264 first (frame
                // order preserved, 1:1), then apply the timestamps for VFR, then mux with mic.
                string camH264 = Path.Combine(baseDir, "camera.enc.h264");
                tmpFiles.Add(camH264);
                var enc = new List<string> { "-hide_banner", "-loglevel", "warning",
                    "-f", "rawvideo", "-pix_fmt", "nv12", "-s", $"{manifest.CameraWidth}x{manifest.CameraHeight}",
                    "-i", vIn, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-bsf:v", "h264_mp4toannexb",
                    "-f", "h264", "-y", camH264 };
                if (!await RunFfmpegAsync(enc, rec.Id, "camera-enc", ct)) anyFailed = true;
                else
                {
                    string? vfrMkv = await BuildVfrMkvAsync(camH264, tsIn, rec.Id, "camera", tmpFiles, ct);
                    if (vfrMkv == null) anyFailed = true;
                    else if (await MuxFinalAsync(vfrMkv, aIn, manifest.MicFormat, manifest.MicOffsetMs,
                                 rec.CameraFilePath!, rec.Id, "camera", ct)) cameraOut = rec.CameraFilePath;
                    else anyFailed = true;
                }
            }
        }

        // Update the row: paths reflect what was actually produced; status reflects codec/result.
        rec.DesktopFilePath = desktopOut;
        rec.CameraFilePath = cameraOut;
        rec.Status = anyFailed ? RecordingStatus.Failed
            : manifest.UnsupportedVideoCodec ? RecordingStatus.UnsupportedCodec
            : RecordingStatus.Completed;
        await db.SaveChangesAsync(ct);

        // Intermediates (encoded camera h264, VFR mkvs) are always removed. Raws are removed only on
        // success (kept on failure for retry/debugging).
        foreach (var f in tmpFiles) { try { File.Delete(f); } catch { } }
        /*if (!anyFailed)
        {
            foreach (var f in rawFiles) { try { File.Delete(f); } catch { } }
            try { File.Delete(Path.Combine(baseDir, "manifest.json")); } catch { }
        }*/
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

    // Final mux: VFR video MKV (copy) + optional PCM audio (→ aac, silence-padded by offsetMs) → MP4.
    private async Task<bool> MuxFinalAsync(string? videoMkv, string? pcm, PcmFormat? fmt, long offsetMs,
        string outPath, string recId, string tag, CancellationToken ct)
    {
        if (videoMkv == null && pcm == null) return false;
        var args = new List<string> { "-hide_banner", "-loglevel", "warning" };
        if (videoMkv != null) { args.Add("-i"); args.Add(videoMkv); }
        if (pcm != null && fmt is { } f)
        {
            args.AddRange(new[] { "-f", f.BitsPerSample == 8 ? "u8" : "s16le", "-ar", f.SampleRate.ToString(),
                                  "-ac", Math.Max(1, f.Channels).ToString(), "-i", pcm });
        }
        else pcm = null;
        if (videoMkv != null) args.AddRange(new[] { "-c:v", "copy" });
        if (pcm != null)
        {
            args.AddRange(new[] { "-c:a", "aac" });
            // adelay inserts real leading silence so audio that started at t=offset lands there.
            if (offsetMs > 0) { args.Add("-af"); args.Add($"adelay={offsetMs}:all=1"); }
        }
        args.AddRange(new[] { "-movflags", "+faststart", "-y", outPath });
        return await RunFfmpegAsync(args, recId, tag, ct);
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
