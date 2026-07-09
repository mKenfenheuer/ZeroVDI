namespace KSol.ZeroVDI.RDP.Vnc;

/// <summary>
/// Bridges an RFB/VNC host into a decrypted RDP byte stream the unmodified browser console + recorder
/// consume. Speaks RFB to the host, and internally runs a minimal RDP server (starting at MCS
/// Connect-Initial — the gateway already terminates X.224/TLS/NLA, so the browser client begins there
/// over plaintext, see <see cref="RdpServerFrontEnd"/>) that translates VNC framebuffer updates into RDP
/// fastpath bitmap PDUs and RDP input PDUs into VNC pointer/key events.
///
/// Built incrementally (see the plan milestones):
///   M1 — RDP-server handshake to ACTIVE, feeding one solid-color frame (no VNC yet). ← current
///   M2 — RFB connect + full-frame Raw. M3 — incremental + mouse. M4 — keyboard.
///   M5 — H.264/GFX output. M6 — Progressive/ClearCodec.
/// </summary>
public sealed class VncRdpResolver : IRdpResolver
{
    public Task<RdpHostConnection.Connected> ConnectAsync(RdpResolveRequest request, CancellationToken ct)
    {
        // M1: no RFB connection yet. Stand up the RDP server front-end over an in-memory duplex and, once
        // the browser reaches ACTIVE, paint a solid color to prove every server encoder against the real
        // client. M2 replaces the solid fill with real framebuffer pixels from RfbClient.
        var logger = request.Logger;
        // Fixed placeholder geometry until ServerInit (M2) reports the real framebuffer size.
        const int width = 1024, height = 768;

        var pipe = new DuplexPipeStream();
        var frontEnd = new RdpServerFrontEnd(pipe.ServerSide, width, height, logger);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _ = Task.Run(async () =>
        {
            try
            {
                await frontEnd.RunHandshakeAsync(cts.Token);
                // M1 placeholder content: a solid teal fill so the browser visibly reaches "active".
                var rect = PixelConvert.SolidRect(width, height, 0x11, 0x88, 0x88);
                await frontEnd.SendBitmapAsync(new[] { rect }, cts.Token);
                logger.LogInformation("VNC/RDP-server: painted M1 solid-color frame ({W}x{H})", width, height);
                // Keep the client stream drained so it doesn't back-pressure; ends when the browser closes.
                await frontEnd.DrainClientAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "VNC/RDP-server front-end ended with error"); }
            finally { pipe.Complete(); }
        }, cts.Token);

        // The disposer tears down the front-end task and the pipe when the relay releases the session.
        var disposer = new ActionDisposable(() => { try { cts.Cancel(); } catch { } pipe.Complete(); });
        return Task.FromResult(new RdpHostConnection.Connected(pipe.BrowserSide, disposer));
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
