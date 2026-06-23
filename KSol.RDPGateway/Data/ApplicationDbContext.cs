using KSol.RDPGateway.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<RDPResource> RDPResources { get; set; }
    public DbSet<RDPResourceUserAuthorization> RDPResourceUserAuthorizations { get; set; }
    public DbSet<ProxmoxBackend> ProxmoxBackends { get; set; }
    public DbSet<Recording> Recordings { get; set; }
    public DbSet<RecordingRule> RecordingRules { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Persist the per-resource RDP options as a single JSON column rather than a side table,
        // so the option set can evolve without a migration per field.
        builder.Entity<RDPResource>().OwnsOne(r => r.RdpOptions, b => b.ToJson());

        // Per-(user, resource) console connect defaults, stored as a JSON column on the authorization
        // (same rationale as RdpOptions above). Stored credentials are plain encrypted-string columns.
        builder.Entity<RDPResourceUserAuthorization>()
            .OwnsOne(a => a.ConnectionDefaults, b => b.ToJson());

        // Register the OpenIddict applications/authorizations/scopes/tokens entity sets so the
        // self-hosted OAuth/OIDC server persists its state in the same database.
        builder.UseOpenIddict();
    }
}
