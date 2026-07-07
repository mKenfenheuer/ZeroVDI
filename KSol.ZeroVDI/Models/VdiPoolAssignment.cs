using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.ZeroVDI.Models;

/// <summary>
/// Entitles a principal (a single user OR a whole group) to a <see cref="VdiPool"/>. Exactly one of
/// <see cref="UserId"/>/<see cref="GroupId"/> is set. Effective entitlement is the union of direct
/// user assignments and group assignments — mirroring the direct ∪ group resource-access model
/// (see <c>ResourceAccessService</c>). Unique per (pool, user) and per (pool, group).
/// </summary>
public class VdiPoolAssignment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string PoolId { get; set; } = "";
    [ForeignKey(nameof(PoolId))]
    public VdiPool? Pool { get; set; }

    /// <summary>Set for a direct user assignment (null for a group assignment).</summary>
    public string? UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public ApplicationUser? User { get; set; }

    /// <summary>Set for a group assignment (null for a direct user assignment).</summary>
    public string? GroupId { get; set; }
    [ForeignKey(nameof(GroupId))]
    public UserGroup? Group { get; set; }
}
