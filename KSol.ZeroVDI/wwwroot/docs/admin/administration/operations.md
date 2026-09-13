# Operations

**Admin → Operations** (`/admin/operations`, Admin only) is the one screen that answers *is this
deployment healthy, and does everything I configured actually work?* — without reading the container
log.

## Deployment

Version, environment, uptime, .NET runtime, account and session counts, and the two settings that are
easy to leave at an unsafe default:

- **`App:PublicBaseUrl` not set** — password-reset links, e-mail-change links and the OpenID Connect
  redirect URI are then built from the request's `Host` header, which a forged request can point
  elsewhere. Set it to the address your users actually reach ZeroVDI on.
- **`AllowedHosts` is `*`** — set it to the host names this deployment answers on so a forged `Host`
  header is rejected before it reaches any of that logic.

## Background workers

ZeroVDI's automation runs in background workers, and a worker that dies takes its feature with it
**silently**: an idle reaper that stopped leaves VMs running and billing, a reconciler that stopped
leaves desktops stuck in *Provisioning*. Each one reports after every pass:

| State | Meaning |
|---|---|
| **Healthy** | Last pass completed on schedule. |
| **Starting** | The process is young and the worker's startup delay hasn't elapsed. |
| **Overdue** | No pass for three intervals (at least five minutes). Something is blocking it. |
| **Failing** | The last pass threw; the message is shown below the table. |

The workers are the [VDI reconciler](../features/vdi-pools), the idle reaper, the Proxmox sync, the
resource status probe and the [recording retention](../features/recordings) sweep. Liveness is
in-memory, so the view always means "since this instance started" — which is the question being
asked.

## Test buttons

Each one exercises the real integration with the real configuration, so a failure here is a failure
your users would have hit:

| Button | What it proves |
|---|---|
| **Send a test message to me** | SMTP works end to end. Password resets, e-mail-change confirmations and e-mail MFA codes all ride on it. |
| **Test discovery** | The [identity provider](../features/identity-federation)'s discovery document is reachable and names an authorization and token endpoint. |
| **Test backends** | Every configured Proxmox cluster answers with the stored API token — the same call the broker makes when it clones a desktop. |

Every test is [audited](../features/audit) (`SmtpTested`, `OidcDiscoveryTested`, `BackendsTested`),
success or failure.

## Database & secrets

File path, size, audit-event count, and **pending migrations** — a restart applies them, and until it
does, the first query touching a new column fails at runtime.

The keyring line is the one to pay attention to when planning backups: **back up the DataProtection
keys directory together with the database.** Without those keys every stored VM credential and every
encrypted recording is unrecoverable. A missing `DataProtection:MasterKeyPassphrase` is called out
here too — without it the keyring falls back to a development passphrase.

## Storage

Recording count and size, free space on that volume, whether recordings are encrypted at rest, and
the retention rule. With no retention rule and no size cap, recordings grow until the disk is full —
the page says so once recordings exist.

## Related

- [Recordings](../features/recordings) · [VDI pools](../features/vdi-pools) ·
  [Configuration keys](../reference/configuration-keys) · [Audit log](../features/audit)
