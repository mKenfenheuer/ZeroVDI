using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// Serves the bundled administrator documentation (markdown under <c>wwwroot/docs/admin</c>). All
/// reading, rendering, and nav-tree building lives in <see cref="DocsService"/> (shared with the
/// sidebar contents view component); this controller just maps the catch-all route to a view. Gated
/// to Admin and Auditor. The end-user documentation set is served separately by
/// <see cref="UserDocsController"/>.
/// </summary>
[Authorize(Roles = "Admin,Auditor")]
[Route("admin/docs")]
public class DocsController : Controller
{
    private readonly DocsService _docs;

    public DocsController(DocsServiceFactory docs)
    {
        _docs = docs.For(DocsSection.Admin);
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

public class DocsViewModel
{
    public string Html { get; set; } = "";
    public string Title { get; set; } = "";
    public string CurrentUrl { get; set; } = "";
    public List<DocNavNode> Nav { get; set; } = new();
    public List<Breadcrumb> Breadcrumbs { get; set; } = new();
    /// <summary>Absolute URL base for this docs section, e.g. <c>/admin/docs</c> or <c>/docs</c>.</summary>
    public string UrlBase { get; set; } = "/admin/docs";
}
