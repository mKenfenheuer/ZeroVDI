namespace KSol.ZeroVDI.RDP;

/// <summary>
/// The Electron desktop client is scoped to VDI console access only — administration happens through a
/// regular browser. Blocks the admin surface for requests identified as coming from the desktop app (see
/// <see cref="DesktopClient"/>) with a 404, same as an unmapped route, so the app's existence isn't hinted
/// at. Runs after routing/auth so <see cref="HttpContext.GetEndpoint"/> reflects the matched controller.
/// </summary>
public sealed class DesktopAdminBlockMiddleware
{
    private readonly RequestDelegate _next;

    public DesktopAdminBlockMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (DesktopClient.IsElectron(context)
            && context.Request.Path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }
}
