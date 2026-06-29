using System.Text.RegularExpressions;
using Markdig;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// The two documentation sets. Each maps to its own subfolder under <c>wwwroot/docs</c> and its own
/// URL base, and is gated differently: <see cref="Admin"/> is Admin/Auditor-only, <see cref="User"/>
/// is for any signed-in user.
/// </summary>
public enum DocsSection
{
    Admin,
    User,
}

/// <summary>
/// Reads and renders bundled documentation from <c>wwwroot/docs</c>. The docs are split into two
/// independent sets — <c>wwwroot/docs/admin</c> (administrator guide, served under <c>/admin/docs</c>)
/// and <c>wwwroot/docs/user</c> (end-user guide, served under <c>/docs</c>) — each represented by its
/// own <see cref="DocsService"/> instance resolved via <see cref="DocsServiceFactory"/>.
///
/// Markdown may be nested to any depth; URLs mirror the folder structure with the <c>.md</c>
/// extension stripped (e.g. <c>wwwroot/docs/admin/features/vdi-pools.md</c> →
/// <c>/admin/docs/features/vdi-pools</c>). A folder URL resolves to its <c>index.md</c> (or
/// <c>README.md</c>).
///
/// Path resolution is rooted at the section directory and validated against directory traversal, so a
/// crafted URL can never read files outside that root. Markdown is rendered with the Markdig
/// "advanced" pipeline plus auto-linking, and local <c>.md</c> links are rewritten to absolute,
/// extension-less docs URLs under this section's base (relative links resolved against the current
/// page's directory).
/// </summary>
public class DocsService
{
    private static readonly string[] IndexNames = { "index.md", "readme.md" };

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoIdentifiers()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private readonly string _docsRoot;

    /// <summary>The section this instance serves.</summary>
    public DocsSection Section { get; }

    /// <summary>The absolute URL base for this section's pages, e.g. <c>/admin/docs</c> or
    /// <c>/docs</c> (no trailing slash).</summary>
    public string UrlBase { get; }

    public DocsService(IWebHostEnvironment env, DocsSection section)
    {
        Section = section;
        var folder = section == DocsSection.Admin ? "admin" : "user";
        UrlBase = section == DocsSection.Admin ? "/admin/docs" : "/docs";
        _docsRoot = Path.GetFullPath(Path.Combine(env.WebRootPath, "docs", folder));
    }

    public bool DocsExist => Directory.Exists(_docsRoot);

    /// <summary>Renders the page for a request path, or null if the docs root is missing or no
    /// matching markdown file exists. The nav tree is always returned (for 404 rendering too).</summary>
    public DocPage? Render(string? path)
    {
        if (!DocsExist)
        {
            return null;
        }

        var url = NormalizeUrl(path);
        var nav = BuildNavTree(_docsRoot, "");

        var file = ResolveFile(path);
        if (file == null)
        {
            return new DocPage { Found = false, CurrentUrl = url, Nav = nav };
        }

        var markdown = File.ReadAllText(file);
        return new DocPage
        {
            Found = true,
            Html = Markdown.ToHtml(RewriteLinks(markdown, url), Pipeline),
            Title = ExtractTitle(markdown) ?? "Documentation",
            CurrentUrl = url,
            Nav = nav,
            Breadcrumbs = BuildBreadcrumbs(url),
        };
    }

    /// <summary>The contents tree only (cheap-ish); used by the sidebar view component.</summary>
    public List<DocNavNode> BuildNav() => DocsExist ? BuildNavTree(_docsRoot, "") : new();

    private string? ResolveFile(string? path)
    {
        var rel = NormalizeUrl(path);

        if (rel.Length == 0)
        {
            return ResolveIndex(_docsRoot);
        }

        var asFile = SafeCombine(rel + ".md");
        if (asFile != null && File.Exists(asFile))
        {
            return asFile;
        }

        var asDir = SafeCombine(rel);
        if (asDir != null && Directory.Exists(asDir))
        {
            return ResolveIndex(asDir);
        }

        return null;
    }

