using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.RDPGateway.Models;

/// <summary>
/// A remote proxy agent (the <c>KSol.ZeroVDI.Connector</c> console app) deployed inside a network the
/// gateway cannot reach directly. It enrolls GitLab-runner style — the admin mints a one-time
/// <see cref="RegistrationToken"/>, the agent redeems it once for a long-lived auth token (only the
/// <see cref="AuthTokenHash"/> is persisted) — then holds an <i>outbound</i> WebSocket control channel
/// open. On demand the gateway asks it to open further outbound WebSocket data channels that carry plain
/// TCP (for tunnelled RDP) or serve as the transport for an <see cref="HttpClient"/> (for a private
/// Proxmox API). The connector also answers reachability/RTT probes over the control channel so the
/// gateway can pick the fastest path to a host at connect time.
///
/// A connector is admin-managed config, mirroring <see cref="ProxmoxBackend"/>/<see cref="RDPResource"/>
/// conventions: stable string-GUID identity, unique name, encrypted enrollment secret.
/// </summary>
public class Connector
{
    /// <summary>Stable identity, decoupled from the connector's (unknown, NATed) network address.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Friendly name shown in the UI (e.g. "Berlin datacenter").</summary>
    [Required]
    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    /// <summary>Whether this connector is eligible for path selection and may connect its control channel.</summary>
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// One-time enrollment secret shown to the admin. The agent presents it once to <c>/agent/register</c>
    /// to obtain its long-lived auth token, after which <see cref="RegistrationTokenUsedUtc"/> is stamped
    /// and it can no longer be redeemed. Encrypted at rest (see <c>ApplicationDbContext.OnModelCreating</c>).
    /// </summary>
    [Display(Name = "Registration token")]
    public string? RegistrationToken { get; set; }

    /// <summary>When the registration token was redeemed. Null = not yet enrolled.</summary>
    public DateTime? RegistrationTokenUsedUtc { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the long-lived auth token the agent presents as a bearer credential on its control
    /// and data channels. Only the hash is stored; the plaintext is shown to the agent exactly once at
    /// redemption. Null until enrolled.
    /// </summary>
    public string? AuthTokenHash { get; set; }

    /// <summary>
    /// Optional allow-scope: newline/comma-separated host names or CIDR patterns this connector may be used
    /// for. Empty ⇒ candidate for every host. Limits which hosts the connector is RTT-probed against.
    /// </summary>
    [Display(Name = "Allow scope (hosts / CIDRs, one per line)")]
    public string? AllowScope { get; set; }

    /// <summary>When the control channel was last seen (updated on connect + heartbeat). Transient-ish.</summary>
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>Remote address the control channel last connected from (diagnostics).</summary>
    public string? LastRemoteAddress { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True once the agent has redeemed its registration token.</summary>
    public bool IsEnrolled => AuthTokenHash != null;
}
