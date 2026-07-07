using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.ZeroVDI.Models;

/// <summary>Whether a pool gives each user their own persistent desktop or leases from a shared set.</summary>
public enum VdiPoolKind
{
    /// <summary>One user ↔ one clone, provisioned once, persisting across sessions.</summary>
    Dedicated = 0,
    /// <summary>A shared set of clones; a user is leased a free one per session and it is reclaimed on return.</summary>
    Floating = 1,
}

/// <summary>How a clone's disk is created from the template.</summary>
public enum CloneMode
{
    /// <summary>Independent full copy of the template disk. Persistent, migrates freely, more storage.</summary>
    Full = 0,
    /// <summary>Copy-on-write off the template base disk. Fast, space-efficient, tied to the template.</summary>
    Linked = 1,
}

/// <summary>How a freshly cloned VM is given its own identity (hostname/SID/domain) before first use.</summary>
public enum VdiIdentityMode
{
    /// <summary>No customization — the template is pre-generalized (sysprep / cloud-init self-config).</summary>
    None = 0,
    /// <summary>Inject Proxmox cloud-init (ciuser/cipassword/hostname/sshkeys) before first boot.</summary>
    CloudInit = 1,
    /// <summary>After boot, rename (and optionally domain-join) via the QEMU guest agent. Windows fallback.</summary>
    GuestAgent = 2,
}

/// <summary>What happens to a floating pool VM when a user disconnects (returns the lease).</summary>
public enum FloatingResetMode
{
    /// <summary>Destroy the VM and let the reconcile loop re-clone a fresh one from the template.</summary>
    DestroyRecreate = 0,
}

/// <summary>
/// A VDI provisioning policy: members of the assigned users/groups each get a desktop cloned from a
/// Proxmox template on a backend. Clones are created lazily on first connect (surfaced through the
/// existing readiness UI). A pool is either <see cref="VdiPoolKind.Dedicated"/> (one persistent clone
/// per user) or <see cref="VdiPoolKind.Floating"/> (a shared, reclaimed set). Entitlement is the union
/// of direct user assignments and group assignments — the same direct ∪ group model as resource
/// access (see <c>ResourceAccessService</c>). Spawned clones are <see cref="RDPResource"/> rows with
/// <see cref="ResourceSource.VdiClone"/>, linked back via <see cref="VdiInstance"/>.
/// </summary>
public class VdiPool
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(128)]
    public string Name { get; set; } = "";

    [StringLength(512)]
    public string? Description { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // --- Source template ---
    /// <summary>The Proxmox backend (cluster) the template lives on and clones are created on.</summary>
    public int ProxmoxBackendId { get; set; }
    [ForeignKey(nameof(ProxmoxBackendId))]
    public ProxmoxBackend? ProxmoxBackend { get; set; }

    /// <summary>The Proxmox template VMID to clone from.</summary>
    public int TemplateVmId { get; set; }

    // --- Provisioning policy ---
    public VdiPoolKind Kind { get; set; } = VdiPoolKind.Dedicated;
    public CloneMode CloneMode { get; set; } = CloneMode.Full;

    /// <summary>Target node for clones; null = same node as the template.</summary>
    public string? TargetNode { get; set; }
    /// <summary>Target storage for clones; null = Proxmox default / same as template.</summary>
    public string? TargetStorage { get; set; }

    /// <summary>Optional inclusive VMID allocation window for clones; null = use cluster/nextid.</summary>
    public int? VmidRangeStart { get; set; }
    public int? VmidRangeEnd { get; set; }

    /// <summary>Clone name pattern. Tokens: <c>{pool}</c>, <c>{user}</c>, <c>{n}</c>.</summary>
    public string NamePattern { get; set; } = "{pool}-{user}";

    /// <summary>Maximum number of clones for a <see cref="VdiPoolKind.Floating"/> pool. Ignored otherwise.</summary>
    public int MaxSize { get; set; } = 0;

    /// <summary>Connection defaults inherited by the spawned resources (stored as JSON).</summary>
    public ConnectionDefaults? ConnectionDefaults { get; set; }

    /// <summary>RDP port assigned to spawned resources.</summary>
    public int Port { get; set; } = 3389;

    public OsType OsType { get; set; } = OsType.Windows;

    // --- Per-clone identity / customization ---
    public VdiIdentityMode IdentityMode { get; set; } = VdiIdentityMode.None;

    /// <summary>Hostname pattern applied to each clone. Tokens: <c>{pool}</c>, <c>{user}</c>, <c>{n}</c>.</summary>
    public string? HostnamePattern { get; set; } = "{pool}-{user}";

    /// <summary>Cloud-init user account (CloudInit mode). Ignored when <see cref="GenerateCredentials"/>.</summary>
    public string? CiUser { get; set; }
    /// <summary>Cloud-init password, encrypted via CredentialProtector (CloudInit mode). Ignored when <see cref="GenerateCredentials"/>.</summary>
    public string? ProtectedCiPassword { get; set; }
    /// <summary>Cloud-init SSH public keys (CloudInit mode, Linux).</summary>
    public string? CiSshKeys { get; set; }

    /// <summary>
    /// CloudInit mode only: instead of injecting a single shared <see cref="CiUser"/>/<see cref="ProtectedCiPassword"/>
    /// into every clone, derive a unique random username and password per owner from their account data
    /// (cloudbase-init on Windows / cloud-init on Linux creates the account). The generated credentials are
    /// also stored as the owner's per-resource SSO so the gateway logs them in automatically.
    /// </summary>
    public bool GenerateCredentials { get; set; }

    /// <summary>Windows domain to join (GuestAgent mode); null = no domain join.</summary>
    public string? DomainName { get; set; }
    /// <summary>Optional OU distinguished name for the computer account.</summary>
    public string? DomainOu { get; set; }
    /// <summary>Domain-join account.</summary>
    public string? DomainJoinUser { get; set; }
    /// <summary>Domain-join password, encrypted via CredentialProtector.</summary>
    public string? ProtectedDomainJoinPassword { get; set; }

    // --- Floating policy ---
    public FloatingResetMode FloatingReset { get; set; } = FloatingResetMode.DestroyRecreate;

    public ICollection<VdiPoolAssignment> Assignments { get; set; } = new List<VdiPoolAssignment>();
    public ICollection<VdiInstance> Instances { get; set; } = new List<VdiInstance>();
}
