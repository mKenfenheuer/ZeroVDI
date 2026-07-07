namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Detects requests coming from the Electron desktop client (KSol.ZeroVDI.Desktop) via its User-Agent,
/// which includes "Electron/&lt;version&gt;" by default — Electron does not strip this, unlike some
/// embedders. Used to hide/block admin surfaces: the desktop app is scoped to VDI console access only,
/// administration is done through a regular browser.
/// </summary>
public static class DesktopClient
{
    public static bool IsElectron(HttpContext context)
        => context.Request.Headers.UserAgent.ToString().Contains("Electron/", StringComparison.Ordinal);
}
