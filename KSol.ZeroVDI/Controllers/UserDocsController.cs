using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// Serves the bundled end-user documentation (markdown under <c>wwwroot/docs/user</c>), reachable
/// from the user dashboard's top-bar help icon. Available to any signed-in user; the administrator
/// guide lives separately at <see cref="DocsController"/> and is Admin/Auditor-only.
///
/// Rendering, link rewriting, and nav-tree building are shared with the admin set via
/// <see cref="DocsService"/> (resolved for <see cref="DocsSection.User"/>); this controller only maps
/// the catch-all route to a view.
/// </summary>
[Authorize]
[Route("docs")]
public class UserDocsController : Controller
{
    private readonly DocsService _docs;

    public UserDocsController(DocsServiceFactory docs)
    {
        _docs = docs.For(DocsSection.User);
    }

    // Catch-all: matches any depth, including the empty root path.
    [HttpGet("{*path}")]
    public IActionResult Page(string? path)
    {
        var page = _docs.Render(path);
        if (page == null)
        {
            return View("NotConfigured");
        }

        if (!page.Found)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("DocNotFound", page);
        }

        return View(new DocsViewModel
        {
            Html = page.Html,
            Title = page.Title,
            CurrentUrl = page.CurrentUrl,
            Nav = page.Nav,
            Breadcrumbs = page.Breadcrumbs,
            UrlBase = _docs.UrlBase,
        });
    }
}
