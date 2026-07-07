using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    // Encrypts sensitive string columns at rest (NtHash, Proxmox API secret). Null at design time
    // (migrations/scaffolding), where no encryption is needed because no data is read or written.
    private readonly CredentialProtector? _protector;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, CredentialProtector protector)
        : base(options)
    {
        _protector = protector;
    }

    public DbSet<RDPResource> RDPResources { get; set; }
    public DbSet<RDPResourceUserAuthorization> RDPResourceUserAuthorizations { get; set; }
    public DbSet<ProxmoxBackend> ProxmoxBackends { get; set; }
    public DbSet<Recording> Recordings { get; set; }
    public DbSet<RecordingRule> RecordingRules { get; set; }
    public DbSet<AuditEvent> AuditEvents { get; set; }
    public DbSet<DevicePolicy> DevicePolicies { get; set; }
    public DbSet<AppearanceSettings> AppearanceSettings { get; set; }
    public DbSet<UserGroup> UserGroups { get; set; }
    public DbSet<UserGroupMembership> UserGroupMemberships { get; set; }
    public DbSet<RDPResourceGroupAuthorization> RDPResourceGroupAuthorizations { get; set; }
    public DbSet<VdiPool> VdiPools { get; set; }
    public DbSet<VdiPoolAssignment> VdiPoolAssignments { get; set; }
    public DbSet<VdiInstance> VdiInstances { get; set; }
    public DbSet<Connector> Connectors { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Resource-wide console connect defaults (inherited by newly granted users), stored as a
        // single JSON column rather than a side table so the option set can evolve without a
        // migration per field.
        builder.Entity<RDPResource>().OwnsOne(r => r.DefaultConnectionDefaults, b => b.ToJson());

        // Per-(user, resource) console connect defaults, stored as a JSON column on the authorization
        // (same rationale as above). Stored credentials are plain encrypted-string columns.
        builder.Entity<RDPResourceUserAuthorization>()
            .OwnsOne(a => a.ConnectionDefaults, b => b.ToJson());

        // Audit trail: index the columns the admin viewer filters/sorts on (newest-first, by category,
        // by actor). The store is append-only at the application level.
        builder.Entity<AuditEvent>(e =>
        {
            e.HasIndex(a => a.TimestampUtc);
            e.HasIndex(a => a.Category);
            e.HasIndex(a => a.ActorUserId);
            e.Property(a => a.Action).HasMaxLength(64);
        });

        // User groups: an authorization grouping layer. Unique membership per (group, user) and unique
        // group-grant per (group, resource); cascade-delete the join rows when a group/user/resource goes.
        builder.Entity<UserGroup>(e =>
        {
            e.HasIndex(g => g.Name).IsUnique();
        });
        builder.Entity<UserGroupMembership>(e =>
        {
            e.HasIndex(m => new { m.GroupId, m.UserId }).IsUnique();
            e.HasOne(m => m.Group).WithMany(g => g.Memberships).HasForeignKey(m => m.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<RDPResourceGroupAuthorization>(e =>
        {
            e.HasIndex(g => new { g.GroupId, g.RDPResourceId }).IsUnique();
            e.HasOne(g => g.Group).WithMany(ug => ug.ResourceAuthorizations).HasForeignKey(g => g.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(g => g.RDPResource).WithMany().HasForeignKey(g => g.RDPResourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // VDI pools: clone-from-template provisioning policies. Connection defaults stored as JSON
        // (same rationale as RDPResource). Assignments mirror the direct ∪ group access model with a
        // unique (pool, user) / (pool, group) index; group/user/pool deletes cascade their join rows.
        // Instances are NOT cascade-deleted with the pool — they own a live Proxmox VM that the
        // provisioner/reconcile loop must tear down first (a raw row delete would orphan the VM).
        builder.Entity<VdiPool>(e =>
        {
            e.HasIndex(p => p.Name).IsUnique();
            e.OwnsOne(p => p.ConnectionDefaults, b => b.ToJson());
            e.HasOne(p => p.ProxmoxBackend).WithMany().HasForeignKey(p => p.ProxmoxBackendId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<VdiPoolAssignment>(e =>
        {
            e.HasIndex(a => new { a.PoolId, a.UserId }).IsUnique();
            e.HasIndex(a => new { a.PoolId, a.GroupId }).IsUnique();
            e.HasOne(a => a.Pool).WithMany(p => p.Assignments).HasForeignKey(a => a.PoolId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Group).WithMany().HasForeignKey(a => a.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<VdiInstance>(e =>
        {
            // One dedicated clone per (pool, owner). Filtered unique index so floating instances
            // (owner null while free, and many sharing a pool) are not constrained.
            e.HasIndex(i => new { i.PoolId, i.OwnerUserId }).IsUnique()
                .HasFilter("\"OwnerUserId\" IS NOT NULL");
            e.HasOne(i => i.Pool).WithMany(p => p.Instances).HasForeignKey(i => i.PoolId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.RDPResource).WithMany().HasForeignKey(i => i.RDPResourceId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(i => i.OwnerUser).WithMany().HasForeignKey(i => i.OwnerUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Connectors: remote proxy agents. Unique name; a Proxmox backend may optionally reach through one
        // (SetNull so deleting a connector reverts its backends to direct rather than blocking the delete).
        builder.Entity<Connector>(e =>
        {
            e.HasIndex(c => c.Name).IsUnique();
            e.HasIndex(c => c.AuthTokenHash);
        });
        builder.Entity<ProxmoxBackend>()
            .HasOne(b => b.Connector).WithMany().HasForeignKey(b => b.ConnectorId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.Entity<RDPResource>()
            .HasOne(r => r.ForcedConnector).WithMany().HasForeignKey(r => r.ForcedConnectorId)
            .OnDelete(DeleteBehavior.SetNull);

        // Encrypt sensitive columns at rest. These hold secrets that must never be plaintext in the DB:
        //  - ApplicationUser.NtHash: unsalted MD4 of the gateway password (offline-crackable / PtH if leaked).
        //  - ProxmoxBackend.ApiTokenSecret: full API credential for the Proxmox cluster.
        // The Protected* columns on RDPResource/RDPResourceUserAuthorization are already encrypted by the
        // CredentialProtector at the call sites, so they are NOT double-wrapped here.
        if (_protector != null)
        {
            var encrypt = new EncryptedStringConverter(_protector);
            builder.Entity<ApplicationUser>().Property(u => u.NtHash).HasConversion(encrypt);
            builder.Entity<ProxmoxBackend>().Property(b => b.ApiTokenSecret).HasConversion(encrypt);
            builder.Entity<Connector>().Property(c => c.RegistrationToken).HasConversion(encrypt);
        }
    }
}
