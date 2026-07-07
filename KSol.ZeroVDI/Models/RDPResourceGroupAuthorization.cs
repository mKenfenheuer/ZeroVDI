using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.ZeroVDI.Models;

/// <summary>
/// Grants a whole <see cref="UserGroup"/> access to an <see cref="RDPResource"/>. Every member of the
/// group may connect to the resource. The group analogue of <see cref="RDPResourceUserAuthorization"/>,
/// but without per-user credential/console state (those remain personal). Unique on (GroupId, ResourceId).
/// </summary>
public class RDPResourceGroupAuthorization
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string GroupId { get; set; } = "";
    [ForeignKey(nameof(GroupId))]
    public UserGroup? Group { get; set; }

    public string RDPResourceId { get; set; } = "";
    [ForeignKey(nameof(RDPResourceId))]
    public RDPResource? RDPResource { get; set; }
}
