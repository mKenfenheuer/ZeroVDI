using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.ZeroVDI.Models;

/// <summary>What a recording rule matches on.</summary>
public enum RecordingRuleScope
{
    /// <summary>Matches every session (a baseline; usually low priority).</summary>
    Global = 0,
    /// <summary>Matches one user.</summary>
    User = 1,
    /// <summary>Matches one resource/machine.</summary>
    Resource = 2,
    /// <summary>Matches any user in an ASP.NET Identity role.</summary>
    Role = 3,
}

public enum RecordingRuleAction
{
    /// <summary>Record sessions that match this rule.</summary>
    Allow = 0,
    /// <summary>Do NOT record sessions that match this rule.</summary>
    Deny = 1,
}

/// <summary>
/// An ordered recording rule. The policy (<see cref="RDP.RecordingPolicy"/>) evaluates enabled rules by
/// ascending <see cref="Order"/>; the first whose scope matches the session decides record/skip. If no
/// rule matches, the configured global default applies. Firewall-style: most-specific-wins is achieved
/// by ordering specific rules (User/Resource/Role) ahead of broad ones (Global).
/// </summary>
public class RecordingRule
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Evaluation order (ascending). Lower = evaluated first.</summary>
    public int Order { get; set; }

    public RecordingRuleScope Scope { get; set; } = RecordingRuleScope.Global;
    public RecordingRuleAction Action { get; set; } = RecordingRuleAction.Allow;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When this rule wins with <see cref="RecordingRuleAction.Allow"/>, show the user a "this session is
    /// recorded" notice in the console. Ignored for Deny rules. Defaults true so recorded sessions are
    /// disclosed unless an admin opts out.
    /// </summary>
    public bool NotifyUser { get; set; } = true;

    /// <summary>Target user id (Scope == User).</summary>
    public string? UserId { get; set; }
    /// <summary>Target resource id (Scope == Resource).</summary>
    public string? RDPResourceId { get; set; }
    /// <summary>Target role name (Scope == Role).</summary>
    public string? RoleName { get; set; }

    /// <summary>Optional admin note.</summary>
    public string? Description { get; set; }
}
