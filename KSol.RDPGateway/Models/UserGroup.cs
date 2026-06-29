using System.ComponentModel.DataAnnotations;

namespace KSol.RDPGateway.Models;

/// <summary>
/// A named group of users. Groups are an authorization grouping layer over the flat per-(user, resource)
/// grants: a resource can be authorized for a whole group, and every member inherits access. Personal,
/// secret state (stored SSO credentials, per-user console <see cref="ConnectionDefaults"/>) stays on the
/// per-user <see cref="RDPResourceUserAuthorization"/> — a group grants the <em>right to connect</em>,
/// not anyone's credentials. Effective access for a user is the union of their direct grants and the
/// grants of every group they belong to (see <c>ResourceAccessService</c>).
/// </summary>
public class UserGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(128)]
    public string Name { get; set; } = "";

    [StringLength(512)]
    public string? Description { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<UserGroupMembership> Memberships { get; set; } = new List<UserGroupMembership>();
    public ICollection<RDPResourceGroupAuthorization> ResourceAuthorizations { get; set; } = new List<RDPResourceGroupAuthorization>();
}
