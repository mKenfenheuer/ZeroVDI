using KSol.ZeroVDI.RDP.Bridge;

namespace KSol.ZeroVDI.RDP.Spice;

/// <summary>
/// The SPICE host protocol, plugged into the shared RDP encoder. Like <see cref="VncRdpResolver"/> this
/// resolver is thin: it constructs the SPICE <see cref="IProtocolSource"/> (<see cref="SpiceClient"/>),
/// connects it, and hands it to the shared <see cref="RdpEncoderSession"/> which owns all RDP-server +
/// codec + scaling logic.
///
///   SpiceClient (IProtocolSource) → RdpEncoderSession (shared) → DuplexPipeStream → RdpRelaySession → web
///
/// The only host-protocol-specific code is <see cref="SpiceClient"/> and its channels — no RDP knowledge.
/// </summary>
public sealed class SpiceRdpResolver : IRdpResolver
{
    private readonly string _ffmpegPath;

    public SpiceRdpResolver(IConfiguration config)
    {
        // Reuse the recording ffmpeg for real-time H.264 encoding of the GFX path.
        _ffmpegPath = config["Recording:FfmpegPath"] ?? "ffmpeg";
    }

    public async Task<RdpHostConnection.Connected> ConnectAsync(RdpResolveRequest request, CancellationToken ct)
    {
        var logger = request.Logger;

        // 1) Build + connect the SPICE source (link handshake + ticket auth + wait for the primary
        // surface). Runs synchronously so we surface auth/reachability failures as a ConnectException
        // before the browser session is handed a stream.
        //
        // Proxmox spiceproxy: each SPICE channel rides its OWN freshly-fetched single-use ticket, so the
        // SPICE auth password must come from the tunnel the channel just dialed — the transport exposes it
        // as LastTicketPassword. Direct SPICE uses request.Creds.password for every channel.
        Func<string?>? passwordProvider = request.Transport is SpiceProxyTransport spt
            ? () => spt.LastTicketPassword
            : null;
        var source = new SpiceClient(request.Transport, request.Host, request.Port,
            request.Creds.user, request.Creds.password, logger, passwordProvider);
        try
        {
            await source.ConnectAsync(ct);
        }
        catch (RdpHostConnection.ConnectException)
        {
            await source.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            await source.DisposeAsync();
            logger.LogWarning(ex, "SPICE: source connect failed");
            throw new RdpHostConnection.ConnectException("SPICE handshake failed");
        }

        if (source.Width <= 0 || source.Height <= 0)
        {
            await source.DisposeAsync();
            throw new RdpHostConnection.ConnectException("SPICE server reported an invalid display size");
        }

        // 2) Hand the source to the shared RDP encoder over an in-memory duplex; the browser reads the
        // RDP stream from BrowserSide.
        var pipe = new DuplexPipeStream();
        var encoder = new RdpEncoderSession(source, pipe.ServerSide, KeysymMap.For(request.KeyboardLayout), _ffmpegPath, logger,
            bitmapCongestion: () => pipe.ServerToBrowserPending);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _ = Task.Run(async () =>
        {
            try { await encoder.RunAsync(cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "SPICE bridge ended with error"); }
            finally { pipe.Complete(); await encoder.DisposeAsync(); await source.DisposeAsync(); }
        }, cts.Token);

        var disposer = new ActionDisposable(() =>
        {
            try { cts.Cancel(); } catch { }
            pipe.Complete();
            _ = source.DisposeAsync();
        });
        return new RdpHostConnection.Connected(pipe.BrowserSide, disposer);
    }

    /// <summary>A <see cref="Stream"/>-typed disposer so <c>Connected.Inner</c> can carry teardown.</summary>
    private sealed class ActionDisposable : Stream
    {
        private readonly Action _onDispose;
        public ActionDisposable(Action onDispose) => _onDispose = onDispose;
        protected override void Dispose(bool disposing) { if (disposing) _onDispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
