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

## Oidc

Identity federation ([guide](../features/identity-federation)). Nothing is registered unless
`Oidc:Enabled` is true **and** `Authority` and `ClientId` are set.

| Key | Type | Default | Description |
|---|---|---|---|
| `Oidc:Enabled` | bool | `false` | Turn on OpenID Connect sign-in. |
| `Oidc:Authority` | string | — | Issuer URL; its discovery document must be reachable from the gateway. |
| `Oidc:ClientId` | string | — | Client id registered at the provider. |
| `Oidc:ClientSecret` | string | — | Client secret (confidential client, authorization code + PKCE). |
| `Oidc:DisplayName` | string | `Single sign-on` | Label on the sign-in button. |
| `Oidc:Scopes` | string[] | `["profile","email"]` | Scopes requested in addition to `openid`. |
| `Oidc:CallbackPath` | string | `/signin-oidc` | Redirect path; must match the provider's registration. |
| `Oidc:GroupsClaim` | string | `groups` | Claim carrying the user's groups. |
| `Oidc:AutoProvision` | bool | `true` | Create a ZeroVDI account on first sign-in. |
| `Oidc:RequireMappedGroup` | bool | `false` | Refuse sign-in unless a provider group matches a ZeroVDI group. |
| `Oidc:AdminGroups` | string[] | `[]` | Provider groups granted the Admin role (empty = never touch the role). |
| `Oidc:AuditorGroups` | string[] | `[]` | Provider groups granted the Auditor role. |
| `Oidc:SatisfiesMfa` | bool | `true` | Treat a federated sign-in as already multi-factor. |

## Redirection

Rules for following an RDP **Server Redirection** to a different host — what a session broker or load
balancer in front of a desktop farm sends to name the machine that should serve the user.

| Key | Type | Default | Description |
|---|---|---|---|
| `Redirection:FollowTargetHost` | bool | `true` | Follow a redirect that names a different host. Off = stay on the resource's own host (the pre-0.6.42 behaviour). |
| `Redirection:AllowedTargetHosts` | string[] | `[]` | When non-empty, the target must match one of these: a host name, an IP address, or a domain suffix written with a leading dot (`.rds.example.com`). |

Loopback, link-local (including the `169.254.169.254` cloud metadata address), multicast and
unspecified addresses are **always** refused, whatever the allow-list says.

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
