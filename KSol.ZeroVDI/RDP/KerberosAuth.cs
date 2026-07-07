using Kerberos.NET.Client;
using Kerberos.NET.Configuration;
using Kerberos.NET.Credentials;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Optional Kerberos (AES) authentication helper for the CredSSP client. When a realm and KDC are
/// configured (per-backend or globally) this can obtain a Kerberos service ticket / AP-REQ for the
/// target host's <c>TERMSRV/&lt;host&gt;</c> SPN using the vetted Kerberos.NET library, giving an
/// AES128/256 security context with no RC4.
///
/// This is the preferred, current-standards path. When the realm/KDC is unconfigured or unreachable,
/// callers fall back to NTLMv2 (<see cref="NtlmClient"/>), which keeps workgroup Proxmox VMs working
/// with zero configuration. The GSS confidentiality wrap used for the CredSSP public-key/credential
/// sealing is non-trivial; this helper currently focuses on ticket acquisition and reports
/// availability so the CredSSP client can choose the mechanism.
/// </summary>
public sealed class KerberosAuth
{
    private readonly string _realm;
    private readonly string _kdc;
    private readonly ILogger _logger;

    public KerberosAuth(string realm, string kdc, ILogger logger)
    {
        _realm = realm;
        _kdc = kdc;
        _logger = logger;
    }

    /// <summary>True when a realm and KDC are configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_realm) && !string.IsNullOrWhiteSpace(_kdc);

    /// <summary>
    /// Authenticates to the KDC with the supplied credentials and obtains a service ticket for
    /// <c>TERMSRV/&lt;targetHost&gt;</c>. Returns the GSS-API AP-REQ token, or null if Kerberos is
    /// unavailable/failed (caller should fall back to NTLM).
    /// </summary>
    public async Task<byte[]?> TryGetApReqAsync(string user, string password, string targetHost,
        CancellationToken ct)
    {
        if (!IsConfigured) return null;

        try
        {
            var config = Krb5Config.Default();
            config.Realms[_realm].Kdc.Add(_kdc);
            config.Defaults.DefaultRealm = _realm;

            using var client = new KerberosClient(config, logger: null);
            client.PinKdc(_realm, _kdc);

            var credential = new KerberosPasswordCredential(user, password, _realm);
            await client.Authenticate(credential);

            var spn = $"TERMSRV/{targetHost}";
            var apReq = await client.GetServiceTicket(spn, Kerberos.NET.Entities.ApOptions.MutualRequired,
                s4u: null, s4uTicket: null, u2uServerTicket: null);

            // The AP-REQ (GSS-API framed) is the token we would feed into SPNEGO/CredSSP.
            return apReq.EncodeGssApi().ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Kerberos: ticket acquisition for {Host} failed; falling back to NTLM", targetHost);
            return null;
        }
    }
}
