namespace KSol.RDPGateway.Models;

/// <summary>
/// The per-(user, resource) defaults for the in-browser RDP console connect form, stored on the
/// <see cref="RDPResourceUserAuthorization"/>. When present, the console auto-connects using these
/// instead of showing the login overlay.
///
/// This is deliberately a SMALLER, console-shaped option set, distinct from <see cref="RdpOptions"/>
/// (which is .rdp-file-shaped: display/redirection/experience). These mirror the controls in
/// <c>Views/Home/Console.cshtml</c>'s connect form.
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
    /// The assembled RDP ExtendedInfoPacket performanceFlags bitmask (the value Console.cshtml builds
    /// from the per-flag checkboxes). 0 = protocol default (best visuals).
    /// </summary>
    public int PerformanceFlags { get; set; } = 0;
}
