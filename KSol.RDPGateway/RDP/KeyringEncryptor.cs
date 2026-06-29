using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Encrypts the DataProtection keyring itself at rest with AES-256-GCM, keyed by a passphrase derived
/// from configuration (<c>DataProtection:MasterKeyPassphrase</c>, typically an env var / secret).
///
/// Without this, ASP.NET Core DataProtection writes its master keys to disk in plaintext
/// (<c>&lt;!-- Warning: the key below is in an unencrypted form. --&gt;</c>). Those keys decrypt every
/// stored VM/IPMI/SSH credential, so a leaked key file (backup, volume snapshot, or an accidental
/// commit) compromises all secrets. Encrypting the keyring means the on-disk key material is useless
/// without the separately-held passphrase.
///
/// The passphrase is stretched to a 256-bit key with PBKDF2-HMAC-SHA256. Each key blob carries its own
/// random salt and nonce, so the same passphrase produces distinct ciphertexts.
/// </summary>
public sealed class KeyringEncryptor : IXmlEncryptor, IXmlDecryptor
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;   // AES-GCM standard nonce
    private const int TagSize = 16;     // AES-GCM tag
    private const int KeySize = 32;     // AES-256
    private const int Pbkdf2Iterations = 200_000;
    private static readonly XName ElementName = "encryptedKeyringBlob";

    /// <summary>
    /// Environment variable holding the keyring passphrase. ASP.NET Core maps the configuration key
    /// <c>DataProtection:MasterKeyPassphrase</c> to this env var name (':' → '__'). DataProtection
    /// reconstructs the decryptor by type name via a parameterless constructor (it does NOT use DI for
    /// custom <see cref="IXmlDecryptor"/> types), so the parameterless ctor reads the passphrase from the
    /// environment — the same source the configured value comes from in a deployed container.
    /// </summary>
    public const string PassphraseEnvVar = "DataProtection__MasterKeyPassphrase";
    public const string DevFallbackPassphrase = "dev-only-insecure-passphrase-do-not-use-in-production";

    private readonly byte[] _passphrase;

    public KeyringEncryptor(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Keyring passphrase must not be empty.", nameof(passphrase));
        _passphrase = Encoding.UTF8.GetBytes(passphrase);
    }

    /// <summary>
    /// The raw master passphrase bytes, for deriving sibling keys (e.g. <see cref="RecordingCryptor"/>)
    /// from the same secret. Returns a copy so callers can't mutate the internal key material.
    /// </summary>
    public byte[] PassphraseBytes => (byte[])_passphrase.Clone();

    /// <summary>
    /// Parameterless constructor used by DataProtection's <c>TypeForwardingActivator</c> when it unseals
    /// an existing key whose decryptor is named in the XML. Resolves the passphrase from the environment.
    /// </summary>
    public KeyringEncryptor() : this(ResolvePassphraseFromEnvironment())
    {
    }

    private static string ResolvePassphraseFromEnvironment()
    {
        var passphrase = Environment.GetEnvironmentVariable(PassphraseEnvVar);
        if (!string.IsNullOrWhiteSpace(passphrase)) return passphrase;

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
            return DevFallbackPassphrase;

        throw new InvalidOperationException(
            $"{PassphraseEnvVar} is not set; cannot decrypt the DataProtection keyring.");
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        var key = DeriveKey(salt);
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        // Envelope: salt || nonce || tag || ciphertext, base64-encoded inside a single element.
        var blob = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(salt, 0, blob, 0, SaltSize);
        Buffer.BlockCopy(nonce, 0, blob, SaltSize, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, SaltSize + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, blob, SaltSize + NonceSize + TagSize, ciphertext.Length);

        var element = new XElement(ElementName, Convert.ToBase64String(blob));
        return new EncryptedXmlInfo(element, typeof(KeyringEncryptor));
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        var blob = Convert.FromBase64String(encryptedElement.Value.Trim());
        if (blob.Length < SaltSize + NonceSize + TagSize)
            throw new CryptographicException("Encrypted keyring blob is malformed.");

        var salt = blob.AsSpan(0, SaltSize).ToArray();
        var nonce = blob.AsSpan(SaltSize, NonceSize).ToArray();
        var tag = blob.AsSpan(SaltSize + NonceSize, TagSize).ToArray();
        var ciphertext = blob.AsSpan(SaltSize + NonceSize + TagSize).ToArray();
        var plaintext = new byte[ciphertext.Length];

        var key = DeriveKey(salt);
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            // Wrong passphrase or tampered blob — fail loudly. The keyring cannot be loaded, which means
            // previously stored credentials can't be decrypted; an operator must restore the correct
            // DataProtection:MasterKeyPassphrase.
            throw new CryptographicException(
                "Failed to decrypt the DataProtection keyring. The DataProtection:MasterKeyPassphrase " +
                "does not match the one used to encrypt the on-disk keys.", ex);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        return XElement.Parse(Encoding.UTF8.GetString(plaintext));
    }

    private byte[] DeriveKey(byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(_passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
}
