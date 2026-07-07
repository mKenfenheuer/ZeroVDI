using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.ZeroVDI.Models;

/// <summary>
/// Join row: a user belongs to a <see cref="UserGroup"/>. Unique on (GroupId, UserId).
/// </summary>
public class UserGroupMembership
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string GroupId { get; set; } = "";
    [ForeignKey(nameof(GroupId))]
    public UserGroup? Group { get; set; }

    public string UserId { get; set; } = "";
    [ForeignKey(nameof(UserId))]
    public ApplicationUser? User { get; set; }
}