    private static string? ResolveIndex(string dir)
    {
        foreach (var name in IndexNames)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Combines a relative URL segment with the docs root and rejects anything that escapes
    /// it (directory traversal). Returns the full path, or null if outside the root.</summary>
    private string? SafeCombine(string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_docsRoot, normalized));
        var rootWithSep = _docsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _docsRoot
            : _docsRoot + Path.DirectorySeparatorChar;
        if (!full.Equals(_docsRoot, StringComparison.Ordinal) &&
            !full.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            return null;
        }
        return full;
    }

    private string RewriteLinks(string markdown, string currentUrl)
    {
        var slash = currentUrl.LastIndexOf('/');
        var currentDir = slash >= 0 ? currentUrl[..slash] : string.Empty;

        return Regex.Replace(
            markdown,
            @"\]\((?!https?:|mailto:|#)([^)\s]+?)\.md(#[^)]*)?\)",
            m =>
            {
                var resolved = ResolveRelativeUrl(currentDir, m.Groups[1].Value);
                return $"]({UrlBase}/{resolved}{m.Groups[2].Value})";
            });
    }

    private static string ResolveRelativeUrl(string currentDir, string target)
    {
        var baseDir = target.StartsWith('/') ? string.Empty : currentDir;
        var segments = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var seg in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
            {
                continue;
            }
            if (seg == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }
            segments.Add(seg);
        }
        return string.Join('/', segments);
    }

    private static string NormalizeUrl(string? path)
    {
        var rel = (path ?? string.Empty).Trim('/');
        if (rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel[..^3];
        }
        return rel;
    }

    private static string? ExtractTitle(string markdown)
    {
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("# "))
            {
                return line[2..].Trim();
            }
            if (line.Length > 0 && !line.StartsWith("#"))
            {
                break;
            }
        }
        return null;
    }

    private static List<Breadcrumb> BuildBreadcrumbs(string url)
    {
        var crumbs = new List<Breadcrumb> { new("Docs", "") };
        if (url.Length == 0)
        {
            return crumbs;
        }
        var acc = "";
        foreach (var seg in url.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = acc.Length == 0 ? seg : $"{acc}/{seg}";
            crumbs.Add(new(Humanize(seg), acc));
        }
        return crumbs;
    }

    /// <summary>Recursively builds the contents tree, sorting alphabetically and hiding
    /// index/readme files (represented by their containing folder).</summary>
    private static List<DocNavNode> BuildNavTree(string dir, string urlPrefix)
    {
        var nodes = new List<DocNavNode>();

        foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(sub);
            var url = urlPrefix.Length == 0 ? name : $"{urlPrefix}/{name}";
            nodes.Add(new DocNavNode
            {
                Title = Humanize(name),
                Url = ResolveIndex(sub) != null ? url : null,
                Children = BuildNavTree(sub, url),
                IsFolder = true,
            });
        }

        foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(f);
            if (IndexNames.Contains(fileName.ToLowerInvariant()))
            {
                continue;
            }
            var slug = Path.GetFileNameWithoutExtension(f);
            var url = urlPrefix.Length == 0 ? slug : $"{urlPrefix}/{slug}";
            nodes.Add(new DocNavNode
            {
                Title = ExtractTitle(File.ReadAllText(f)) ?? Humanize(slug),
                Url = url,
            });
        }

        return nodes;
    }

    private static string Humanize(string slug)
    {
        var cleaned = Regex.Replace(slug, @"^\d+[-_]", "");
        var words = cleaned.Replace('-', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w =>
            w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}

/// <summary>
/// Resolves the <see cref="DocsService"/> for a given <see cref="DocsSection"/>. Registered as a
/// singleton; the per-section instances are cached so each controller/view component can ask for the
/// admin or user docs without re-scanning paths on construction.
/// </summary>
public class DocsServiceFactory
{
    private readonly DocsService _admin;
    private readonly DocsService _user;

    public DocsServiceFactory(IWebHostEnvironment env)
    {
        _admin = new DocsService(env, DocsSection.Admin);
        _user = new DocsService(env, DocsSection.User);
    }

    public DocsService For(DocsSection section) =>
        section == DocsSection.Admin ? _admin : _user;
}

public class DocPage
{
    public bool Found { get; set; }
    public string Html { get; set; } = "";
    public string Title { get; set; } = "";
    public string CurrentUrl { get; set; } = "";
    public List<DocNavNode> Nav { get; set; } = new();
    public List<Breadcrumb> Breadcrumbs { get; set; } = new();
}

public class DocNavNode
{
    public string Title { get; set; } = "";
    /// <summary>Relative docs URL (no leading slash), or null for an unclickable folder header.</summary>
    public string? Url { get; set; }
    public bool IsFolder { get; set; }
    public List<DocNavNode> Children { get; set; } = new();
}

public record Breadcrumb(string Label, string Url);
