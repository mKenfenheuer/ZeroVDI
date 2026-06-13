using System.Text;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Builds the contents of a Windows .rdp connection file for an <see cref="RDPResource"/>,
/// pre-configured to route through this gateway. Shared by the web UI download endpoint and the
/// RemoteApp &amp; Desktop Connections (RDWeb) workspace feed.
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
        var fullAddress = resource.ResourceIdentifier ?? string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("screen mode id:i:2");
        sb.AppendLine("use multimon:i:0");
        sb.AppendLine("desktopwidth:i:1920");
        sb.AppendLine("desktopheight:i:1080");
        sb.AppendLine("session bpp:i:32");
        sb.AppendLine("winposstr:s:0,1,0,0,800,600");
        sb.AppendLine("compression:i:1");
        sb.AppendLine("keyboardhook:i:2");
        sb.AppendLine("audiocapturemode:i:0");
        sb.AppendLine("videoplaybackmode:i:1");
        sb.AppendLine("connection type:i:7");
        sb.AppendLine("networkautodetect:i:1");
        sb.AppendLine("bandwidthautodetect:i:1");
        sb.AppendLine("displayconnectionbar:i:1");
        sb.AppendLine("enableworkspacereconnect:i:0");
        sb.AppendLine("remoteappmousemoveinject:i:1");
        sb.AppendLine("disable wallpaper:i:0");
        sb.AppendLine("allow font smoothing:i:0");
        sb.AppendLine("allow desktop composition:i:0");
        sb.AppendLine("disable full window drag:i:1");
        sb.AppendLine("disable menu anims:i:1");
        sb.AppendLine("disable themes:i:0");
        sb.AppendLine("disable cursor setting:i:0");
        sb.AppendLine("bitmapcachepersistenable:i:1");
        sb.AppendLine("full address:s:" + fullAddress);
        sb.AppendLine("audiomode:i:0");
        sb.AppendLine("redirectprinters:i:1");
        sb.AppendLine("redirectlocation:i:0");
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:1");
        sb.AppendLine("redirectwebauthn:i:1");
        sb.AppendLine("redirectclipboard:i:1");
        sb.AppendLine("redirectposdevices:i:0");
        sb.AppendLine("drivestoredirect:s:");
        sb.AppendLine("autoreconnection enabled:i:1");
        sb.AppendLine("authentication level:i:2");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("negotiate security layer:i:1");
        sb.AppendLine("remoteapplicationmode:i:0");
        sb.AppendLine("alternate shell:s:");
        sb.AppendLine("shell working directory:s:");
        sb.AppendLine("gatewayhostname:s:" + gatewayHost);
        sb.AppendLine("gatewayusagemethod:i:1");
        sb.AppendLine("gatewaycredentialssource:i:4");
        sb.AppendLine("gatewayprofileusagemethod:i:1");
        sb.AppendLine("promptcredentialonce:i:1");
        sb.AppendLine("gatewaybrokeringtype:i:0");
        sb.AppendLine("use redirection server name:i:0");
        sb.AppendLine("rdgiskdcproxy:i:0");
        sb.AppendLine("kdcproxyname:s:");
        sb.AppendLine("enablerdsaadauth:i:0");
        if (!string.IsNullOrEmpty(userName))
        {
            sb.AppendLine("username:s:" + userName);
        }

        return sb.ToString();
    }
}
