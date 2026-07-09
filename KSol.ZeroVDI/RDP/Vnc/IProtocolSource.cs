namespace KSol.ZeroVDI.RDP.Vnc;

/// <summary>
/// A remote-desktop <b>protocol source</b>: something that provides a live desktop pixel stream and
/// accepts input, independent of RDP. The shared <see cref="RdpEncoderSession"/> drives any source into
/// the browser RDP client, so adding a new host protocol (VNC today; SPICE, etc. later) means writing a
/// new <see cref="IProtocolSource"/> — no RDP knowledge required.
///
///   IProtocolSource (VNC, …) → RdpEncoderSession (shared: RDP server + codecs) → chain → web client
///
/// Audio and clipboard will be added to this interface when implemented (they ride the GFX/DVC channels
/// the bitmap path does not need yet).
/// </summary>
internal interface IProtocolSource : IAsyncDisposable
{
    /// <summary>Native desktop width in pixels (valid after <see cref="ConnectAsync"/>).</summary>
    int Width { get; }
    /// <summary>Native desktop height in pixels (valid after <see cref="ConnectAsync"/>).</summary>
    int Height { get; }

    /// <summary>
    /// Raised for each decoded desktop rectangle: (x, y, w, h, top-down 32bpp BGRX pixels) in the
    /// source's native coordinate space. The encoder scales/letterboxes into the RDP session size.
    /// </summary>
    event Action<int, int, int, int, byte[]>? OnRectangle;

    /// <summary>Connects and authenticates to the host, populating <see cref="Width"/>/<see cref="Height"/>.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Runs the source's receive loop (dispatching <see cref="OnRectangle"/>) until cancelled.</summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>Requests a full or incremental refresh of the whole desktop.</summary>
    Task RequestFullFrameAsync(CancellationToken ct);
    Task RequestIncrementalAsync(CancellationToken ct);

    // ── input (source coordinate space; the encoder maps browser input into it) ──
    /// <summary>Pointer moved/clicked at native (x, y) with an absolute button mask (bit0=L,1=M,2=R,3=up,4=down).</summary>
    Task PointerAsync(int x, int y, int buttonMask, CancellationToken ct);
    /// <summary>Key event by X11 keysym, down or up.</summary>
    Task KeyAsync(uint keysym, bool down, CancellationToken ct);
}
