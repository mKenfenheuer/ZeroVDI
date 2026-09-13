using System.Diagnostics;
using System.Reflection;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KSol.ZeroVDI.Controllers;

/// <summary>
/// The operations page: one screen that answers "is this deployment healthy, and is everything I
/// configured actually working?" — the question that otherwise costs a trawl through the container
/// log. It reads live state (background-worker liveness, database and recording storage, keyring,
/// SMTP, federation) and offers the test buttons for the integrations that are otherwise only
/// exercised at the worst possible moment: when a user needs a password reset, or at 3 a.m. when a
/// pool won't provision.
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin/operations")]
public class OperationsController : Controller
{
    private static readonly DateTime StartedUtc = DateTime.UtcNow;

    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ServiceHeartbeats _heartbeats;
    private readonly SessionTracker _sessions;
    private readonly ExternalIdentityOptions _oidc;
    private readonly PublicUrl _publicUrl;
    private readonly SmtpSettings _smtp;
    private readonly IEmailSender _email;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ProxmoxBackendProvider _backends;
    private readonly ProxmoxClient _proxmox;
    private readonly IAuditLogger _audit;
    private readonly IHttpClientFactory? _httpFactory;

    public OperationsController(ApplicationDbContext db, IConfiguration config, IWebHostEnvironment env,
        ServiceHeartbeats heartbeats, SessionTracker sessions, ExternalIdentityOptions oidc,
        PublicUrl publicUrl, IOptions<SmtpSettings> smtp, IEmailSender email,
        UserManager<ApplicationUser> users, ProxmoxBackendProvider backends, ProxmoxClient proxmox,
        IAuditLogger audit, IHttpClientFactory? httpFactory = null)
    {
        _db = db;
        _config = config;
        _env = env;
        _heartbeats = heartbeats;
        _sessions = sessions;
        _oidc = oidc;
        _publicUrl = publicUrl;
        _smtp = smtp.Value;
        _email = email;
        _users = users;
        _backends = backends;
        _proxmox = proxmox;
        _audit = audit;
        _httpFactory = httpFactory;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var vm = new OperationsViewModel
        {
            Version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?.Split('+')[0] ?? "unknown",
            Environment = _env.EnvironmentName,
            Runtime = Environment.Version.ToString(),
            StartedUtc = StartedUtc,
            Heartbeats = _heartbeats.All(),
            ActiveSessions = _sessions.All().Count,
            MaxConcurrentPerUser = _config.GetValue("Sessions:MaxConcurrentPerUser", 0),
            PublicBaseUrl = _publicUrl.BaseUrl?.ToString(),
            AllowedHosts = _config["AllowedHosts"],
            SmtpHost = _smtp.Host,
            SmtpPort = _smtp.Port,
            SmtpFrom = _smtp.FromAddress,
            SmtpAuthenticated = !string.IsNullOrEmpty(_smtp.Username),
            OidcEnabled = _oidc.Enabled,
            OidcConfigured = _oidc.IsConfigured,
            OidcAuthority = _oidc.Authority,
            OidcProblem = _oidc.ConfigurationProblem,
            KeyringPassphraseSet = !string.IsNullOrWhiteSpace(_config["DataProtection:MasterKeyPassphrase"]),
            RecordingsEncrypted = _config.GetValue("Recording:EncryptAtRest", false),
            RetentionDays = _config.GetValue("Recording:RetentionDays", 0),
            MaxRecordingGb = _config.GetValue("Recording:MaxTotalGB", 0),
            AuditEventCount = await _db.AuditEvents.CountAsync(),
            RecordingCount = await _db.Recordings.CountAsync(),
            UserCount = await _users.Users.CountAsync(),
            AdminCount = (await _users.GetUsersInRoleAsync("Admin")).Count,
        };

        // Database: pending migrations are the difference between "restart applied it" and a silent
        // runtime failure on the first query that touches a new column.
        try
        {
            vm.PendingMigrations = (await _db.Database.GetPendingMigrationsAsync()).ToList();
            vm.DatabasePath = SqlitePath(_db.Database.GetConnectionString());
            vm.DatabaseBytes = FileSize(vm.DatabasePath);
        }
        catch (Exception ex) { vm.DatabaseError = ex.Message; }

        // Keyring: without the key files the stored VM credentials are unreadable, which surfaces as
        // "SSO stopped working" long after the volume was lost.
        var keysDir = _config["DataProtection:KeysDir"]
            ?? Path.Combine(_env.ContentRootPath, "Data", "dp-keys");
        vm.KeyringDir = keysDir;
        vm.KeyringKeyCount = Directory.Exists(keysDir) ? Directory.GetFiles(keysDir, "*.xml").Length : 0;

        var recordingDir = _config["Recording:Directory"] ?? "Data/recordings";
        if (!Path.IsPathRooted(recordingDir))
            recordingDir = Path.Combine(_env.ContentRootPath, recordingDir);
        vm.RecordingDir = recordingDir;
        vm.RecordingBytes = DirectorySize(recordingDir);
        vm.FreeDiskBytes = FreeSpace(recordingDir);

        vm.ConnectorCount = await _db.Connectors.CountAsync();
        vm.BackendCount = await _db.ProxmoxBackends.CountAsync();

        return View(vm);
    }

