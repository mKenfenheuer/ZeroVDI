namespace KSol.RDPGateway.Models;

/// <summary>
/// The configurable subset of a Windows .rdp connection file, stored per <see cref="RDPResource"/>.
/// Defaults match the gateway's historically hard-coded template so existing behaviour is preserved
/// when a resource is created without customizing anything.
///
/// Only settings that are meaningful to expose are modelled; the gateway routing lines
/// (<c>full address</c>, <c>gatewayhostname</c>, <c>username</c>, the gateway* keys) are always
/// emitted by <see cref="RDP.RdpFileGenerator"/> and are not part of this options object.
/// </summary>
public class RdpOptions
{
    // Display
    /// <summary>screen mode id: 1 = windowed, 2 = full screen.</summary>
    public int ScreenModeId { get; set; } = 2;
    public int DesktopWidth { get; set; } = 1920;
    public int DesktopHeight { get; set; } = 1080;
    /// <summary>use multimon: span the session across all monitors.</summary>
    public bool UseMultimon { get; set; } = false;
    /// <summary>session bpp: colour depth (15/16/24/32).</summary>
    public int SessionBpp { get; set; } = 32;

    // Redirections
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectPrinters { get; set; } = true;
    public bool RedirectSmartCards { get; set; } = true;
    public bool RedirectWebAuthn { get; set; } = true;
    /// <summary>redirectcomports: redirect local serial (COM) ports.</summary>
    public bool RedirectComPorts { get; set; } = false;
    /// <summary>redirectposdevices: redirect point-of-sale devices.</summary>
    public bool RedirectPosDevices { get; set; } = false;
    /// <summary>redirectlocation: share the client's location with the session.</summary>
    public bool RedirectLocation { get; set; } = false;
    /// <summary>drivestoredirect: which local drives to redirect (e.g. "*" for all, "" for none).</summary>
    public string DriveStoreRedirect { get; set; } = string.Empty;

    // Audio
    /// <summary>audiomode: 0 = play on client, 1 = play on remote, 2 = do not play.</summary>
    public int AudioMode { get; set; } = 0;
    /// <summary>audiocapturemode: 1 = capture and redirect the local microphone.</summary>
    public bool AudioCaptureMode { get; set; } = false;

    // Experience
    /// <summary>connection type: 7 = autodetect. Influences the default experience settings.</summary>
    public int ConnectionType { get; set; } = 7;
    public bool NetworkAutodetect { get; set; } = true;
    public bool BandwidthAutodetect { get; set; } = true;
    public bool Compression { get; set; } = true;
    public bool DisableWallpaper { get; set; } = false;
    public bool AllowFontSmoothing { get; set; } = false;
    public bool AllowDesktopComposition { get; set; } = false;
    public bool DisableFullWindowDrag { get; set; } = true;
    public bool DisableMenuAnims { get; set; } = true;
    public bool DisableThemes { get; set; } = false;
    public bool AutoReconnectionEnabled { get; set; } = true;

    /// <summary>remoteapplicationmode: 1 launches a RemoteApp instead of a full desktop.</summary>
    public bool RemoteApplicationMode { get; set; } = false;
    /// <summary>alternate shell: the RemoteApp program path (when RemoteApplicationMode is on).</summary>
    public string AlternateShell { get; set; } = string.Empty;
}
