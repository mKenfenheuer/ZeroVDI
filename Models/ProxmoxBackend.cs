using System.ComponentModel.DataAnnotations;

namespace KSol.RDPGateway.Models;

/// <summary>What to do to an idle VM when the idle timeout elapses.</summary>
public enum PauseAction
{
    /// <summary>qm suspend — suspend to RAM. Fast resume; RAM stays reserved.</summary>
    Suspend = 0,
    /// <summary>qm stop — shut the VM down. Frees RAM; slower first connect.</summary>
    Stop = 1,
    /// <summary>qm suspend --todisk — hibernate to disk. Frees RAM; persists session.</summary>
    Hibernate = 2,
}

/// <summary>
/// A configured Proxmox VE backend (cluster/host). There may be several; each carries its own API
/// connection details and its own VDI lifecycle policy (idle timeout, pause action, start timeout,
/// default RDP port). Resources reference the backend they belong to via
/// <see cref="RDPResource.ProxmoxBackendId"/>. Edited through the admin GUI.
/// </summary>
public class ProxmoxBackend
{
    [Key]
    public int Id { get; set; }

    /// <summary>Friendly name shown in the UI (e.g. "Lab cluster").</summary>
    [Required]
    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL of the Proxmox API, e.g. <c>https://pve.example.com:8006</c>.</summary>
    [Display(Name = "Proxmox host URL")]
    public string? Host { get; set; }

    /// <summary>API token id in the form <c>user@realm!tokenname</c>.</summary>
    [Display(Name = "API token ID")]
    public string? ApiTokenId { get; set; }

    /// <summary>API token secret (UUID).</summary>
    [Display(Name = "API token secret")]
    public string? ApiTokenSecret { get; set; }

    /// <summary>Whether to validate the Proxmox TLS certificate. Off for self-signed lab setups.</summary>
    [Display(Name = "Verify TLS certificate")]
    public bool VerifyTls { get; set; } = false;

    // --- Per-backend VDI lifecycle policy ---

    /// <summary>Default RDP port assigned to discovered VMs.</summary>
    [Display(Name = "Default RDP port")]
    public int DefaultRdpPort { get; set; } = 3389;

    /// <summary>Hours a resource may sit with no active session before it is paused.</summary>
    [Display(Name = "Idle timeout (hours)")]
    public int IdleTimeoutHours { get; set; } = 24;

    /// <summary>What pausing an idle VM does.</summary>
    [Display(Name = "Pause action")]
    public PauseAction PauseAction { get; set; } = PauseAction.Suspend;

    /// <summary>How long to wait for a started VM to become reachable before giving up.</summary>
    [Display(Name = "Start timeout (seconds)")]
    public int StartTimeoutSeconds { get; set; } = 120;

    /// <summary>Whether this backend is complete enough to talk to Proxmox.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(ApiTokenId)
        && !string.IsNullOrWhiteSpace(ApiTokenSecret);
}
