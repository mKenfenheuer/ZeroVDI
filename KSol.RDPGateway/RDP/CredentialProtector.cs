using Microsoft.AspNetCore.DataProtection;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Encrypts/decrypts stored VM credentials at rest using ASP.NET Core DataProtection. The keyring is
/// persisted on disk (see <c>Program.cs</c>) so protected values survive restarts; if the keyring is
/// lost (e.g. the volume is wiped) previously stored credentials become undecryptable and an admin
/// must re-enter them.
/// </summary>
public sealed class CredentialProtector
{
    private readonly IDataProtector _protector;

    public CredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("RDPResourceUserAuthorization.Credentials");
    }

    /// <summary>Encrypts a value, or returns null for null/empty input.</summary>
    public string? Protect(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    /// <summary>
    /// Decrypts a protected value. Returns null for null/empty input or if decryption fails (e.g. the
    /// keyring changed) — callers treat a null as "no usable credential".
    /// </summary>
    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try { return _protector.Unprotect(protectedValue); }
        catch { return null; }
    }
}
