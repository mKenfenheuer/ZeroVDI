using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;
using KSol.ZeroVDI.Models;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Trust-on-first-use pinning of RDP host TLS certificates. Hosts almost always present a self-signed
/// certificate, so CA validation is impossible; instead the SHA-256 fingerprint of the certificate seen on
/// a resource's first successful connection is stored on the resource, and every later connection must
/// present the same certificate or it is refused before any credential is sent. An administrator forgets
/// the pin on the resource page after a legitimate change (host reinstalled, certificate renewed).
///
/// <c>HostCertificates:Mode</c>: <c>Tofu</c> (default — pin and enforce), <c>Audit</c> (pin and log
/// mismatches but allow, for rolling out), <c>Off</c> (accept any certificate, the pre-0.6.34 behaviour).
/// Singleton; opens its own scope for the DbContext because the check runs inside the relay's connect.
/// </summary>
public sealed class HostCertificatePolicy
{
    public enum Mode { Tofu, Audit, Off }

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HostCertificatePolicy> _logger;

    public Mode CurrentMode { get; }

    public HostCertificatePolicy(IServiceScopeFactory scopes, IConfiguration config, ILogger<HostCertificatePolicy> logger)
    {
        _scopes = scopes;
        _logger = logger;
        var raw = config["HostCertificates:Mode"];
        CurrentMode = Enum.TryParse<Mode>(raw, ignoreCase: true, out var m) ? m : Mode.Tofu;
        if (!string.IsNullOrEmpty(raw) && !Enum.TryParse<Mode>(raw, ignoreCase: true, out _))
            _logger.LogWarning("HostCertificates:Mode '{Raw}' is not Tofu/Audit/Off — using Tofu", raw);
    }

    /// <summary>Uppercase hex SHA-256 over the DER certificate, the form shown on the resource page.</summary>
    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    /// <summary>
    /// The certificate hook for one resource, or null when pinning is Off. Pins on first sight, accepts a
    /// match, and on a mismatch audits <c>HostCertificateMismatch</c> and refuses (Tofu) or allows (Audit).
    /// </summary>
    public Func<X509Certificate2, CancellationToken, Task<string?>>? CheckFor(string resourceId, string? resourceName, IAuditLogger audit)
    {
        if (CurrentMode == Mode.Off) return null;
        return async (cert, ct) =>
        {
            var fingerprint = Fingerprint(cert);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var res = await db.RDPResources.FirstOrDefaultAsync(r => r.Id == resourceId, ct);
            if (res == null) return null; // nothing to pin against (should not happen: the id is rebound to the concrete resource before connect)

            if (string.IsNullOrEmpty(res.HostCertFingerprint))
            {
                res.HostCertFingerprint = fingerprint;
                res.HostCertSubject = cert.Subject;
                res.HostCertPinnedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(AuditCategory.Resource, "HostCertificatePinned",
                    targetType: nameof(RDPResource), targetId: resourceId, targetName: resourceName,
                    detail: new { fingerprint, subject = cert.Subject, notAfterUtc = cert.NotAfter.ToUniversalTime() });
                _logger.LogInformation("Host certificate pinned for resource {Resource}: {Fingerprint} ({Subject})",
                    resourceName ?? resourceId, fingerprint, cert.Subject);
                return null;
            }

            if (string.Equals(res.HostCertFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return null;

            bool enforce = CurrentMode == Mode.Tofu;
            await audit.LogAsync(AuditCategory.Resource, "HostCertificateMismatch", success: false,
                targetType: nameof(RDPResource), targetId: resourceId, targetName: resourceName,
                detail: new { pinned = res.HostCertFingerprint, presented = fingerprint, subject = cert.Subject, enforced = enforce });
            _logger.LogWarning("Host certificate for resource {Resource} CHANGED: pinned {Pinned}, presented {Presented} ({Subject}) — {Action}",
                resourceName ?? resourceId, res.HostCertFingerprint, fingerprint, cert.Subject, enforce ? "refusing" : "allowing (Audit mode)");
            if (!enforce) return null;
            return "The desktop's security certificate has changed since it was first trusted. An administrator must review it (Resources → Backend & VM → Host certificate) before you can connect.";
        };
    }
}
