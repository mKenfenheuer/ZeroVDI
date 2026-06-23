using System.Diagnostics;
using System.Text.Json;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Background job that muxes the RAW elementary streams captured by <see cref="SessionRecorder"/> into
/// the final MP4(s). Polls for recordings in <see cref="RecordingStatus.Processing"/>, reads the
/// per-recording <c>manifest.json</c>, and produces ONE combined MP4 (session.mp4) with up to four
/// separate tracks (reading real seekable files, so no FIFOs / launch-timing races):
///   • desktop video — desktop.h264 (-c:v copy, VFR via per-frame timestamps)
///   • camera video  — camera.nv12 (-c:v libx264, VFR via per-frame timestamps)
///   • remote audio  — desktop.pcm (→ aac, silence-padded to its session offset)
///   • mic audio     — mic.pcm     (→ aac, silence-padded to its session offset)
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

        // --- single combined MP4: desktop video + camera video + remote audio + mic audio ---
        string? sessionOut = null;
        if (!anyFailed && (desktopMkv != null || cameraMkv != null || remotePcm != null || micPcm != null))
        {
            var tracks = new List<MuxTrack>();
            if (desktopMkv != null) tracks.Add(MuxTrack.Video(desktopMkv, "desktop"));
            if (cameraMkv != null) tracks.Add(MuxTrack.Video(cameraMkv, "camera"));
            if (remotePcm != null) tracks.Add(MuxTrack.Audio(remotePcm, manifest.RemoteAudio!.Value, manifest.DesktopAudioOffsetMs, "audio"));
            if (micPcm != null) tracks.Add(MuxTrack.Audio(micPcm, manifest.MicFormat!.Value, manifest.MicOffsetMs, "mic"));
            if (await MuxCombinedAsync(tracks, rec.DesktopFilePath!, rec.Id, ct)) sessionOut = rec.DesktopFilePath;
            else anyFailed = true;
        }

        // Update the row: the single MP4 path (or null), status reflects codec/result.
        rec.DesktopFilePath = sessionOut;
        rec.CameraFilePath = null;
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

    // One input track for the combined mux: either a VFR video MKV (copied) or a raw PCM audio stream
    // (AAC-encoded, silence-padded by OffsetMs so it lands at its real session time). Title labels the
    // track in the container (desktop/camera/audio/mic) so desktop players can tell them apart.
    private readonly record struct MuxTrack(bool IsVideo, string Path, PcmFormat Pcm, long OffsetMs, string Title)
    {
        public static MuxTrack Video(string path, string title) => new(true, path, default, 0, title);
        public static MuxTrack Audio(string path, PcmFormat fmt, long offsetMs, string title) => new(false, path, fmt, offsetMs, title);
    }

    // Combined mux: up to two VFR video MKVs (copy) + up to two PCM audio streams (→ aac, each
    // silence-padded by its own offset) → ONE MP4 with all tracks mapped as separate streams. Audio
    // delays are applied via -filter_complex (one adelay per audio input, each to its own output label),
    // because a plain -af can't target individual inputs when there are several.
    private async Task<bool> MuxCombinedAsync(List<MuxTrack> tracks, string outPath, string recId, CancellationToken ct)
    {
        if (tracks.Count == 0) return false;
        var args = new List<string> { "-hide_banner", "-loglevel", "warning" };

        // Inputs in order; remember each track's ffmpeg input index.
        var inputIndex = new int[tracks.Count];
        int idx = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.IsVideo) { args.Add("-i"); args.Add(t.Path); }
            else
            {
                args.AddRange(new[] { "-f", t.Pcm.BitsPerSample == 8 ? "u8" : "s16le",
                    "-ar", t.Pcm.SampleRate.ToString(), "-ac", Math.Max(1, t.Pcm.Channels).ToString(),
                    "-i", t.Path });
            }
            inputIndex[i] = idx++;
        }

        // Per-audio-input adelay filter graph: [N:a]adelay=ms:all=1[aLabel].
        var filters = new List<string>();
        var audioLabels = new List<string>();
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].IsVideo) continue;
            string label = "a" + i;
            long ms = Math.Max(0, tracks[i].OffsetMs);
            // adelay with 0 is a harmless no-op; keep the filter so the label exists to map.
            filters.Add($"[{inputIndex[i]}:a]adelay={ms}:all=1[{label}]");
            audioLabels.Add(label);
        }
        if (filters.Count > 0) { args.Add("-filter_complex"); args.Add(string.Join(";", filters)); }

        // Map every track. Video by input:stream; audio by its filter output label.
        int outVideo = 0, outAudio = 0, ai = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.IsVideo)
            {
                args.AddRange(new[] { "-map", $"{inputIndex[i]}:v:0" });
                // MP4 stores per-track names in handler_name (not title); desktop players show it.
                args.AddRange(new[] { $"-metadata:s:v:{outVideo}", $"handler_name={t.Title}" });
                outVideo++;
            }
            else
            {
                args.AddRange(new[] { "-map", $"[{audioLabels[ai++]}]" });
                args.AddRange(new[] { $"-metadata:s:a:{outAudio}", $"handler_name={t.Title}" });
                outAudio++;
            }
        }

        if (outVideo > 0) args.AddRange(new[] { "-c:v", "copy" });
        if (outAudio > 0) args.AddRange(new[] { "-c:a", "aac" });
        args.AddRange(new[] { "-movflags", "+faststart", "-y", outPath });
        return await RunFfmpegAsync(args, recId, "combined", ct);
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
