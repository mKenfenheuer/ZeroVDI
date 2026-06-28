using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.RDP;
using KSol.RDPGateway.Models;
using Microsoft.AspNetCore.DataProtection;

namespace KSol.RDPGateway;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Force HTTP/1.1 on all Kestrel endpoints. The RemoteApp & Desktop Connections client
        // authenticates to the feed with NTLM, which is a connection-bound auth scheme and is
        // incompatible with HTTP/2's stream multiplexing: over h2 the NTLM handshake appears to
        // complete but the client mishandles the response body, surfacing as "the workspace failed
        // to parse the XML". Restricting to HTTP/1.1 keeps the handshake and the body on one
        // connection. (Behind a reverse proxy, also ensure the proxy speaks HTTP/1.1 to the client.)
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureEndpointDefaults(lo =>
                lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
        });

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

        // Persist the DataProtection keyring on disk so credentials encrypted with
        // CredentialProtector (stored VM passwords) remain decryptable across restarts. The directory
        // defaults to a subdir of the persisted /app/Data volume; override via DataProtection:KeysDir.
        var dpKeysDir = builder.Configuration["DataProtection:KeysDir"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "Data", "dp-keys");
        Directory.CreateDirectory(dpKeysDir);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir));
        builder.Services.AddSingleton<RDP.CredentialProtector>();

        // SMTP email sender for Identity (password reset, confirmation, 2FA codes).
        builder.Services.Configure<RDP.SmtpSettings>(builder.Configuration.GetSection("Smtp"));
        builder.Services.AddTransient<IEmailSender, RDP.SmtpEmailSender>();
        // Holds RDP Server Redirection routing tokens between a redirect and the browser's reconnect.
        builder.Services.AddSingleton<RDP.RedirectionTokenCache>();
        // Session-recording rules evaluation (per request: uses the scoped DbContext).
        builder.Services.AddScoped<RDP.RecordingPolicy>();
        // Background job that muxes captured raw streams into MP4 after sessions end (and picks up any
        // recordings left Processing by a previous run — crash-resilient).
        builder.Services.AddHostedService<RDP.RecordingMuxService>();

        builder.Services.AddControllersWithViews().AddRazorRuntimeCompilation();

        // Route native RDGW connections through the gateway's NLA man-in-the-middle so they share the
        // browser console's pipeline: SSO (stored host creds swapped in) and session recording.

        // Proxmox VE backend (VDI): settings provider, REST client, session tracking, the gateway
        // resource resolver (GUID -> current host, start-on-connect), and the background services
        // that sync the VM inventory and pause idle VMs.
        builder.Services.AddSingleton<RDP.ProxmoxBackendProvider>();
        builder.Services.AddSingleton<RDP.ProxmoxClient>();
        builder.Services.AddSingleton<RDP.SessionTracker>();
        builder.Services.AddSingleton<RDP.VdiResourceResolver>();
        // Tracks the per-(user, resource) connect-readiness sequence so the browser preflight can poll
        // progress (start VM → guest agent → IP → RDP probe → ready) before launching the console.
        builder.Services.AddSingleton<RDP.ConnectionReadinessService>();
        builder.Services.AddSingleton<RDP.ProxmoxSyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RDP.ProxmoxSyncService>());
        builder.Services.AddHostedService<RDP.IdleReaperService>();

        // Manual resource lifecycle: status polling, WOL, IPMI, SSH shutdown.
        builder.Services.AddSingleton<RDP.IpmiClient>();
        builder.Services.AddSingleton<RDP.SshCommandService>();
        builder.Services.AddSingleton<RDP.ManualHostStatusService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RDP.ManualHostStatusService>());

        // Honor X-Forwarded-Proto / X-Forwarded-Host / X-Forwarded-For when running behind a
        // reverse proxy (e.g. Traefik) that terminates TLS. Without this, Request.Scheme/Host are
        // the internal http://… values, so the absolute URLs in the RDWeb feed (FeedUrl and the
        // per-resource .rdp URLs) would be wrong and the client would reject the workspace.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            // The app is only reachable through the trusted proxy, so accept its forwarded headers.
            // (Tighten KnownProxies/KnownNetworks if the app is ever exposed directly.)
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        var app = builder.Build();

        // Must run before any middleware that inspects scheme/host (HTTPS redirect, auth).
        app.UseForwardedHeaders();

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
            
            // Create roles if they don't exist. "Auditor" may review session recordings (read-only
            // access to the Recordings area) without full Admin rights; ordinary users never see them.
            var roles = new[] { "Admin", "User", "Auditor" };
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

        // Enable WebSocket upgrades for the browser RDP console (/ws/rdp/{id}). Must run before
        // routing so the relay endpoint can accept the upgrade.
        app.UseWebSockets();

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
