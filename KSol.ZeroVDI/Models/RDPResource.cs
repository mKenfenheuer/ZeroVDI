using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.ZeroVDI.Models;

/// <summary>Where a resource's definition comes from.</summary>
public enum ResourceSource
{
    /// <summary>Created and maintained by an administrator through the web UI.</summary>
    Manual = 0,
    /// <summary>Discovered from and synchronized with a Proxmox VE backend.</summary>
    Proxmox = 1,
    /// <summary>
    /// A VM cloned from a template by a <see cref="VdiPool"/>. Owned by the provisioner, not by
    /// <c>ProxmoxSyncService</c> (which ignores these rows so it does not prune or re-discover them).
    /// </summary>
    VdiClone = 2,
}

/// <summary>
/// Wire protocol the gateway speaks to the host. Selects the <c>IRdpResolver</c> that produces the
/// decrypted RDP stream the browser console + recorder consume — native RDP, or a bridge (e.g. VNC).
/// </summary>
public enum RdpProtocol
{
    /// <summary>Native RDP over NLA/CredSSP (the default).</summary>
    Rdp = 0,
    /// <summary>VNC/RFB host, bridged to an RDP stream by the gateway.</summary>
    Vnc = 1,
    /// <summary>SPICE host, bridged to an RDP stream by the gateway.</summary>
    Spice = 2,
}

/// <summary>Keyboard layout used to interpret key events for protocols that need it (e.g. the VNC bridge,
/// which receives layout-independent scancodes and must resolve them to characters).</summary>
public enum KeyboardLayout
{
    Us = 0,
    German = 1,
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

/// <summary>How to gracefully shut down a manual resource when it goes idle.</summary>
public enum ShutdownMethod
{
    None = 0,
    /// <summary>SSH into the host and run a shutdown command (Linux/macOS).</summary>
    Ssh = 1,
    /// <summary>Send an ACPI soft power-off over IPMI (shares the wake IPMI config).</summary>
    Ipmi = 2,
    /// <summary>Remote Windows shutdown via Samba's <c>net rpc shutdown</c>.</summary>
    Windows = 3,
}

/// <summary>Last known power state of a resource's backing machine.</summary>
public enum ResourcePowerState
{
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

    /// <summary>
    /// Wire protocol the gateway speaks to the host. <see cref="RdpProtocol.Rdp"/> (default) connects
    /// natively; <see cref="RdpProtocol.Vnc"/> bridges an RFB/VNC host into an RDP stream. Selects the
    /// resolver in <c>RdpWebSocketController</c>.
    /// </summary>
    public RdpProtocol Protocol { get; set; } = RdpProtocol.Rdp;

    /// <summary>
    /// Keyboard layout the host expects. Used by protocol bridges that receive layout-independent key
    /// scancodes (the VNC bridge) to resolve them to the correct characters. Ignored by native RDP (the
    /// client and host negotiate the layout themselves).
    /// </summary>
    public KeyboardLayout KeyboardLayout { get; set; } = KeyboardLayout.Us;

    /// <summary>
    /// When set, force this resource's connection through the named connector instead of letting the
    /// gateway pick the fastest path (direct vs. round-trip-time probing). Useful when the host is only
    /// reachable through one connector, or to pin traffic to a specific egress. Null = automatic selection.
    /// </summary>
    public string? ForcedConnectorId { get; set; }
    [ForeignKey(nameof(ForcedConnectorId))]
    public Connector? ForcedConnector { get; set; }

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

    /// <summary>
    /// For <see cref="ResourceSource.VdiClone"/> resources, the <see cref="VdiInstance"/> that owns
    /// this clone (and the pool it was provisioned from). Null for Manual/Proxmox resources.
    /// </summary>
    public string? VdiInstanceId { get; set; }

    // --- Lifecycle ---
    public ResourcePowerState PowerState { get; set; } = ResourcePowerState.Stopped;
    /// <summary>UTC time of the last connect/disconnect activity; drives idle pausing.</summary>
    public DateTime? LastActivityUtc { get; set; }

    /// <summary>
    /// For Proxmox resources, the raw JSON read from the VM notes/description field. Kept for
    /// diagnostics and round-tripping.
    /// </summary>
    public string? ConfigJson { get; set; }

    /// <summary>Resource-wide connection defaults inherited by newly granted users.</summary>
    public ConnectionDefaults? DefaultConnectionDefaults { get; set; }

    // --- Manual resource lifecycle (only meaningful when Source == Manual) ---

    public OsType OsType { get; set; } = OsType.Windows;
    public WakeMethod WakeMethod { get; set; } = WakeMethod.None;
    public ShutdownMethod ShutdownMethod { get; set; } = ShutdownMethod.None;

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

    /// <summary>Windows admin user for remote shutdown via <c>net rpc shutdown</c>.</summary>
    public string? WindowsUser { get; set; }
    /// <summary>Encrypted via CredentialProtector.</summary>
    public string? ProtectedWindowsPassword { get; set; }

    List<RDPResourceUserAuthorization>? UserAuthorizations { get; set; }
}
