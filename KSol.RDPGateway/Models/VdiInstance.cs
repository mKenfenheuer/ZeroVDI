using System.ComponentModel.DataAnnotations.Schema;

namespace KSol.RDPGateway.Models;

/// <summary>Lifecycle state of a provisioned clone.</summary>
public enum VdiInstanceState
{
    /// <summary>Clone/customization in progress; not yet connectable.</summary>
    Provisioning = 0,
    /// <summary>Ready and (for floating) free to be leased.</summary>
    Ready = 1,
    /// <summary>Currently leased to a user (floating) — owner is the lessee.</summary>
    Leased = 2,
    /// <summary>Lease returned; awaiting reset/destroy (floating).</summary>
    Returning = 3,
    /// <summary>Provisioning failed; needs cleanup.</summary>
    Failed = 4,
    /// <summary>Being torn down (assignment lost, floating destroy-recreate, manual deprovision).</summary>
    Deprovisioning = 5,
}

/// <summary>
/// One provisioned clone: the bridge between a <see cref="VdiPool"/> and the spawned
/// <see cref="RDPResource"/> (which is a <see cref="ResourceSource.VdiClone"/> row). Tracks the
/// Proxmox VM, its owner (the dedicated user, or the current floating lessee, or null when free),
/// and its lifecycle <see cref="State"/>.
/// </summary>
public class VdiInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string PoolId { get; set; } = "";
    [ForeignKey(nameof(PoolId))]
    public VdiPool? Pool { get; set; }

    /// <summary>The spawned resource users connect through. Null only briefly during initial create.</summary>
    public string? RDPResourceId { get; set; }
    [ForeignKey(nameof(RDPResourceId))]
    public RDPResource? RDPResource { get; set; }

    /// <summary>The Proxmox node the clone lives on (refreshed on migration like other Proxmox rows).</summary>
    public string? ProxmoxNode { get; set; }
    /// <summary>The clone's Proxmox VMID.</summary>
    public int ProxmoxVmId { get; set; }

    /// <summary>
    /// Dedicated: the user the clone belongs to (set for the life of the instance). Floating: the
    /// current lessee, or null when the instance is free.
    /// </summary>
    public string? OwnerUserId { get; set; }
    [ForeignKey(nameof(OwnerUserId))]
    public ApplicationUser? OwnerUser { get; set; }

    public VdiInstanceState State { get; set; } = VdiInstanceState.Provisioning;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLeasedUtc { get; set; }
}
