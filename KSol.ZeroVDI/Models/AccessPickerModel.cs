namespace KSol.ZeroVDI.Models;

/// <summary>
/// Backing model for the shared <c>_AccessPicker</c> partial: a searchable add-control (filter input +
/// candidate list + Add button) that POSTs a single chosen id to a supplied action. Used identically by
/// the user page (add to group / grant resource), the resource page (grant user / grant group) and the
/// group manage page, so the "grant something" interaction is the same everywhere.
/// </summary>
public sealed class AccessPickerModel
{
    /// <summary>MVC action name the form posts to.</summary>
    public string FormAction { get; init; } = "";

    /// <summary>MVC controller for the action; null = current controller.</summary>
    public string? FormController { get; init; }

    /// <summary>Route id passed as <c>id</c> (the owning user/resource/group).</summary>
    public string? RouteId { get; init; }

    /// <summary>The form field name carrying the selected candidate value (e.g. "userId", "resourceId").</summary>
    public string FieldName { get; init; } = "id";

    /// <summary>Selectable candidates (value posted, label shown).</summary>
    public IEnumerable<(string Value, string Label)> Candidates { get; init; } = Array.Empty<(string, string)>();

    /// <summary>Placeholder for the empty option.</summary>
    public string Placeholder { get; init; } = "Select…";

    /// <summary>Label for the submit button.</summary>
    public string ButtonLabel { get; init; } = "Add";
}

/// <summary>Backing model for the shared <c>_AccessBadges</c> partial (Direct + via-Group provenance).</summary>
public sealed record AccessBadgesModel(bool Direct, List<(string Id, string Name)> ViaGroups);
