# Configuration keys reference

A consolidated list of the main configuration keys. All are read from the standard ASP.NET Core
configuration providers (`appsettings.json`, environment variables, user-secrets). Environment-variable
form uses `__` for nesting, e.g. `Mfa__RequireForAll=true`.

## App

| Key | Type | Default | Description |
|---|---|---|---|
| `App:PublicBaseUrl` | URL | — (request host) | The address users reach ZeroVDI at, e.g. `https://vdi.example.com`. Used for password-reset and e-mail-change links and the connector enrolment command. **Set it in production** — without it those links are built from the request's `Host` header, which an attacker can forge unless `AllowedHosts` is restricted. |
| `AllowedHosts` | string | `*` | Standard ASP.NET Core host filtering. Restrict to your public host name(s), e.g. `vdi.example.com`. |

## DataProtection

| Key | Type | Default | Description |
|---|---|---|---|
| `DataProtection:MasterKeyPassphrase` | string | — | Master passphrase for the AES-GCM keyring. Encrypts stored credentials, VDI secrets, and (optionally) recordings. **Back this up.** |

## Mfa

| Key | Type | Default | Description |
|---|---|---|---|
| `Mfa:RequireForAll` | bool | `false` | Require MFA for every user. |
| `Mfa:RequiredRoles` | string[] | `["Admin"]` | Roles for which MFA is required (when not requiring all). |

## Sessions

| Key | Type | Default | Description |
|---|---|---|---|
| `Sessions:MaxConcurrentPerUser` | int | `0` | Max simultaneous sessions per user; `0` = unlimited. |

## RateLimiting

| Key | Type | Default | Description |
|---|---|---|---|
| `RateLimiting:AuthPermitLimit` | int | `10` (shipped `appsettings.json`: `30`) | Requests per window on Identity/login pages, per client IP. |
| `RateLimiting:AuthWindowSeconds` | int | `60` | Window length for the auth policy. |
| `RateLimiting:WsPermitLimit` | int | `30` (shipped `appsettings.json`: `45`) | Requests per window on the WS relay handshake, per client IP. |
| `RateLimiting:WsWindowSeconds` | int | `60` | Window length for the ws policy. |

The limits are keyed on the client IP. Behind a reverse proxy you **must** list the proxy in
`ForwardedHeaders:KnownProxies` / `KnownNetworks` (or set `ForwardedHeaders:TrustAllProxies=true`
when the app is reachable only through the proxy) — otherwise every client shares the proxy's single
bucket and a handful of users can lock the login page for everyone.

## ForwardedHeaders

| Key | Type | Default | Description |
|---|---|---|---|
| `ForwardedHeaders:KnownProxies` | string[] | loopback only | Reverse-proxy IPs whose `X-Forwarded-*` headers are honoured. |
| `ForwardedHeaders:KnownNetworks` | string[] (`cidr/prefix`) | — | Reverse-proxy networks whose `X-Forwarded-*` headers are honoured. |
| `ForwardedHeaders:TrustAllProxies` | bool | `false` | Honour forwarded headers from any hop. Only when the app is reachable solely through the proxy. |

## Bootstrap

| Key | Type | Default | Description |
|---|---|---|---|
| `Bootstrap:AdminEmail` | string | `admin@example.com` | Username/e-mail of the initial administrator. |
| `Bootstrap:AdminPassword` | string | random, logged once | Password of the initial administrator on first start. |

## HostCertificates

| Key | Type | Default | Description |
|---|---|---|---|
| `HostCertificates:Mode` | `Tofu` / `Audit` / `Off` | `Tofu` | RDP host TLS certificate pinning: enforce (refuse a changed certificate), log only, or disabled. See [Security](../features/security). |

## Recording

| Key | Type | Default | Description |
|---|---|---|---|
| `Recording:EncryptAtRest` | bool | `false` | Encrypt track files with AES-256-CTR. |
| `Recording:RetentionDays` | int | `0` | Purge recordings older than N days; `0` = keep forever. |
| `Recording:MaxTotalGB` | int | `0` | Oldest-first size cap in GB; `0` = no cap. |

## Notes

- Backends (Proxmox endpoints) are configured in the database via the admin UI, not in
  `appsettings.json` — see [Backends](../administration/backends).
- Per-pool VDI settings (clone mode, identity, generated credentials, …) live on the pool record,
  not in config — see [VDI pools](../features/vdi-pools).

## Related

- [Configuration](../getting-started/configuration)
