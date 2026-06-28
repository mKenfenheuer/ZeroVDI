using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.RDPGateway.Models;

/// <summary>Where a resource's definition comes from.</summary>
public enum ResourceSource
{
    /// <summary>Created and maintained by an administrator through the web UI.</summary>
    Manual = 0,
    /// <summary>Discovered from and synchronized with a Proxmox VE backend.</summary>
    Proxmox = 1,
}

/// <summary>Operating system type of the resource's backing machine.</summary>
public enum OsType
{
    Windows = 0,
    WindowsServer = 1,
    Linux = 2,
    MacOS = 3,
}

/// <summary>How to power on a manual resource that is offline.</summary>
public enum WakeMethod
{
    None = 0,
    WakeOnLan = 1,
    Ipmi = 2,
}

/// <summary>Last known power state of a resource's backing machine.</summary>
public enum ResourcePowerState
{
    Unknown = 0,
    Running = 1,
    Stopped = 2,
    Suspended = 3,
    /// <summary>Transitional: a start was requested and we are waiting for it to become reachable.</summary>
    Starting = 4,
}

/// <summary>
/// A remote desktop a user can connect to through the gateway.
///
/// The resource's stable identity is its <see cref="Id"/> (a GUID), which is what the gateway
/// authorizes on and what is written as the <c>full address</c> of the generated .rdp file. The
/// actual network address the gateway tunnels to is <see cref="IpAddress"/>, resolved from the GUID
/// at connect time. Decoupling the two lets a Proxmox-backed VM change its IP between sessions while
/// keeping a single stable identity, and lets the gateway start the VM on demand.
/// </summary>
public class RDPResource
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string? Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// The host/IP the gateway connects the tunnel to. For <see cref="ResourceSource.Manual"/>
    /// resources this is entered by an admin; for <see cref="ResourceSource.Proxmox"/> resources it
    /// is refreshed from the QEMU guest agent.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>The RDP (TCP) port on the target. Defaults to 3389.</summary>
    public int Port { get; set; } = 3389;

    public ResourceSource Source { get; set; } = ResourceSource.Manual;

    // --- Proxmox linkage (only meaningful when Source == Proxmox) ---
    /// <summary>The backend (cluster) this VM belongs to. A resource is keyed by backend + node + VMID.</summary>
    public int? ProxmoxBackendId { get; set; }
    [ForeignKey(nameof(ProxmoxBackendId))]
    public ProxmoxBackend? ProxmoxBackend { get; set; }
    /// <summary>The Proxmox node (host) the VM lives on.</summary>
    public string? ProxmoxNode { get; set; }
    /// <summary>The Proxmox VM id (VMID).</summary>
    public int? ProxmoxVmId { get; set; }

    // --- Lifecycle ---
    public ResourcePowerState PowerState { get; set; } = ResourcePowerState.Unknown;
    /// <summary>UTC time of the last connect/disconnect activity; drives idle pausing.</summary>
    public DateTime? LastActivityUtc { get; set; }

    /// <summary>
    /// Configurable .rdp options for this resource. Persisted as an owned/JSON value; never null.
    /// </summary>
    public RdpOptions RdpOptions { get; set; } = new();

    /// <summary>
    /// For Proxmox resources, the raw JSON read from the VM notes/description field (the source the
    /// <see cref="RdpOptions"/> were parsed from). Kept for diagnostics and round-tripping.
    /// </summary>
    public string? ConfigJson { get; set; }

    /// <summary>Resource-wide connection defaults inherited by newly granted users.</summary>
    public ConnectionDefaults? DefaultConnectionDefaults { get; set; }

    // --- Manual resource lifecycle (only meaningful when Source == Manual) ---

    public OsType OsType { get; set; } = OsType.Windows;
    public WakeMethod WakeMethod { get; set; } = WakeMethod.None;

    /// <summary>MAC address for Wake-on-LAN (AA:BB:CC:DD:EE:FF).</summary>
    public string? WolMacAddress { get; set; }

    /// <summary>IPMI BMC address for remote power control.</summary>
    public string? IpmiHost { get; set; }
    public string? IpmiUser { get; set; }
    /// <summary>Encrypted via CredentialProtector.</summary>
    public string? ProtectedIpmiPassword { get; set; }

    /// <summary>SSH user for remote shutdown (Linux/macOS).</summary>
    public string? SshUser { get; set; }
    /// <summary>Encrypted SSH private key content.</summary>
    public string? ProtectedSshKey { get; set; }
    /// <summary>Custom shutdown command (defaults based on OsType if null).</summary>
    public string? ShutdownCommand { get; set; }

    List<RDPResourceUserAuthorization>? UserAuthorizations { get; set; }
}
