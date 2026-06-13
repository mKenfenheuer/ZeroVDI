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
}
