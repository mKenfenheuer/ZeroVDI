namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// A remote-desktop <b>protocol source</b>: something that provides a live desktop pixel stream and
/// accepts input, independent of RDP. The shared <see cref="RdpEncoderSession"/> drives any source into
/// the browser RDP client, so adding a new host protocol (VNC or SPICE; SPICE, etc. later) means writing a
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

    /// <summary>
    /// Raised when the source's native desktop size changes mid-session (e.g. the SPICE guest re-created
    /// its primary surface after a resize request). Carries the new (width, height). The encoder rebuilds
    /// its framebuffer + output surface to match. Sources with a fixed size never raise this.
    /// </summary>
    event Action<int, int>? OnGeometryChanged;

    /// <summary>Connects and authenticates to the host, populating <see cref="Width"/>/<see cref="Height"/>.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Asks the host to change its desktop resolution to (width, height) — used to make the source match
    /// the RDP client's requested/window size 1:1 (avoids letterbox scaling). Best-effort: sources that
    /// can't resize (or whose guest agent is absent) ignore it and the encoder keeps letterbox-scaling.
    /// A successful resize later surfaces as <see cref="OnGeometryChanged"/>.
    /// </summary>
    Task RequestResizeAsync(int width, int height, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Runs the source's receive loop (dispatching <see cref="OnRectangle"/>) until cancelled.</summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>Requests a full or incremental refresh of the whole desktop.</summary>
    Task RequestFullFrameAsync(CancellationToken ct);
    Task RequestIncrementalAsync(CancellationToken ct);

    /// <summary>
    /// Adapts the <b>host-to-gateway</b> quality/bandwidth to a congestion tier (0 = best quality … higher =
    /// more compression / lower quality). Driven by the encoder's backpressure controller so that on a slow
    /// client link we also pull cheaper frames FROM the host, not just compress harder toward the client.
    /// Best-effort: sources map the tier onto their own knobs (VNC → Tight JPEG quality + zlib level;
    /// SPICE → image-codec preference). Sources with no such knob ignore it.
    /// </summary>
    Task SetQualityTierAsync(int tier, CancellationToken ct) => Task.CompletedTask;

    // ── input (source coordinate space; the encoder maps browser input into it) ──
    /// <summary>Pointer moved/clicked at native (x, y) with an absolute button mask (bit0=L,1=M,2=R,3=up,4=down).</summary>
    Task PointerAsync(int x, int y, int buttonMask, CancellationToken ct);
    /// <summary>Key event by X11 keysym, down or up.</summary>
    Task KeyAsync(uint keysym, bool down, CancellationToken ct);

    /// <summary>
    /// True if the source consumes raw RDP (AT set-1) scancodes directly via
    /// <see cref="KeyScancodeAsync"/> instead of X11 keysyms. SPICE speaks the same scancode set as the
    /// browser, so it takes scancodes losslessly; VNC/RFB is keysym-based and leaves this false, so the
    /// encoder resolves scancodes to keysyms for it.
    /// </summary>
    bool PrefersScancodes => false;

    /// <summary>Raw AT set-1 scancode key event (only called when <see cref="PrefersScancodes"/> is true).</summary>
    Task KeyScancodeAsync(byte scancode, bool extended, bool down, CancellationToken ct) => Task.CompletedTask;
}
