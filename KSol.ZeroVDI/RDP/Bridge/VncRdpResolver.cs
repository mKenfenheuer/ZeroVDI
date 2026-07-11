namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// The VNC host protocol, plugged into the shared RDP encoder. This resolver is now thin: it constructs
/// the VNC <see cref="IProtocolSource"/> (<see cref="RfbClient"/>), connects it, and hands it to the
/// shared <see cref="RdpEncoderSession"/> which owns all RDP-server + codec + scaling logic.
///
///   RfbClient (IProtocolSource) → RdpEncoderSession (shared) → DuplexPipeStream → RdpRelaySession → web
///
/// Adding another host protocol (SPICE, …) means writing a new <see cref="IProtocolSource"/> and a
/// one-line resolver like this — no RDP knowledge required.
/// </summary>
public sealed class VncRdpResolver : IRdpResolver
{
    private readonly string _ffmpegPath;

    public VncRdpResolver(IConfiguration config)
    {
        // Reuse the recording ffmpeg for real-time H.264 encoding of the GFX path.
        _ffmpegPath = config["Recording:FfmpegPath"] ?? "ffmpeg";
    }

    public async Task<RdpHostConnection.Connected> ConnectAsync(RdpResolveRequest request, CancellationToken ct)
    {
        var logger = request.Logger;

        // 1) Build + connect the VNC source (RFB handshake + auth). Runs synchronously so we surface auth/
        // reachability failures as a ConnectException before the browser session is handed a stream.
        var source = new RfbClient(request.Transport, request.Host, request.Port,
            request.Creds.user, request.Creds.password, logger);
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
            logger.LogWarning(ex, "VNC: source connect failed");
            throw new RdpHostConnection.ConnectException("VNC handshake failed");
        }

        if (source.Width <= 0 || source.Height <= 0)
        {
            await source.DisposeAsync();
            throw new RdpHostConnection.ConnectException("VNC server reported an invalid framebuffer size");
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
            catch (Exception ex) { logger.LogWarning(ex, "VNC bridge ended with error"); }
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
