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

    // --- Single sign-on: stored VM credentials (DataProtection-encrypted envelopes) ---
    // Each is the base64 output of CredentialProtector.Protect; null = nothing stored. Never bind
    // these directly from a form — set/clear them only via the controller's SetCredentials action.
    public string? ProtectedUsername { get; set; }
    public string? ProtectedPassword { get; set; }
    public string? ProtectedDomain { get; set; }

    /// <summary>True when a VM password is stored for this (user, resource) pair.</summary>
    [NotMapped]
    public bool HasStoredCredentials => !string.IsNullOrEmpty(ProtectedPassword);

    /// <summary>
    /// Per-(user, resource) defaults for the in-browser console connect form. When set (together with
    /// stored credentials), the console auto-connects without showing the login overlay. Persisted as
    /// a JSON column; null = no defaults stored (show the form).
    /// </summary>
    public ConnectionDefaults? ConnectionDefaults { get; set; }
}