using Microsoft.AspNetCore.Html;

namespace KSol.ZeroVDI.Models;

/// <summary>
/// View model for the shared <c>_ListToolbar</c> partial: a GET search form with an optional
/// filter slot and a result count. Generalises the filter bar first built on the Audit page.
/// </summary>
public class ListToolbarModel
{
    /// <summary>Current search term (bound to the <c>q</c> query-string parameter).</summary>
    public string? Query { get; set; }

    /// <summary>Placeholder shown in the empty search box.</summary>
    public string Placeholder { get; set; } = "Search…";

    /// <summary>Optional label rendered above the search box.</summary>
    public string? Label { get; set; }

    /// <summary>Total matching rows, shown on the right. Null hides the count.</summary>
    public int? TotalCount { get; set; }

    /// <summary>Singular noun for the count, e.g. "user" → "3 users".</summary>
    public string Noun { get; set; } = "result";

    /// <summary>Extra filter controls (e.g. a category select) rendered before the buttons.</summary>
    public IHtmlContent? FiltersHtml { get; set; }

    /// <summary>Whether any non-search filter is active, so Reset shows even with an empty query.</summary>
    public bool HasActiveFilters { get; set; }
}

/// <summary>
/// View model for the shared <c>_Pagination</c> partial. Carries the current page plus the
/// route values needed to preserve active filters across page links.
/// </summary>
public class PaginationModel
{
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public int TotalCount { get; set; }

    /// <summary>Extra route values (e.g. q, category) merged into Previous/Next links.</summary>
    public IDictionary<string, string?> RouteValues { get; set; } = new Dictionary<string, string?>();
}
