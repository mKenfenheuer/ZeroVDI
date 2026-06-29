using Microsoft.AspNetCore.Mvc;
using KSol.RDPGateway.RDP;

namespace KSol.RDPGateway.ViewComponents;

/// <summary>
/// Renders the collapsible documentation contents tree shown under the "Documentation" link in the
/// admin sidebar. Available on every admin page (not just docs pages) so users can jump straight to
/// any topic. The current docs URL (empty on non-docs pages) drives the active-item highlight.
/// </summary>
public class DocsNavViewComponent : ViewComponent
{
    private readonly DocsService _docs;

    public DocsNavViewComponent(DocsServiceFactory docs)
    {
        _docs = docs.For(DocsSection.Admin);
    }

    public IViewComponentResult Invoke(string currentUrl)
    {
        var model = new DocsNavModel
        {
            Nodes = _docs.BuildNav(),
            CurrentUrl = currentUrl ?? string.Empty,
            UrlBase = _docs.UrlBase,
        };
        return View(model);
    }
}

public class DocsNavModel
{
    public List<DocNavNode> Nodes { get; set; } = new();
    public string CurrentUrl { get; set; } = "";
    public string UrlBase { get; set; } = "/admin/docs";
}
