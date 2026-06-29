using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.Data;

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
    public DbSet<UserGroup> UserGroups { get; set; }
    public DbSet<UserGroupMembership> UserGroupMemberships { get; set; }
    public DbSet<RDPResourceGroupAuthorization> RDPResourceGroupAuthorizations { get; set; }

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
        }
    }
}
