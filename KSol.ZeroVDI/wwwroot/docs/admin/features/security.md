# Security & MFA

ZeroVDI builds on ASP.NET Identity and adds policy-driven multi-factor authentication, login
throttling, and encrypted secret storage.

## Multi-factor authentication

### Policy

MFA enforcement is driven by the `Mfa` configuration section:

```jsonc
{
  "Mfa": {
    "RequireForAll": false,     // true → every user must enrol
    "RequiredRoles": ["Admin"]  // otherwise only these roles are required (secure default)
  }
}
```

When a user is *required* but *not yet enrolled*, `MfaEnforcementMiddleware` redirects them to the
authenticator setup page on every navigation, with an allow-list (Identity/Account/Manage, login,
logout, static assets, the WS path, Home/Error) so they can finish setup or sign out without a loop.
The setup page shows an "MFA required" banner.

### Methods

Either method satisfies the policy:

- **Authenticator app** (TOTP) — the standard Identity authenticator enrollment.
- **Email code** — a one-time code emailed via the configured SMTP sender, with a 5-minute lifespan.

The two-factor management page shows the status of both methods. At login, the challenge auto-sends
an email code when that method is chosen and offers a method switcher and resend.

### Auditing

`MfaEnrollmentRequired`, `MfaEnabled`, `MfaDisabled`, `MfaReset`, `MfaEmailEnabled`,
`MfaEmailDisabled`, and `LoginTwoFactorSucceeded/Failed` (with the method) are all recorded. The
admin Users list shows an MFA-status column.

## Login throttling & lockout

- **Lockout on failure** is enabled, so repeated bad passwords lock the account per Identity policy.
- **Rate limiting** applies an IP fixed-window limit to the Identity/login pages — see
  [Rate limiting](../administration/rate-limiting).

## Host certificate pinning (trust on first use)

RDP hosts almost always present a self-signed TLS certificate, so the gateway cannot validate it
against a CA. Instead it **pins**: the SHA-256 fingerprint of the certificate seen on a resource's
first successful connection is stored on the resource (audited as `HostCertificatePinned`), and every
later connection must present the same certificate. A different certificate is refused *before any
credential is sent* and recorded as a failed `HostCertificateMismatch`; the user sees "The desktop's
security certificate has changed since it was first trusted…". After a legitimate change (host
reinstalled, certificate renewed), an administrator forgets the pin on the resource's **Backend & VM**
tab (`HostCertificateReset`) and the next connection pins the new one.

In addition, the CredSSP (NLA) handshake verifies the host's sealed public-key confirmation, which an
active man-in-the-middle cannot produce without the account password — so even the very first
connection is protected against interception of the credentials.

`HostCertificates:Mode` selects the behaviour: `Tofu` (default — pin and enforce), `Audit` (pin and log
mismatches but allow the connection; useful while rolling out), `Off` (accept any certificate).

Proxmox backends verify the cluster's TLS certificate by default (**Verify TLS certificate** on the
backend form); turn it off only for a self-signed lab cluster.

## Secrets at rest

- Stored VM credentials and VDI identity secrets are encrypted with an AES-GCM keyring derived from
  the `DataProtection:MasterKeyPassphrase`.
- Stored credentials are **never** rendered into the browser; the gateway injects them server-side.
- Recordings can be [encrypted at rest](recordings).

## Related

- [Device policy](device-policy) · [Audit](audit) · [Rate limiting](../administration/rate-limiting)
