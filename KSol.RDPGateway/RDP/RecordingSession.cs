using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using RDPGW.AspNetCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Owns the lifecycle of a recorded session's <see cref="Recording"/> row and its
/// <see cref="SessionRecorder"/> media sink, independent of any HTTP request scope. Used by the native
/// RDGW path (<see cref="GatewayConnectionHandler"/> / <see cref="MitmRdpStream"/>), which has no
/// controller <c>finally</c> to finalize the row — <see cref="FinalizeAsync"/> is invoked when the MITM
/// stream completes. Mirrors the setup/finalize the in-browser console performs inline in its controller.
/// </summary>
internal sealed class RecordingSession
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Recording _recording;
    private readonly ILogger _logger;
    private int _finalized;

    public SessionRecorder Recorder { get; }

    private RecordingSession(IServiceScopeFactory scopeFactory, Recording recording, SessionRecorder recorder, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _recording = recording;
        Recorder = recorder;
        _logger = logger;
    }

    /// <summary>
    /// Creates the Recording row (status Recording) and a SessionRecorder writing raw streams under a
    /// per-recording base dir, ready to be passed to the relay as the media sink. Returns null if setup
    /// fails (the caller then proceeds unrecorded).
    /// </summary>
    public static async Task<RecordingSession?> StartAsync(
        IServiceScopeFactory scopeFactory, IConfiguration config,
        RDPGWConnectionContext context, ILogger logger)
    {
        try
        {
            var dir = config["Recording:Directory"]
                ?? Path.Combine(Directory.GetCurrentDirectory(), "Data", "recordings");
            var recId = Guid.NewGuid().ToString();
            var baseDir = Path.Combine(dir, recId);
            Directory.CreateDirectory(baseDir);

            // Provisionally point DesktopFilePath at desktop.mp4 so the mux job can derive baseDir; the
            // job overwrites all path fields with the actual results (or null). Same as the console path.
            var recording = new Recording
            {
                Id = recId,
                UserId = context.UserId,
                RDPResourceId = context.Resource,
                StartedUtc = DateTime.UtcNow,
                Status = RecordingStatus.Recording,
                DesktopFilePath = Path.Combine(baseDir, "desktop.mp4"),
                CameraFilePath = null,
            };

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Recordings.Add(recording);
                await db.SaveChangesAsync();
            }

            var recorder = new SessionRecorder(baseDir, logger);
            logger.LogInformation("RDGW native: recording session {RecId} for {Resource}", recId, context.Resource);
            return new RecordingSession(scopeFactory, recording, recorder, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RDGW native: recording setup failed; continuing unrecorded");
            return null;
        }
    }

    /// <summary>
    /// Closes the raw streams (Dispose writes manifest.json) and marks the recording Processing (handing
    /// off to <see cref="RecordingMuxService"/>) or Failed if nothing was captured. Idempotent.
    /// </summary>
    public void FinalizeAsync()
    {
        if (Interlocked.Exchange(ref _finalized, 1) != 0) return;
        try
        {
            Recorder.Dispose();
            _recording.EndedUtc = DateTime.UtcNow;
            _recording.VideoCodec = Recorder.VideoCodec ?? "none";
            if (!Recorder.HasDesktop && !Recorder.HasCamera)
                _recording.Status = RecordingStatus.Failed; // nothing captured
            else
                _recording.Status = RecordingStatus.Processing;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Recordings.Update(_recording);
            db.SaveChanges();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RDGW native: finalizing recording {RecId} failed", _recording.Id); }
    }
}
