using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace KSol.RDPGateway.Models;

public class RDPResourceUserAuthorization
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string? Id { get; set; }
    public string? UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public ApplicationUser? User { get; set; }
    public string? RDPResourceId { get; set; }
    [ForeignKey(nameof(RDPResourceId))]
    public RDPResource? RDPResource { get; set; }
}