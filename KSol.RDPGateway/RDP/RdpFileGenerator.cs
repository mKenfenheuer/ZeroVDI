using System.Globalization;
using System.Text;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Builds the contents of a Windows .rdp connection file for an <see cref="RDPResource"/>,
/// pre-configured to route through this gateway. Shared by the web UI download endpoint and the
/// RemoteApp &amp; Desktop Connections (RDWeb) workspace feed.
///
/// The display/redirection/experience lines are driven by the resource's <see cref="RdpOptions"/>;
/// the routing lines (<c>full address</c>, the gateway* keys) are always emitted. The
/// <c>full address</c> is the resource's stable <see cref="RDPResource.Id"/> (a GUID) — the gateway
/// resolves that id to the backing machine's current address at connect time.
/// </summary>
public class RdpFileGenerator
{
    /// <summary>
    /// Generates the .rdp file body for the given resource.
    /// </summary>
    /// <param name="resource">The resource to connect to.</param>
    /// <param name="gatewayHost">The public host name of this gateway (RDG endpoint).</param>
    /// <param name="userName">The username to pre-fill, if known.</param>
    public string Generate(RDPResource resource, string gatewayHost, string? userName = null)
    {
        var o = resource.RdpOptions ?? new RdpOptions();

        // The client connects to the resource by its stable id; the gateway maps it to the real host.
        var fullAddress = resource.Id;

        var sb = new StringBuilder();

        // Display
        Line(sb, "screen mode id", o.ScreenModeId);
        Line(sb, "use multimon", Bit(o.UseMultimon));
        Line(sb, "desktopwidth", o.DesktopWidth);
        Line(sb, "desktopheight", o.DesktopHeight);
        Line(sb, "session bpp", o.SessionBpp);
        Line(sb, "winposstr", "s", "0,1,0,0,800,600");

        // Experience
        Line(sb, "compression", Bit(o.Compression));
        Line(sb, "keyboardhook", 2);
        Line(sb, "audiocapturemode", Bit(o.AudioCaptureMode));
        Line(sb, "videoplaybackmode", 1);
        Line(sb, "connection type", o.ConnectionType);
        Line(sb, "networkautodetect", Bit(o.NetworkAutodetect));
        Line(sb, "bandwidthautodetect", Bit(o.BandwidthAutodetect));
        Line(sb, "displayconnectionbar", 1);
        Line(sb, "enableworkspacereconnect", 0);
        Line(sb, "remoteappmousemoveinject", 1);
        Line(sb, "disable wallpaper", Bit(o.DisableWallpaper));
        Line(sb, "allow font smoothing", Bit(o.AllowFontSmoothing));
        Line(sb, "allow desktop composition", Bit(o.AllowDesktopComposition));
        Line(sb, "disable full window drag", Bit(o.DisableFullWindowDrag));
        Line(sb, "disable menu anims", Bit(o.DisableMenuAnims));
        Line(sb, "disable themes", Bit(o.DisableThemes));
        Line(sb, "disable cursor setting", 0);
        Line(sb, "bitmapcachepersistenable", 1);

        // Target. The gateway resolves this id to the backing machine's real host and port at
        // connect time, so no :port is appended here.
        Line(sb, "full address", "s", fullAddress);

        // Redirections
        Line(sb, "audiomode", o.AudioMode);
        Line(sb, "redirectprinters", Bit(o.RedirectPrinters));
        Line(sb, "redirectlocation", Bit(o.RedirectLocation));
        Line(sb, "redirectcomports", Bit(o.RedirectComPorts));
        Line(sb, "redirectsmartcards", Bit(o.RedirectSmartCards));
        Line(sb, "redirectwebauthn", Bit(o.RedirectWebAuthn));
        Line(sb, "redirectclipboard", Bit(o.RedirectClipboard));
        Line(sb, "redirectposdevices", Bit(o.RedirectPosDevices));
        Line(sb, "drivestoredirect", "s", o.DriveStoreRedirect ?? string.Empty);

        // Session
        Line(sb, "autoreconnection enabled", Bit(o.AutoReconnectionEnabled));
        Line(sb, "authentication level", 2);
        Line(sb, "prompt for credentials", 0);
        Line(sb, "negotiate security layer", 1);
        Line(sb, "remoteapplicationmode", Bit(o.RemoteApplicationMode));
        Line(sb, "alternate shell", "s", o.AlternateShell ?? string.Empty);
        Line(sb, "shell working directory", "s", string.Empty);

        // Gateway routing (always)
        Line(sb, "gatewayhostname", "s", gatewayHost);
        Line(sb, "gatewayusagemethod", 1);
        Line(sb, "gatewaycredentialssource", 4);
        Line(sb, "gatewayprofileusagemethod", 1);
        Line(sb, "promptcredentialonce", 1);
        Line(sb, "gatewaybrokeringtype", 0);
        Line(sb, "use redirection server name", 0);
        Line(sb, "rdgiskdcproxy", 0);
        Line(sb, "kdcproxyname", "s", string.Empty);
        Line(sb, "enablerdsaadauth", 0);

        if (!string.IsNullOrEmpty(userName))
        {
            Line(sb, "username", "s", userName);
        }

        return sb.ToString();
    }

    private static int Bit(bool value) => value ? 1 : 0;

    /// <summary>Writes an integer (<c>i</c>) setting line.</summary>
    private static void Line(StringBuilder sb, string key, int value)
        => sb.AppendLine($"{key}:i:{value.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>Writes a setting line of the given type (<c>s</c> string or <c>i</c> int).</summary>
    private static void Line(StringBuilder sb, string key, string type, string value)
        => sb.AppendLine($"{key}:{type}:{value}");
}
