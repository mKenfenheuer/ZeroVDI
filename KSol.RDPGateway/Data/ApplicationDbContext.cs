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
