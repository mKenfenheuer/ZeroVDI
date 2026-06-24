using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using RDPGW.Extensions;
using RDPGW.AspNetCore;
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
        // Session-recording rules evaluation (per request: uses the scoped DbContext).
        builder.Services.AddScoped<RDP.RecordingPolicy>();
        // Background job that muxes captured raw streams into MP4 after sessions end (and picks up any
        // recordings left Processing by a previous run — crash-resilient).
        builder.Services.AddHostedService<RDP.RecordingMuxService>();

        builder.Services.AddControllersWithViews();

        // Route native RDGW connections through the gateway's NLA man-in-the-middle so they share the
        // browser console's pipeline: SSO (stored host creds swapped in) and session recording.
        builder.Services.AddRDPGW()
            .UseConnectionHandler<RDP.GatewayConnectionHandler>();
        builder.Services.AddSingleton<IRDPGWAuthenticationHandler, RDPAuthenticationHandler>();
        builder.Services.AddSingleton<IRDPGWAuthorizationHandler, RDPAutorizationHandler>();
        builder.Services.AddSingleton<RDP.RdpFileGenerator>();
        builder.Services.AddSingleton<RDP.PaaTokenService>();

        // Proxmox VE backend (VDI): settings provider, REST client, session tracking, the gateway
        // resource resolver (GUID -> current host, start-on-connect), and the background services
        // that sync the VM inventory and pause idle VMs.
        builder.Services.AddSingleton<RDP.ProxmoxBackendProvider>();
        builder.Services.AddSingleton<RDP.ProxmoxClient>();
        builder.Services.AddSingleton<RDP.SessionTracker>();
        builder.Services.AddSingleton<IRDPGWResourceResolver, RDP.VdiResourceResolver>();
        builder.Services.AddSingleton<RDP.ProxmoxSyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RDP.ProxmoxSyncService>());
        builder.Services.AddHostedService<RDP.IdleReaperService>();

        // Self-hosted OAuth 2.0 / OpenID Connect server (OpenIddict), backed by the Identity users.
        // Lets OAuth-capable clients obtain a Bearer token via an interactive login and present it
        // to the RDWeb feed. OpenIddict's ASP.NET validation also populates HttpContext.User from a
        // valid Bearer token, so the feed's cookie/bearer resolution works uniformly.
        builder.Services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore().UseDbContext<ApplicationDbContext>();
            })
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("connect/authorize")
                       .SetTokenEndpointUris("connect/token")
                       .SetUserInfoEndpointUris("connect/userinfo")
                       .SetConfigurationEndpointUris(".well-known/openid-configuration");

                // Authorization Code + PKCE (interactive) and refresh tokens.
                options.AllowAuthorizationCodeFlow()
                       .AllowRefreshTokenFlow()
                       .RequireProofKeyForCodeExchange();

                options.RegisterScopes("openid", "profile", "email", "offline_access", "rdgateway");

                // Signing/encryption credentials.
                // In Development, use the throwaway development certificates. In production, use
                // persistent self-signed certificates that are generated on first run and stored on
                // disk, so issued tokens survive restarts (the development certs are ephemeral).
                if (builder.Environment.IsDevelopment())
                {
                    options.AddDevelopmentEncryptionCertificate()
                           .AddDevelopmentSigningCertificate();
                }
                else
                {
                    options.AddSigningCertificate(CertificateProvider.GetSigningCertificate(builder.Configuration, builder.Environment))
                           .AddEncryptionCertificate(CertificateProvider.GetEncryptionCertificate(builder.Configuration, builder.Environment));
                }

                // Issue access tokens as JWTs so the feed can validate them as Bearer tokens.
                options.DisableAccessTokenEncryption();

                options.UseAspNetCore()
                       .EnableAuthorizationEndpointPassthrough()
                       .EnableTokenEndpointPassthrough()
                       .EnableUserInfoEndpointPassthrough()
                       .EnableStatusCodePagesIntegration();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

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

        // Must run before any middleware that inspects scheme/host (RDPGW, HTTPS redirect, auth).
        app.UseForwardedHeaders();

        // Diagnostic request/response logging for the subscription-related endpoints. MSRDC ("Windows
        // App"/Remote Desktop) probes several paths during a workspace subscription; when it reports
        // "the authentication method for the host is not currently supported" we need to see exactly
        // which path it hit, what auth it presented, and what we replied (status + WWW-Authenticate).
        // Scoped to the relevant prefixes to avoid noise; logged at Information so it shows in prod.
        {
            var diagLogger = app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("KSol.RDPGateway.SubscribeDiagnostics");

            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                var watched = path.StartsWith("/rdweb", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/connect", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/Identity", StringComparison.OrdinalIgnoreCase);

                if (!watched)
                {
                    await next(context);
                    return;
                }

                var req = context.Request;
                string AuthScheme()
                {
                    var h = req.Headers.Authorization.ToString();
                    if (string.IsNullOrEmpty(h)) return "(none)";
                    var sp = h.IndexOf(' ');
                    return sp > 0 ? h[..sp] : h; // log the scheme only, never the credential
                }

                diagLogger.LogInformation(
                    "SUBSCRIBE >> {Method} {Scheme}://{Host}{Path}{Query} | Auth={Auth} UA={UserAgent} Accept={Accept} XFwdProto={XFwdProto} XFwdHost={XFwdHost} XFwdFor={XFwdFor}",
                    req.Method, req.Scheme, req.Host.Value, req.Path.Value, req.QueryString.Value,
                    AuthScheme(),
                    req.Headers.UserAgent.ToString(),
                    req.Headers.Accept.ToString(),
                    req.Headers["X-Forwarded-Proto"].ToString(),
                    req.Headers["X-Forwarded-Host"].ToString(),
                    req.Headers["X-Forwarded-For"].ToString());

                await next(context);

                var res = context.Response;
                diagLogger.LogInformation(
                    "SUBSCRIBE << {Method} {Path} -> {Status} | WWW-Authenticate={WwwAuth} Location={Location} ContentType={ContentType}",
                    req.Method, req.Path.Value, res.StatusCode,
                    res.Headers.WWWAuthenticate.ToString(),
                    res.Headers.Location.ToString(),
                    res.ContentType ?? string.Empty);
            });
        }

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

            // Seed the OAuth client used by Remote Desktop / OAuth-capable clients to subscribe.
            // A public client (PKCE, no secret) with the redirect URIs MSRDC and generic OAuth
            // clients use. Adjust RedirectUris for your client if needed.
            var appManager = scope.ServiceProvider.GetService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
            if (appManager != null && appManager.FindByClientIdAsync("rdgateway-client").AsTask().Result == null)
            {
                appManager.CreateAsync(new OpenIddict.Abstractions.OpenIddictApplicationDescriptor
                {
                    ClientId = "rdgateway-client",
                    ClientType = OpenIddict.Abstractions.OpenIddictConstants.ClientTypes.Public,
                    ConsentType = OpenIddict.Abstractions.OpenIddictConstants.ConsentTypes.Implicit,
                    DisplayName = "KSol.IT RDP Gateway Client",
                    RedirectUris =
                    {
                        new Uri("http://localhost"),
                        new Uri("http://localhost:0"),
                        new Uri("ms-appx-web://Microsoft.AAD.BrokerPlugin/rdgateway-client"),
                    },
                    Permissions =
                    {
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.Endpoints.Authorization,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.Endpoints.Token,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.ResponseTypes.Code,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.Scopes.Email,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.Scopes.Profile,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.Prefixes.Scope + "rdgateway",
                    },
                    Requirements =
                    {
                        OpenIddict.Abstractions.OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
                    }
                }).AsTask().Wait();
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
