using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.RDP;
using KSol.ZeroVDI.Models;
using Microsoft.AspNetCore.DataProtection;

namespace KSol.ZeroVDI;

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
            .AddEntityFrameworkStores<ApplicationDbContext>()
            // Registers the built-in token providers, including "Email" — used to issue the one-time codes
            // for email-based MFA (the authenticator app uses its own dedicated provider). The default
            // Email/Phone codes live in the default DataProtector token provider; their lifetime is the
            // global TokenOptions lifespan (default 1 day) unless overridden below.
            .AddDefaultTokenProviders();

        // Email MFA codes should be short-lived. Scope the email-confirmation/2FA token lifespan down from
        // the 1-day default so an intercepted code can't be replayed long after it was sent.
        builder.Services.Configure<DataProtectionTokenProviderOptions>(o =>
            o.TokenLifespan = TimeSpan.FromMinutes(5));

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

        // The keyring decrypts every stored VM/IPMI/SSH credential, so the on-disk key material is itself
        // encrypted at rest (AES-256-GCM) with a passphrase held only in configuration — never on disk in
        // plaintext, never in source control. Provide it via the DataProtection:MasterKeyPassphrase
        // env var / secret. In Production a missing passphrase is fatal (we refuse to write plaintext
        // keys); in Development we fall back to an ephemeral dev passphrase so first-run is frictionless.
        var keyringPassphrase = builder.Configuration["DataProtection:MasterKeyPassphrase"];
        if (string.IsNullOrWhiteSpace(keyringPassphrase))
        {
            if (!builder.Environment.IsDevelopment())
                throw new InvalidOperationException(
                    "DataProtection:MasterKeyPassphrase is not configured. Set it (e.g. the " +
                    "DataProtection__MasterKeyPassphrase environment variable) to a strong secret so the " +
                    "credential-encryption keyring can be stored encrypted at rest. Refusing to start with " +
                    "an unprotected keyring.");
            keyringPassphrase = RDP.KeyringEncryptor.DevFallbackPassphrase;
        }

        // Guard: refuse to boot if a keyring file is tracked in git — a committed key (even encrypted) is
        // a credential-exposure footgun and almost always a mistake.
        EnsureKeyringNotInSourceControl(dpKeysDir, builder.Environment);

        var keyringEncryptor = new RDP.KeyringEncryptor(keyringPassphrase);
        // Register the encryptor so DataProtection can resolve the matching IXmlDecryptor (referenced by
        // type name inside each encrypted key) when unsealing the keyring at startup.
        builder.Services.AddSingleton(keyringEncryptor);
        builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.XmlEncryption.IXmlDecryptor>(keyringEncryptor);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir));
        // Encrypt the keyring at rest with our AES-GCM encryptor. We set it directly on KeyManagementOptions
        // (the public UseXmlEncryptor extension only accepts framework-provided encryptor types), so newly
        // written keys are sealed and existing encrypted keys are unsealed with the same component.
        builder.Services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o =>
            o.XmlEncryptor = keyringEncryptor);
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
        // Recording-at-rest encryption: AES-256-CTR keyed off the master keyring passphrase (seekable so
        // the player's range requests still work). Registered as a singleton; the mux/serve paths use it
        // only when Recording:EncryptAtRest is true.
        builder.Services.AddSingleton<RDP.RecordingCryptor>();
        // Recording retention / auto-purge (age + total-size cap). No-op until configured.
        builder.Services.AddHostedService<RDP.RecordingRetentionService>();

        // Audit trail: immutable record of authentication, session, credential and admin actions.
        // HttpContextAccessor lets the scoped logger resolve the actor + client IP off the request.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<RDP.IAuditLogger, RDP.AuditLogger>();

        // Resource authorization resolver: unifies direct per-user grants with group grants so every
        // access gate (dashboard, console, readiness, WS relay) shares one rule. Scoped (uses DbContext).
        builder.Services.AddScoped<RDP.ResourceAccessService>();

        // Tenant-wide admin-enforced device/channel redirection policy (clipboard, audio, mic, camera).
        // Cached singleton; clamps the effective console ConnectionDefaults at every enforcement point.
        builder.Services.AddSingleton<RDP.DevicePolicyService>();

        // Renders the bundled documentation (markdown under wwwroot/docs), split into an admin set
        // (docs/admin → /admin/docs) and a user set (docs/user → /docs). The factory caches one
        // DocsService per section; each holds no per-request state, just its resolved docs root path.
        builder.Services.AddSingleton<RDP.DocsServiceFactory>();

        // Tenant-wide appearance policy (forced theme/mode + custom logo). Cached singleton.
        builder.Services.AddSingleton<RDP.AppearanceService>();

        // MFA enforcement: Identity already runs the 2FA challenge for enrolled users at login; this
        // policy decides who is REQUIRED to enroll (Mfa section). The middleware (added below) forces
        // required-but-unenrolled users to the authenticator setup page.
        builder.Services.AddSingleton<RDP.MfaPolicy>();

        // Rate limiting (brute-force / abuse protection). Two policies:
        //  - "auth": IP-based fixed window on the login/register/password endpoints.
        //  - "ws":   IP-based fixed window on the WebSocket relay handshake.
        // Limits are configurable via the RateLimiting section; the defaults below are sane for a
        // small/medium VDI deployment. A rejected request gets HTTP 429.
        var rl = builder.Configuration.GetSection("RateLimiting");
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy("auth", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rl.GetValue("AuthPermitLimit", 10),
                        Window = TimeSpan.FromSeconds(rl.GetValue("AuthWindowSeconds", 60)),
                        QueueLimit = 0,
                    }));

            options.AddPolicy("ws", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rl.GetValue("WsPermitLimit", 30),
                        Window = TimeSpan.FromSeconds(rl.GetValue("WsWindowSeconds", 60)),
                        QueueLimit = 0,
                    }));
        });

        builder.Services.AddControllersWithViews().AddRazorRuntimeCompilation();

        // Route native RDGW connections through the gateway's NLA man-in-the-middle so they share the
        // browser console's pipeline: SSO (stored host creds swapped in) and session recording.

        // Proxmox VE backend (VDI): settings provider, REST client, session tracking, the gateway
        // resource resolver (GUID -> current host, start-on-connect), and the background services
        // that sync the VM inventory and pause idle VMs.
        // Connectors: remote proxy agents. The hub tracks live control channels + routes TCP/probe over
        // them; the path selector picks the fastest route (direct vs. connector) per host at connect time.
        builder.Services.AddSingleton<RDP.ConnectorHub>();
        builder.Services.AddSingleton<RDP.ConnectorPathSelector>();

        builder.Services.AddSingleton<RDP.ProxmoxBackendProvider>();
        builder.Services.AddSingleton<RDP.ProxmoxClient>();
        builder.Services.AddSingleton<RDP.SessionTracker>();
        // Pluggable host-protocol resolvers: native RDP (NLA) and the VNC→RDP bridge, chosen per
        // resource by RdpProtocol via the factory.
        builder.Services.AddSingleton<RDP.NlaRdpResolver>();
        // Exchangeable real-time H.264 codec backend for the GFX/AVC420 path. Active backend is in-process
        // libx264 (direct P/Invoke, no ffmpeg subprocess); swap this single registration to
        // FfmpegH264EncoderFactory to fall back to the ffmpeg-subprocess backend.
        builder.Services.AddSingleton<RDP.Bridge.IH264EncoderFactory, RDP.Bridge.Libx264H264EncoderFactory>();
        builder.Services.AddSingleton<RDP.Bridge.VncRdpResolver>();
        builder.Services.AddSingleton<RDP.Spice.SpiceRdpResolver>();
        builder.Services.AddSingleton<RDP.IRdpResolverFactory, RDP.RdpResolverFactory>();
        builder.Services.AddSingleton<RDP.VdiProvisioningService>();
        builder.Services.AddSingleton<RDP.VdiResourceResolver>();
        // Tracks the per-(user, resource) connect-readiness sequence so the browser preflight can poll
        // progress (start VM → guest agent → IP → RDP probe → ready) before launching the console.
        builder.Services.AddSingleton<RDP.ConnectionReadinessService>();
        builder.Services.AddSingleton<RDP.ProxmoxSyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RDP.ProxmoxSyncService>());
        builder.Services.AddHostedService<RDP.IdleReaperService>();

        // Resource lifecycle: periodic power-state polling (all sources), WOL, IPMI, SSH shutdown.
        builder.Services.AddSingleton<RDP.IpmiClient>();
        builder.Services.AddSingleton<RDP.SshCommandService>();
        builder.Services.AddSingleton<RDP.ResourceShutdownService>();
        builder.Services.AddSingleton<RDP.ResourceStatusService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RDP.ResourceStatusService>());

        // Honor X-Forwarded-Proto / X-Forwarded-Host / X-Forwarded-For when running behind a
        // reverse proxy (e.g. Traefik) that terminates TLS. Without this, Request.Scheme/Host are
        // the internal http://… values, so the absolute URLs in the RDWeb feed (FeedUrl and the
        // per-resource .rdp URLs) would be wrong and the client would reject the workspace.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;

            // Only trust X-Forwarded-* from explicitly configured proxies. Without this, ANY client can
            // spoof X-Forwarded-Proto/Host/For — enabling host-header poisoning of the generated .rdp /
            // feed URLs and forged client IPs in logs. Configure the reverse proxy's address(es) via
            // ForwardedHeaders:KnownProxies (IPs) and/or ForwardedHeaders:KnownNetworks ("cidr/prefix").
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();

            var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>();
            foreach (var ip in knownProxies ?? Array.Empty<string>())
                if (System.Net.IPAddress.TryParse(ip.Trim(), out var parsed))
                    options.KnownProxies.Add(parsed);

            var knownNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>();
            foreach (var cidr in knownNetworks ?? Array.Empty<string>())
            {
                var parts = cidr.Split('/', 2);
                if (parts.Length == 2 && System.Net.IPAddress.TryParse(parts[0].Trim(), out var prefix)
                    && int.TryParse(parts[1].Trim(), out var len))
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, len));
            }

            // Backwards-compatible / containerized default: if nothing is configured, trust the loopback
            // proxy only (the common "reverse proxy on the same host/pod" case) rather than every client.
            // Set ForwardedHeaders:TrustAllProxies=true to opt back into trusting any hop (only safe when
            // the app is unreachable except through the proxy).
            if ((knownProxies == null || knownProxies.Length == 0)
                && (knownNetworks == null || knownNetworks.Length == 0))
            {
                if (builder.Configuration.GetValue<bool>("ForwardedHeaders:TrustAllProxies"))
                {
                    options.ForwardLimit = null; // trust the whole chain
                }
                else
                {
                    options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
                    options.KnownProxies.Add(System.Net.IPAddress.Loopback);
                }
            }
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

            // One-time, idempotent: encrypt any legacy plaintext secrets (NtHash, Proxmox API token) now
            // that those columns are encrypted at rest. Safe to run every startup — already-encrypted
            // values are detected and skipped.
            if (context != null)
            {
                try
                {
                    var protector = scope.ServiceProvider.GetRequiredService<RDP.CredentialProtector>();
                    var migrator = new Data.SecretEncryptionMigrator(context, protector,
                        scope.ServiceProvider.GetRequiredService<ILogger<Data.SecretEncryptionMigrator>>());
                    migrator.EncryptPlaintextSecrets();
                }
                catch (Exception ex)
                {
                    scope.ServiceProvider.GetRequiredService<ILogger<Program>>()
                        .LogError(ex, "Failed to encrypt legacy plaintext secrets at startup.");
                }
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
                // Bootstrap the first admin. The email and password may be supplied out-of-band via
                // Bootstrap:AdminEmail / Bootstrap:AdminPassword (env: Bootstrap__AdminPassword). If no
                // password is configured we generate a cryptographically-random one and log it ONCE — we
                // never ship a known default password (the previous hardcoded "zerovdi" let anyone log
                // into a fresh deployment as Admin). Lockout is left ENABLED so the account is brute-force
                // protected like any other.
                var bootstrapEmail = builder.Configuration["Bootstrap:AdminEmail"] ?? "admin@example.com";
                var configuredPassword = builder.Configuration["Bootstrap:AdminPassword"];
                var generated = string.IsNullOrEmpty(configuredPassword);
                var bootstrapPassword = configuredPassword ?? GenerateStrongPassword();

                ApplicationUser user = new ApplicationUser()
                {
                    UserName = bootstrapEmail,
                    NormalizedEmail = bootstrapEmail.ToUpperInvariant(),
                    NormalizedUserName = bootstrapEmail.ToUpperInvariant(),
                    Email = bootstrapEmail,
                    EmailConfirmed = true,
                };
                var createResult = userManager.CreateAsync(user, bootstrapPassword).Result;
                if (createResult.Succeeded)
                {
                    userManager.AddToRoleAsync(user, "Admin").Wait();
                    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                    if (generated)
                        startupLogger.LogWarning(
                            "Bootstrapped initial admin account '{Email}' with a generated password: {Password}\n" +
                            "Sign in and change it immediately; this is the only time it is shown.",
                            bootstrapEmail, bootstrapPassword);
                    else
                        startupLogger.LogInformation(
                            "Bootstrapped initial admin account '{Email}' with the configured Bootstrap:AdminPassword.",
                            bootstrapEmail);
                }
                else
                {
                    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                    startupLogger.LogError("Failed to bootstrap initial admin account: {Errors}",
                        string.Join("; ", createResult.Errors.Select(e => e.Description)));
                }
            }
            else if (userManager != null)
            {
                // If the configured/default bootstrap admin exists but lacks the Admin role, grant it.
                var bootstrapEmail = builder.Configuration["Bootstrap:AdminEmail"] ?? "admin@example.com";
                var adminUser = userManager.FindByNameAsync(bootstrapEmail).Result;
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
            app.UseExceptionHandler("/error/500");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        // Render styled pages for 4xx/5xx responses (e.g. 404) instead of the framework default. The
        // {0} placeholder is replaced with the status code; re-execution preserves the original URL.
        app.UseStatusCodePagesWithReExecute("/error/{0}");

        app.UseHttpsRedirection();

        // Serve runtime-uploaded assets (custom branding logos) from the persisted /app/Data volume at
        // the /uploads URL prefix. This is deliberately a separate UseStaticFiles + PhysicalFileProvider
        // rather than the build-time MapStaticAssets pipeline below: MapStaticAssets only serves files
        // baked into the publish manifest, so logos written after deploy (Data/uploads/branding) would
        // 404 in production. Storing them under Data also keeps them across container recreation.
        var uploadsRoot = Controllers.AppearanceController.UploadsRoot(app.Environment);
        Directory.CreateDirectory(uploadsRoot);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsRoot),
            RequestPath = "/uploads",
        });

        // Enable WebSocket upgrades for the browser RDP console (/ws/rdp/{id}). Must run before
        // routing so the relay endpoint can accept the upgrade.
        app.UseWebSockets();

        app.UseRouting();

        // Brute-force / abuse protection. After routing so per-endpoint policies are known; before auth
        // so we reject floods before doing password work.
        app.UseRateLimiter();

        app.UseAuthorization();

        // After authorization (so User is populated and role-gated): force users who are required to use
        // MFA but have not enrolled to the authenticator setup page before they can use the app.
        app.UseMiddleware<RDP.MfaEnforcementMiddleware>();

        // The Electron desktop client is console-only; block the admin surface for it (404, same as an
        // unmapped route) regardless of the user's role.
        app.UseMiddleware<RDP.DesktopAdminBlockMiddleware>();

        app.MapStaticAssets();
        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}")
            .WithStaticAssets();
        // Throttle the Identity account pages (login, register, password reset, 2FA) by client IP to
        // blunt credential-stuffing / brute force beyond the per-account lockout.
        app.MapRazorPages()
           .WithStaticAssets()
           .RequireRateLimiting("auth");

        // Connector agent surface: token-authenticated register + control/data WebSockets (not cookie auth).
        app.MapConnectorAgentEndpoints();

        app.Run();
    }

    /// <summary>
    /// Generates a cryptographically-random password that satisfies the default Identity complexity
    /// rules (upper, lower, digit, symbol, length). Used only to bootstrap the first admin when no
    /// Bootstrap:AdminPassword is configured; the value is logged once and never persisted in plaintext.
    /// </summary>
    private static string GenerateStrongPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // no I/O to avoid ambiguity
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+";
        const string all = upper + lower + digits + symbols;

        var chars = new char[20];
        // Guarantee one of each required class, then fill the rest from the full alphabet.
        chars[0] = upper[System.Security.Cryptography.RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[System.Security.Cryptography.RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[System.Security.Cryptography.RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[System.Security.Cryptography.RandomNumberGenerator.GetInt32(symbols.Length)];
        for (int i = 4; i < chars.Length; i++)
            chars[i] = all[System.Security.Cryptography.RandomNumberGenerator.GetInt32(all.Length)];

        // Fisher–Yates shuffle so the guaranteed-class characters aren't always in the first positions.
        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    /// <summary>
    /// Refuses to start if any DataProtection key file is tracked by git. A committed keyring exposes the
    /// key that decrypts every stored credential; even though we now encrypt the keyring at rest, a
    /// committed file plus a leaked passphrase is a full compromise, and committing it is virtually always
    /// an accident. This catches the mistake at boot instead of in an audit. Best-effort: if git is not
    /// available the check is skipped (e.g. in a published container with no repo).
    /// </summary>
    private static void EnsureKeyringNotInSourceControl(string keysDir, IWebHostEnvironment env)
    {
        try
        {
            var gitDir = Path.Combine(env.ContentRootPath, ".git");
            if (!Directory.Exists(gitDir) && !Directory.Exists(Path.Combine(Directory.GetParent(env.ContentRootPath)?.FullName ?? "", ".git")))
                return; // not a git working tree — nothing to check

            if (!Directory.Exists(keysDir)) return;

            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = env.ContentRootPath,
            };
            psi.ArgumentList.Add("ls-files");
            psi.ArgumentList.Add("--error-unmatch");
            psi.ArgumentList.Add(Path.Combine(keysDir, "*.xml"));

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return;
            var tracked = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(tracked))
                throw new InvalidOperationException(
                    "A DataProtection keyring file is tracked in git:\n" + tracked.Trim() +
                    "\nRemove it from source control (git rm --cached), add Data/dp-keys/** to .gitignore, " +
                    "and ROTATE the key (the committed key must be treated as compromised). Refusing to start.");
        }
        catch (InvalidOperationException) { throw; }
        catch
        {
            // git missing or any other probe failure: don't block startup on the guard itself.
        }
    }
}
