using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Provides the persistent signing and encryption certificates used by the OpenIddict OAuth/OIDC
/// server in production. Unlike the development certificates (which are ephemeral and regenerate on
/// every restart — invalidating all issued tokens), these are generated once and stored on disk so
/// tokens remain valid across restarts and across multiple instances sharing the same volume.
///
/// Certificates are self-signed RSA-2048 PFX files stored alongside the SQLite database (the
/// <c>Data</c> directory under the content root, e.g. <c>/app/Data</c> in the container). The
/// directory and password can be overridden via <c>Oidc:CertificatePath</c> and
/// <c>Oidc:CertificatePassword</c>.
/// </summary>
public static class CertificateProvider
{
    /// <summary>
    /// Returns the OIDC signing certificate, generating and persisting it on first use.
    /// </summary>
    public static X509Certificate2 GetSigningCertificate(IConfiguration config, IHostEnvironment env)
        => GetOrCreate(config, env, "oidc-signing", "KSol.IT RDP Gateway OIDC Signing", X509KeyUsageFlags.DigitalSignature);

    /// <summary>
    /// Returns the OIDC encryption certificate, generating and persisting it on first use.
    /// </summary>
    public static X509Certificate2 GetEncryptionCertificate(IConfiguration config, IHostEnvironment env)
        => GetOrCreate(config, env, "oidc-encryption", "KSol.IT RDP Gateway OIDC Encryption", X509KeyUsageFlags.KeyEncipherment);

    private static X509Certificate2 GetOrCreate(IConfiguration config, IHostEnvironment env, string fileName, string subject, X509KeyUsageFlags usage)
    {
        // Default to the "Data" directory next to the SQLite database (DataSource=Data/app_db.sqlite,
        // resolved against the content root — /app/Data in the container).
        var dir = config["Oidc:CertificatePath"] ?? Path.Combine(env.ContentRootPath, "Data");
        var password = config["Oidc:CertificatePassword"] ?? "ksol-rdpgw-oidc";
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, fileName + ".pfx");

        // Load the existing certificate if present and still valid.
        if (File.Exists(path))
        {
            var existing = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
            if (existing.NotAfter > DateTimeOffset.UtcNow.AddDays(1))
                return existing;
        }

        // Generate a fresh self-signed RSA certificate valid for 5 years.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));

        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        // Persist as a password-protected PFX so the same key is reused after a restart.
        var pfx = certificate.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(path, pfx);

        return X509CertificateLoader.LoadPkcs12(pfx, password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
    }
}