    // POST: /admin/operations/test/email — prove the SMTP settings work, to the admin's own address.
    [HttpPost("test/email")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestEmail()
    {
        var me = await _users.GetUserAsync(User);
        if (me?.Email is not { Length: > 0 } address)
        {
            TempData["Error"] = "Your account has no e-mail address to send the test to.";
            return RedirectToAction(nameof(Index));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await _email.SendEmailAsync(address, "ZeroVDI test message",
                "This is a test message from your ZeroVDI operations page. " +
                "If you are reading it, password resets and MFA codes can reach your users.");
            await _audit.LogAsync(AuditCategory.Admin, "SmtpTested", detail: new { to = address, ms = sw.ElapsedMilliseconds });
            TempData["Status"] = $"Test message sent to {address} in {sw.ElapsedMilliseconds} ms.";
        }
        catch (Exception ex)
        {
            await _audit.LogAsync(AuditCategory.Admin, "SmtpTested", success: false, detail: new { error = ex.Message });
            TempData["Error"] = "SMTP test failed: " + ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    // POST: /admin/operations/test/oidc — fetch the provider's discovery document.
    [HttpPost("test/oidc")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestOidc()
    {
        if (!_oidc.IsConfigured)
        {
            TempData["Error"] = _oidc.ConfigurationProblem ?? "Identity federation is not enabled.";
            return RedirectToAction(nameof(Index));
        }

        var url = _oidc.Authority!.TrimEnd('/') + "/.well-known/openid-configuration";
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = _httpFactory?.CreateClient() ?? new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode} from {url}");

            // A discovery document that doesn't name the endpoints we need will fail later, at sign-in.
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var issuer = doc.RootElement.TryGetProperty("issuer", out var i) ? i.GetString() : null;
            var hasAuth = doc.RootElement.TryGetProperty("authorization_endpoint", out _);
            var hasToken = doc.RootElement.TryGetProperty("token_endpoint", out _);
            if (!hasAuth || !hasToken)
                throw new InvalidOperationException("The discovery document has no authorization or token endpoint.");

            await _audit.LogAsync(AuditCategory.Admin, "OidcDiscoveryTested", detail: new { url, issuer });
            TempData["Status"] = $"Discovery succeeded in {sw.ElapsedMilliseconds} ms (issuer {issuer}).";
        }
        catch (Exception ex)
        {
            await _audit.LogAsync(AuditCategory.Admin, "OidcDiscoveryTested", success: false,
                detail: new { url, error = ex.Message });
            TempData["Error"] = "Discovery failed: " + ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    // POST: /admin/operations/test/backends — probe every configured Proxmox backend.
    [HttpPost("test/backends")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestBackends()
    {
        var backends = await _backends.GetAllAsync();
        if (backends.Count == 0)
        {
            TempData["Error"] = "No Proxmox backends are configured.";
            return RedirectToAction(nameof(Index));
        }

        var ok = new List<string>();
        var failed = new List<string>();
        foreach (var backend in backends)
        {
            if (!backend.IsConfigured) { failed.Add($"{backend.Name}: not configured"); continue; }
            try
            {
                var vms = await _proxmox.ListVmsAsync(backend);
                ok.Add($"{backend.Name} ({vms.Count} VMs)");
            }
            catch (Exception ex) { failed.Add($"{backend.Name}: {ex.Message}"); }
        }

        await _audit.LogAsync(AuditCategory.Admin, "BackendsTested", success: failed.Count == 0,
            detail: new { ok, failed });
        if (failed.Count == 0) TempData["Status"] = "All backends reachable: " + string.Join(", ", ok);
        else TempData["Error"] = "Unreachable: " + string.Join(" · ", failed);
        return RedirectToAction(nameof(Index));
    }

    private static string? SqlitePath(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        foreach (var part in connectionString.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Replace(" ", "")
                    .Equals("DataSource", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(kv[1].Trim());
        }
        return null;
    }

    private static long FileSize(string? path)
    {
        try { return path != null && System.IO.File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    private static long FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root == null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return 0; }
    }
}

public class OperationsViewModel
{
    public string Version { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Runtime { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public TimeSpan Uptime => DateTime.UtcNow - StartedUtc;

    public IReadOnlyList<ServiceHeartbeat> Heartbeats { get; set; } = Array.Empty<ServiceHeartbeat>();

    public int ActiveSessions { get; set; }
    public int MaxConcurrentPerUser { get; set; }
    public int UserCount { get; set; }
    public int AdminCount { get; set; }
    public int ConnectorCount { get; set; }
    public int BackendCount { get; set; }

    public string? PublicBaseUrl { get; set; }
    public string? AllowedHosts { get; set; }

    public string? DatabasePath { get; set; }
    public long DatabaseBytes { get; set; }
    public List<string> PendingMigrations { get; set; } = new();
    public string? DatabaseError { get; set; }
    public int AuditEventCount { get; set; }

    public string KeyringDir { get; set; } = "";
    public int KeyringKeyCount { get; set; }
    public bool KeyringPassphraseSet { get; set; }

    public string RecordingDir { get; set; } = "";
    public long RecordingBytes { get; set; }
    public long FreeDiskBytes { get; set; }
    public int RecordingCount { get; set; }
    public bool RecordingsEncrypted { get; set; }
    public int RetentionDays { get; set; }
    public int MaxRecordingGb { get; set; }

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; }
    public string SmtpFrom { get; set; } = "";
    public bool SmtpAuthenticated { get; set; }

    public bool OidcEnabled { get; set; }
    public bool OidcConfigured { get; set; }
    public string? OidcAuthority { get; set; }
    public string? OidcProblem { get; set; }
}
