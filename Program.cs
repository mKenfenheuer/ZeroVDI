using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using RDPGW.Extensions;
using RDPGW.AspNetCore;
using KSol.RDPGateway.RDP;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(connectionString));
        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        builder.Services.AddDefaultIdentity<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        // Replace the default password hasher with one that also derives the Digest HA1 and NTLM
        // NT hash on every password set, enabling Digest and NTLM/Negotiate gateway auth without
        // asking the user for anything extra.
        builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, DerivingPasswordHasher>();

        builder.Services.AddControllersWithViews();

        builder.Services.AddRDPGW();
        builder.Services.AddSingleton<IRDPGWAuthenticationHandler, RDPAuthenticationHandler>();
        builder.Services.AddSingleton<IRDPGWAuthorizationHandler, RDPAutorizationHandler>();
        builder.Services.AddSingleton<RDP.RdpFileGenerator>();
        builder.Services.AddSingleton<RDP.PaaTokenService>();

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetService<RoleManager<IdentityRole>>();
            var context = scope.ServiceProvider.GetService<ApplicationDbContext>();

            // Apply any pending EF Core migrations at startup.
            if (context != null && context.Database.GetPendingMigrations().Any())
            {
                context.Database.Migrate();
            }
            
            // Create roles if they don't exist
            var roles = new[] { "Admin", "User" };
            foreach (var role in roles)
            {
                if (roleManager != null && !roleManager.RoleExistsAsync(role).Result)
                {
                    roleManager.CreateAsync(new IdentityRole(role)).Wait();
                }
            }
            
            if (userManager?.Users.Count() == 0)
            {
                ApplicationUser user = new ApplicationUser()
                {
                    UserName = "admin@example.com",
                    NormalizedEmail = "admin@example.com".ToUpper(),
                    NormalizedUserName = "admin@example.com".ToUpper(),
                    Email = "admin@example.com",
                    EmailConfirmed = true,
                    LockoutEnabled = false,
                };
                userManager.CreateAsync(user).Wait();
                user.PasswordHash = userManager.PasswordHasher.HashPassword(user, "rdpgateway");
                context?.Users.Update(user);
                context?.SaveChanges();
                
                // Assign Admin role to admin@example.com
                userManager.AddToRoleAsync(user, "Admin").Wait();
            }
            else if (userManager != null)
            {
                // If admin@example.com exists but doesn't have Admin role, add it
                var adminUser = userManager.FindByNameAsync("admin@example.com").Result;
                if (adminUser != null && !userManager.IsInRoleAsync(adminUser, "Admin").Result)
                {
                    userManager.AddToRoleAsync(adminUser, "Admin").Wait();
                }
            }
        }




        app.UseRDPGW();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseMigrationsEndPoint();
        }
        else
        {
            app.UseExceptionHandler("/Home/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseRouting();

        app.UseAuthorization();

        app.MapStaticAssets();
        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}")
            .WithStaticAssets();
        app.MapRazorPages()
           .WithStaticAssets();

        app.Run();
    }
}
