using KSol.RDPGateway.Models;
using Microsoft.AspNetCore.Identity;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// A password hasher that wraps the default ASP.NET Identity hasher and, as a side effect of every
/// password set (registration, change, reset, admin create), derives and stores the HTTP Digest
/// HA1 and the NTLM NT hash on the <see cref="ApplicationUser"/>.
///
/// Identity calls <see cref="HashPassword"/> with the plaintext on every password set, passing the
/// very user instance it is about to persist, so capturing the derived secrets here means users
/// never have to provide anything extra to enable Digest and NTLM/Negotiate authentication.
/// </summary>
public class DerivingPasswordHasher : IPasswordHasher<ApplicationUser>
{
    private readonly PasswordHasher<ApplicationUser> _inner = new();

    public string HashPassword(ApplicationUser user, string password)
    {
        // Side-effect: derive the gateway auth secrets from the plaintext while we have it.
        user.DigestRealm = AuthCrypto.Realm;
        user.DigestHA1 = AuthCrypto.DigestHA1(user.UserName ?? string.Empty, AuthCrypto.Realm, password);
        user.NtHash = AuthCrypto.NtHash(password);

        return _inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
        => _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
}
