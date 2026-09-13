namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Live structural decode + recording of a decrypted RDP byte stream via the shared RdpWire engine.
/// <see cref="RdpRelaySession"/> feeds both directions of its decrypted stream here.
///
/// When <c>RDPGW_DUMP_DIR</c> is set it also writes a diagnostic PDU log (pdus.log/pdus.jsonl) and
/// meta.txt (per-chunk dir+ts+len, so an offline <c>rdpmitm --replay</c> reconstructs true wire order).
/// Otherwise only the <see cref="IRdpMediaSink"/> callbacks (extracted H.264/PCM/NV12) matter and the
/// decode tree is discarded.
///
/// CRITICAL: the structural decode (<see cref="RdpSession.Feed"/>) must NEVER run on a relay hot path —
/// blocking the s2c pump on a per-chunk decode back-pressures the host (it pauses mid-frame). So
/// <see cref="Feed"/> only copies + enqueues; a single background task drains the queue and decodes
/// serially (RdpSession is stateful/not thread-safe, so exactly one consumer).
/// </summary>
internal sealed class RdpStreamRecorder : IDisposable
{
    /// <summary>A no-op event sink for recording-only sessions (no RDPGW_DUMP_DIR diagnostic log).</summary>
    private sealed class NullEventSink : IRdpEventSink
    {
        public static readonly NullEventSink Instance = new();
        public void Emit(RdpDir dir, long elapsedMs, Node node, RoundTrip rt) { }
    }

    private readonly RdpLogSink? _sink;          // diagnostic PDU log (only when RDPGW_DUMP_DIR set)
    private readonly RdpSession _session;
    private readonly StreamWriter? _meta;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private readonly ILogger _logger;
    private readonly System.Threading.Channels.Channel<(RdpDir dir, long ms, byte[] data)> _queue;
    private readonly Task _drain;

    private RdpStreamRecorder(string? dumpDir, IRdpMediaSink? media, ILogger logger)
    {
        _logger = logger;
        IRdpEventSink eventSink;
        if (!string.IsNullOrEmpty(dumpDir))
        {
            _sink = new RdpLogSink(dumpDir, echoConsole: false);
            eventSink = _sink;
            try { _meta = new StreamWriter(Path.Combine(dumpDir, "meta.txt")) { AutoFlush = true }; } catch { _meta = null; }
        }
        else eventSink = NullEventSink.Instance;
        _session = new RdpSession(eventSink, media);
        _queue = System.Threading.Channels.Channel.CreateUnbounded<(RdpDir, long, byte[])>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Whether the <c>RDPGW_DUMP_DIR</c> raw-stream dump may run at all. Set once at startup from the
    /// hosting environment (development only). The dump writes the DECRYPTED stream — keystrokes,
    /// screen contents and the connection-sequence PDUs — to unencrypted files outside the recording
    /// pipeline, with none of its retention, encryption-at-rest or tamper-evidence. An environment
    /// variable on its own must therefore not be able to switch it on in production.
    /// </summary>
    public static bool StreamDumpAllowed { get; set; }

    /// <summary>
    /// Create a recorder if either diagnostics (RDPGW_DUMP_DIR, development only) or real recording
    /// (a media sink) is requested. Returns null when neither is active (no decode overhead for
    /// ordinary sessions).
    /// </summary>
    public static RdpStreamRecorder? TryCreate(ILogger logger, IRdpMediaSink? media)
    {
        var dir = Environment.GetEnvironmentVariable("RDPGW_DUMP_DIR");
        bool wantDump = !string.IsNullOrEmpty(dir);
        if (wantDump && !StreamDumpAllowed)
        {
            logger.LogWarning("RDPGW_DUMP_DIR is set but raw stream dumps are only available in the " +
                "Development environment; ignoring it.");
            wantDump = false;
        }
        if (!wantDump && media == null) return null;
        try
        {
            if (wantDump) Directory.CreateDirectory(dir!);
            return new RdpStreamRecorder(wantDump ? dir : null, media, logger);
        }
        catch (Exception ex) { logger.LogDebug(ex, "RDP recorder: init failed"); return null; }
    }

    /// <summary>
    /// Hot-path: copy + enqueue only. Never blocks on decode. (The copy is required because the caller's
    /// buffer is reused after this returns.)
    /// </summary>
    public void Feed(RdpDir dir, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        _queue.Writer.TryWrite((dir, _sw.ElapsedMilliseconds, data.ToArray()));
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var (dir, ms, data) in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    _meta?.WriteLine($"{ms,8} {(dir == RdpDir.ClientToServer ? "C2S" : "S2C")} {data.Length}");
                    _session.Feed(dir, data);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "RDP recorder: decode error"); }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "RDP recorder: drain ended"); }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _drain.Wait(TimeSpan.FromSeconds(5)); } catch { }
        try { _meta?.Dispose(); } catch { }
        try { _sink?.Dispose(); } catch { }
    }
}
