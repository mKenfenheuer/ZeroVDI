using KSol.RDPGateway.RDP;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KSol.RDPGateway.Data;

/// <summary>
/// EF Core value converter that transparently encrypts a string column at rest using
/// <see cref="CredentialProtector"/> (ASP.NET Core DataProtection, keyring encrypted at rest — see
/// <c>Program.cs</c>). The property stays a plain <c>string?</c> in the model, so all read/write sites
/// are unchanged, but what lands in the database is always ciphertext.
///
/// Used for sensitive values that must never sit in plaintext columns: the per-user NTLM
/// <c>NtHash</c> (an unsalted MD4 that would otherwise enable offline cracking / pass-the-hash from a
/// DB leak) and the Proxmox API token secret.
///
/// Decryption failures (e.g. a value written before encryption was enabled, or an unrecoverable
/// keyring) yield <c>null</c> rather than throwing, matching <see cref="CredentialProtector.Unprotect"/>
/// semantics — callers treat that as "no usable value".
/// </summary>
public sealed class EncryptedStringConverter : ValueConverter<string?, string?>
{
    public EncryptedStringConverter(CredentialProtector protector)
        : base(
            plaintext => protector.Protect(plaintext),
            stored => protector.Unprotect(stored))
    {
    }
}
