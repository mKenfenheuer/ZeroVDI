using KSol.ZeroVDI.RDP;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace KSol.ZeroVDI.TagHelpers;

/// <summary>
/// Stamps the request's Content-Security-Policy nonce onto every <c>&lt;script&gt;</c> the application
/// renders. Doing it here rather than in each view means a new inline script cannot silently fail to
/// run (or, worse, invite someone to re-add <c>'unsafe-inline'</c> to make it work) — the policy and
/// the markup can never drift apart.
///
/// Runs after the framework's own script tag helper so it does not interfere with
/// <c>asp-append-version</c> or fallback-source handling.
/// </summary>
[HtmlTargetElement("script")]
public sealed class ScriptNonceTagHelper : TagHelper
{
    public override int Order => 1000;

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var http = ViewContext?.HttpContext;
        if (http == null || output.Attributes.ContainsName("nonce")) return;
        output.Attributes.SetAttribute("nonce", CspNonce.Get(http));
    }
}
