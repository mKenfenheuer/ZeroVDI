namespace KSol.RDPGateway.Models;

/// <summary>
/// The defaults for the in-browser RDP console connect form. Held both per-(user, resource) on the
/// <see cref="RDPResourceUserAuthorization"/> and resource-wide on
/// <see cref="RDPResource.DefaultConnectionDefaults"/> (inherited by newly granted users). When a
/// per-user set is present the console auto-connects using it instead of showing the login overlay.
///
/// This is the single console option model: the resource editor, the per-user override, and the
/// console "Session options" popup (<c>Views/Home/Console.cshtml</c>) all render the same fields via
/// <c>Views/RDPResourceUserAuthorizations/_ConnectionDefaultsEditor.cshtml</c> and must stay identical.
/// </summary>
public class ConnectionDefaults
{
    /// <summary>Play remote sound (rdpsnd). Maps to the "Remote sound" checkbox.</summary>
    public bool Audio { get; set; } = true;

    /// <summary>Clipboard sync (cliprdr). Maps to the "Clipboard sync" checkbox.</summary>
    public bool Clipboard { get; set; } = true;

    /// <summary>Microphone redirection (audin). Maps to the "Microphone" checkbox.</summary>
    public bool Microphone { get; set; } = true;

    /// <summary>Camera redirection (rdpecam). Maps to the "Camera" checkbox.</summary>
    public bool Camera { get; set; } = true;

    /// <summary>
    /// GFX graphics pipeline + codec: "off" (legacy bitmaps) | "clearcodec" | "avc420" | "avc444".
    /// Maps to the <c>opt-gfx-mode</c> dropdown (and <c>window.RDP_GFX_MODE</c>).
    /// </summary>
    public string GfxMode { get; set; } = "avc420";

    /// <summary>
    /// The assembled RDP ExtendedInfoPacket performanceFlags bitmask (the value the editors build from
    /// the per-flag checkboxes). Default = all visual features on: ENABLE_FONT_SMOOTHING (0x80) |
    /// ENABLE_DESKTOP_COMPOSITION (0x100), with every DISABLE_* eye-candy bit cleared (so wallpaper,
    /// themes, window drag, menu animations, cursor shadow and cursor blink are all kept).
    /// </summary>
    public int PerformanceFlags { get; set; } = 0x80 | 0x100;
}
