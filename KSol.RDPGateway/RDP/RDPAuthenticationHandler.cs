using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KSol.RDPGateway.Models;
using Microsoft.AspNetCore.Identity;
using RDPGW.AspNetCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Gateway authentication handler supporting every method the RDG protocol exposes:
/// <list type="bullet">
/// <item>Basic   — username/password validated against ASP.NET Identity.</item>
/// <item>Digest  — RFC 2617/7616, validated against the stored HA1.</item>
/// <item>Negotiate / NTLM — NTLMv2 challenge/response validated against the stored NT hash.</item>
/// <item>PAA     — signed pre-auth token (extended auth cookie flow).</item>
/// </list>
/// The Digest HA1 and NTLM NT hash are derived automatically when a password is set (see
/// <see cref="DerivingPasswordHasher"/>), so no method asks the user for anything extra.
/// </summary>
public class RDPAuthenticationHandler : IRDPGWAuthenticationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RDPAuthenticationHandler> _logger;
    private readonly PaaTokenService _paa;

    // In-flight NTLM challenges, keyed by a hash of the Type 1 token, so the Type 2 challenge we
    // issued can be recalled when the Type 3 response arrives on the next leg.
    private static readonly ConcurrentDictionary<string, (byte[] Challenge, DateTimeOffset Issued)> NtlmChallenges = new();

    public RDPAuthenticationHandler(IServiceScopeFactory scopeFactory, ILogger<RDPAuthenticationHandler> logger, PaaTokenService paa)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _paa = paa;
    }

    public async Task<RDPGWAuthenticationResult> HandleBasicAuth(string auth)
    {
        string userName, password;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth));
            var sep = decoded.IndexOf(':');
            if (sep < 0)
                return RDPGWAuthenticationResult.Failed();
            userName = StripDomain(decoded.Substring(0, sep));
            password = decoded.Substring(sep + 1);
        }
        catch (FormatException)
        {
            return RDPGWAuthenticationResult.Failed();
        }

        _logger.LogInformation("Basic auth attempt for {User}", userName);

        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.FindByNameAsync(userName);
        if (user != null && await userManager.CheckPasswordAsync(user, password))
        {
            _logger.LogInformation("Basic auth succeeded for {User}", userName);
            return RDPGWAuthenticationResult.Success(user.Id);
        }

        _logger.LogWarning("Basic auth failed for {User}", userName);
        return RDPGWAuthenticationResult.Failed();
    }

    public async Task<RDPGWAuthenticationResult> HandleDigestAuth(string auth)
    {
        // First leg: client sends an empty credential (or none) -> issue a Digest challenge.
        if (string.IsNullOrWhiteSpace(auth) || !auth.Contains("response", StringComparison.OrdinalIgnoreCase))
        {
            return RDPGWAuthenticationResult.Challenge(DigestAuth.BuildChallenge(AuthCrypto.Realm));
        }

        var p = DigestAuth.Parse(auth);
        var userName = StripDomain(p.Username);

        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.FindByNameAsync(userName);
        if (user?.DigestHA1 == null)
        {
            _logger.LogWarning("Digest auth: no HA1 stored for {User}", userName);
            return RDPGWAuthenticationResult.Failed();
        }

        if (DigestAuth.Validate(p, user.DigestHA1))
        {
            _logger.LogInformation("Digest auth succeeded for {User}", userName);
            return RDPGWAuthenticationResult.Success(user.Id);
        }

        _logger.LogWarning("Digest auth failed for {User}", userName);
        return RDPGWAuthenticationResult.Failed();
    }

    public async Task<RDPGWAuthenticationResult> HandleNegotiateAuth(string auth)
    {
        PruneNtlm();

        if (string.IsNullOrWhiteSpace(auth))
            return RDPGWAuthenticationResult.Failed();

        byte[] token;
        try
        {
            token = Ntlm.TryUnwrapSpnego(Convert.FromBase64String(auth.Trim()));
        }
        catch (FormatException)
        {
            return RDPGWAuthenticationResult.Failed();
        }

        switch (Ntlm.GetMessageType(token))
        {
            case Ntlm.MessageType.Negotiate:
            {
                // Type 1 -> reply with a Type 2 challenge and remember it for the Type 3 leg.
                var challenge = Ntlm.NewServerChallenge();
                var type2 = Ntlm.BuildChallenge(challenge);
                NtlmChallenges[KeyFor(token)] = (challenge, DateTimeOffset.UtcNow);
                return RDPGWAuthenticationResult.Challenge(Convert.ToBase64String(type2));
            }

            case Ntlm.MessageType.Authenticate:
            {
                var t3 = Ntlm.ParseType3(token);
                if (t3 == null)
                    return RDPGWAuthenticationResult.Failed();

                // Recall the challenge we issued; if we can't correlate, reject.
                var challenge = TakeMostRecentChallenge();
                if (challenge == null)
                {
                    _logger.LogWarning("NTLM auth: no server challenge on record for {User}", t3.User);
                    return RDPGWAuthenticationResult.Failed();
                }

                var userName = StripDomain(t3.User);
                using var scope = _scopeFactory.CreateScope();
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

                var user = await userManager.FindByNameAsync(userName);
                if (user?.NtHash == null)
                {
                    _logger.LogWarning("NTLM auth: no NT hash stored for {User}", userName);
                    return RDPGWAuthenticationResult.Failed();
                }

                var ntHash = Convert.FromHexString(user.NtHash);
                if (Ntlm.VerifyNtlmV2(ntHash, t3.User, t3.Domain, challenge, t3.NtChallengeResponse))
                {
                    _logger.LogInformation("NTLM/Negotiate auth succeeded for {User}", userName);
                    return RDPGWAuthenticationResult.Success(user.Id);
                }

                _logger.LogWarning("NTLM/Negotiate auth failed for {User}", userName);
                return RDPGWAuthenticationResult.Failed();
            }

            default:
                return RDPGWAuthenticationResult.Failed();
        }
    }

    public async Task<RDPGWAuthenticationResult> HandlePAACookieAuth(byte[] paaCookie)
    {
        // The cookie may carry a trailing NUL or be UTF-16; normalize to a token string.
        var token = Encoding.UTF8.GetString(paaCookie).Trim('\0', ' ', '\r', '\n');
        if (string.IsNullOrEmpty(token))
            return RDPGWAuthenticationResult.Failed();

        var userId = _paa.Validate(token);
        if (userId == null)
        {
            _logger.LogWarning("PAA auth: invalid or expired token");
            return RDPGWAuthenticationResult.Failed();
        }

        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId);
        if (user == null)
            return RDPGWAuthenticationResult.Failed();

        _logger.LogInformation("PAA auth succeeded for {User}", user.UserName);
        return RDPGWAuthenticationResult.Success(user.Id);
    }

    /// <summary>Normalizes DOMAIN\user and user@domain to the bare username Identity stores.</summary>
    private static string StripDomain(string userName)
    {
        var slash = userName.IndexOf('\\');
        if (slash >= 0)
            userName = userName.Substring(slash + 1);
        return userName;
    }

    private static string KeyFor(byte[] token) => Convert.ToBase64String(SHA256.HashData(token));

    /// <summary>
    /// Returns and removes the most recently issued NTLM challenge. The gateway handles one tunnel
    /// setup at a time per connection, so the most recent outstanding challenge is the right one.
    /// </summary>
    private static byte[]? TakeMostRecentChallenge()
    {
        KeyValuePair<string, (byte[] Challenge, DateTimeOffset Issued)>? newest = null;
        foreach (var kv in NtlmChallenges)
            if (newest == null || kv.Value.Issued > newest.Value.Value.Issued)
                newest = kv;

        if (newest == null)
            return null;

        NtlmChallenges.TryRemove(newest.Value.Key, out var entry);
        return entry.Challenge;
    }

    private static void PruneNtlm()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        foreach (var kv in NtlmChallenges)
            if (kv.Value.Issued < cutoff)
                NtlmChallenges.TryRemove(kv.Key, out _);
    }
}
