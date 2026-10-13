# Security policy

ZeroVDI sits between every user and every desktop in a deployment. It terminates TLS, holds stored
credentials, and relays keystrokes and screen contents. A vulnerability here is not a small one, and
reports are taken seriously.

## Reporting a vulnerability

**Please do not open a public issue.**

Report privately, either way:

- GitHub → the repository's **Security** tab → **Report a vulnerability** (private advisory), or
- e-mail **maximilian.kenfenheuer@ksol.it**.

Helpful to include: the version (shown on **Admin → Operations**), what an attacker gains, and the
smallest reproduction you have. A proof of concept is welcome but not required — a clear description
of the flawed code path is enough.

You can expect an acknowledgement within a few days and an assessment shortly after. You will be
credited in the changelog unless you would rather not be.

## Supported versions

ZeroVDI is developed on `main` and released as versioned container images. Fixes land on `main` and
in the next image; there are no long-term support branches. Run a recent version.

## Scope

In scope — anything that lets someone:

- reach a desktop, recording, or credential they were not granted;
- escape the device policy (clipboard, audio, microphone, camera) it is configured to enforce;
- escalate to Admin or Auditor, or defeat MFA enforcement;
- read or forge audit entries, or decrypt recordings or stored credentials;
- crash, hang, or exhaust the gateway from a browser session or a malicious RDP host;
- execute script in the console (the CSP allows no inline script — a bypass is a finding).

Out of scope:

- Anything requiring an administrator to act against their own deployment. Administrators can
  configure hosts, credentials and shutdown commands by design.
- Missing hardening on a deployment that ignores the documented required configuration (no
  `DataProtection__MasterKeyPassphrase`, `AllowedHosts` left at `*`, no TLS in front).
- The unsigned desktop installers. They are unsigned deliberately — see
  [`.github/workflows/desktop-release.yml`](.github/workflows/desktop-release.yml).
- Findings against a modified build. The licence does not permit modified deployments anyway.

## What ZeroVDI already does

Useful context before reporting, and a checklist if you are auditing:

- **Credentials** are encrypted at rest with a keyring sealed by `DataProtection__MasterKeyPassphrase`,
  and are never rendered into the browser — the gateway injects them server-side.
- **Portal passwords** are stored only as ASP.NET Identity's salted hash. Nothing reversible or
  NTLM-usable is kept.
- **Host identity**: the CredSSP server public-key confirmation is verified, and host certificates are
  pinned on first use (`HostCertificates:Mode`).
- **Device policy** is enforced by inspecting the relayed protocol stream, so a modified browser
  client cannot join a blocked channel.
- **Server redirection** to a different host is validated — loopback, link-local (including the cloud
  metadata address) and other non-routable targets are refused.
- **Browser**: a nonce-based CSP with no `'unsafe-inline'` for scripts, `frame-ancestors 'none'`,
  `X-Frame-Options: DENY`, nosniff, a restrictive `Permissions-Policy`.
- **Container**: runs as UID 1654, all Linux capabilities dropped except `NET_RAW`,
  `no-new-privileges`.
- **Supply chain**: images are cosign-signed, and CI fails on any NuGet package with a known
  vulnerability.
