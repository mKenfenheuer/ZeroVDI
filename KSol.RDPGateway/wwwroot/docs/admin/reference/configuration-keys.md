# Configuration keys reference

A consolidated list of the main configuration keys. All are read from the standard ASP.NET Core
configuration providers (`appsettings.json`, environment variables, user-secrets). Environment-variable
form uses `__` for nesting, e.g. `Mfa__RequireForAll=true`.

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
| `RateLimiting:Auth:PermitLimit` | int | `10` | Requests per window on Identity/login pages, per IP. |
| `RateLimiting:Auth:WindowSeconds` | int | `60` | Window length for the auth policy. |
| `RateLimiting:Ws:PermitLimit` | int | `30` | Requests per window on the WS relay handshake, per IP. |
| `RateLimiting:Ws:WindowSeconds` | int | `60` | Window length for the ws policy. |

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
