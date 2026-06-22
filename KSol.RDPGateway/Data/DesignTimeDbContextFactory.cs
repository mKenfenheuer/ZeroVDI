using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KSol.RDPGateway.Data;

/// <summary>
/// Design-time factory so EF Core tooling (migrations) can construct the context without the full
/// application host / DI graph.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("DataSource=Data/app_db.sqlite;Cache=Shared")
            .Options;
        return new ApplicationDbContext(options);
    }
}
