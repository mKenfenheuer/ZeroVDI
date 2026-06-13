using KSol.RDPGateway.Data;
using Microsoft.EntityFrameworkCore;
using RDPGW.AspNetCore;

namespace KSol.RDPGateway.RDP;

public class RDPAutorizationHandler : IRDPGWAuthorizationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RDPAutorizationHandler> _logger;

    public RDPAutorizationHandler(IServiceScopeFactory scopeFactory, ILogger<RDPAutorizationHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> HandleUserAuthorization(string userId, string resource)
    {
        using var scope = _scopeFactory.CreateScope();
        using var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // The client connects to a resource by its stable id (the GUID written as the .rdp full
        // address), so authorization keys on RDPResourceId == resource id. Strip any stray nulls.
        resource = resource.Trim().Replace("\0","");

        var authorized = await dbContext.RDPResourceUserAuthorizations.AnyAsync(a => a.UserId == userId && a.RDPResourceId == resource);

        if (authorized)
        {
            _logger.LogInformation($"User {userId} is authorized for resource {resource}");
        }
        else
        {
            _logger.LogWarning($"User {userId} is not authorized for resource {resource}");
        }

        return authorized;
    }
}