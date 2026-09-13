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

## Browser security headers

Every response carries a **Content-Security-Policy** whose `script-src` names a fresh, unguessable
nonce for this one response instead of `'unsafe-inline'`. Scripts ZeroVDI renders are stamped with it
automatically; a script injected into a page — the payload of every reflected and stored XSS — has no
nonce and does not execute. Alongside it: `frame-ancestors 'none'` and `X-Frame-Options: DENY` (the
console drives keyboard and mouse into a real desktop and must never be framed by another site),
`X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, and a
`Permissions-Policy` granting camera and microphone to this origin only.

`style-src` still allows inline styles — the views use style attributes throughout, and a nonce covers
`<style>` elements, not attributes.

## Secrets at rest

- Stored VM credentials and VDI identity secrets are encrypted with an AES-GCM keyring derived from
  the `DataProtection:MasterKeyPassphrase`.
- Stored credentials are **never** rendered into the browser; the gateway injects them server-side.
- Recordings can be [encrypted at rest](recordings).
- The **portal sign-in password is stored only as ASP.NET Identity's salted hash.** Nothing reversible
  and nothing NTLM-usable is kept: the gateway authenticates to RDP hosts with the per-resource
  credentials, never with the portal password.
- **Raw stream dumps** (`RDPGW_DUMP_DIR`) write the decrypted session to disk outside the recording
  pipeline — no encryption, retention or tamper-evidence. They are a development-only tool and the
  variable is ignored outside the Development environment.

## Related

- [Device policy](device-policy) · [Identity federation](identity-federation) · [Audit](audit) · [Rate limiting](../administration/rate-limiting)
