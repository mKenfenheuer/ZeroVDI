using System.Security.Cryptography;
using System.Text;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Tamper-evidence for completed recordings: per-track SHA-256 hashes plus an append-only hash chain.
/// Hashes are computed over the FINAL on-disk bytes (i.e. the ciphertext when encryption-at-rest is on),
/// so a later verify reads the files directly without needing the recording key. The chain links each
/// recording to the previous completed one — H(prevChain || id || trackHashes) — so removing or editing a
/// recording in the middle of the timeline is detectable when the chain is recomputed from any later row.
/// </summary>
public static class RecordingIntegrity
{
    /// <summary>SHA-256 (lowercase hex) of a file, or null if the path is null/missing.</summary>
    public static string? HashFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Compute the chain hash for <paramref name="rec"/> given the previous link. The track hashes must
    /// already be populated on the recording.
    /// </summary>
    public static string ComputeChainHash(string? previousChainHash, Recording rec)
    {
        var sb = new StringBuilder();
        sb.Append(previousChainHash ?? "GENESIS");
        sb.Append('|').Append(rec.Id);
        sb.Append('|').Append(rec.DesktopSha256 ?? "-");
        sb.Append('|').Append(rec.CameraSha256 ?? "-");
        sb.Append('|').Append(rec.AudioSha256 ?? "-");
        sb.Append('|').Append(rec.MicSha256 ?? "-");
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Populate the per-track hashes and the chain hash on a freshly-muxed recording. The previous link is
    /// the most recent recording (by StartedUtc) that already has a ChainHash. Caller saves the context.
    /// </summary>
    public static async Task StampAsync(ApplicationDbContext db, Recording rec, CancellationToken ct)
    {
        rec.DesktopSha256 = HashFile(rec.DesktopFilePath);
        rec.CameraSha256 = HashFile(rec.CameraFilePath);
        rec.AudioSha256 = HashFile(rec.AudioFilePath);
        rec.MicSha256 = HashFile(rec.MicFilePath);

        var prevChain = await db.Recordings
            .Where(r => r.ChainHash != null && r.Id != rec.Id)
            .OrderByDescending(r => r.StartedUtc)
            .Select(r => r.ChainHash)
            .FirstOrDefaultAsync(ct);

        rec.ChainHash = ComputeChainHash(prevChain, rec);
    }

    /// <summary>
    /// Re-hash a recording's on-disk tracks and compare against the stored hashes. Returns the per-track
    /// verification results; a track is "intact" when its stored hash matches the current file (or both are
    /// absent), "tampered" when they differ, and "missing" when a hash was recorded but the file is gone.
    /// </summary>
    public static VerifyResult Verify(Recording rec)
    {
        var tracks = new List<TrackVerify>
        {
            VerifyTrack("desktop", rec.DesktopFilePath, rec.DesktopSha256),
            VerifyTrack("camera", rec.CameraFilePath, rec.CameraSha256),
            VerifyTrack("audio", rec.AudioFilePath, rec.AudioSha256),
            VerifyTrack("mic", rec.MicFilePath, rec.MicSha256),
        };
        // Recompute would-be chain from the stored track hashes; if the stored ChainHash doesn't match what
        // those hashes produce (given the SAME previous link), the row's hash fields were edited.
        return new VerifyResult(tracks);
    }

    private static TrackVerify VerifyTrack(string name, string? path, string? storedHash)
    {
        if (storedHash == null)
            return new TrackVerify(name, TrackState.NotRecorded, null, null);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new TrackVerify(name, TrackState.Missing, storedHash, null);
        var current = HashFile(path);
        return current == storedHash
            ? new TrackVerify(name, TrackState.Intact, storedHash, current)
            : new TrackVerify(name, TrackState.Tampered, storedHash, current);
    }

    public enum TrackState { NotRecorded, Intact, Tampered, Missing }

    public record TrackVerify(string Track, TrackState State, string? StoredHash, string? CurrentHash);

    public record VerifyResult(IReadOnlyList<TrackVerify> Tracks)
    {
        /// <summary>True only if every recorded track is intact (none tampered or missing).</summary>
        public bool AllIntact => Tracks.All(t =>
            t.State is TrackState.Intact or TrackState.NotRecorded);
    }
}
