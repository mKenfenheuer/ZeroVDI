# Architecture overview

ZeroVDI is an ASP.NET Core (net9.0) application that brokers RDP connections from a web browser to
virtual machines hosted on **Proxmox VE**. Users never install an RDP client; the desktop renders in
the browser over a WebSocket relay.

## High-level flow

```
Browser ──HTTPS──▶ ZeroVDI ──RDP──▶ Target VM (Windows/Linux)
   ▲   console.js      │  WS relay        on Proxmox
   │                   │
   └───WebSocket───────┘
```

1. A user signs in — ASP.NET Identity with a local password, or an
   [OpenID Connect provider](../features/identity-federation) — optionally with MFA.
2. They open a **resource** (a published machine) or a **VDI pool** (auto-provisioned desktop).
3. ZeroVDI ensures the target is powered on and reachable, then opens an RDP connection and relays
   the protocol to the browser over a WebSocket.
4. Stored SSO credentials are injected **server-side** — they are never sent to the browser.
5. Optionally the session is recorded for audit.

The RDP client is implemented in this codebase on both sides — C# on the gateway (`RDP/`) and
JavaScript in the browser (`wwwroot/lib/rdpweb/`). There is no FreeRDP or `guacd` in the path. A host
that is not directly reachable is reached through a [connector](../features/connectors): a small agent
inside that network which dials out to the gateway and tunnels the connection back.

## Key components

| Component | Responsibility |
|---|---|
| `HomeController` / `ConnectController` | User-facing dashboard, console page, connection setup. |
| `RdpWebSocketController` | The WebSocket relay endpoint; registers the live session. |
| `ProxmoxClient` / `ProxmoxBackendProvider` | Talks to Proxmox APIs across one or more backends. |
| `ResourceAccessService` | The single authorization gate (direct ∪ group access). |
| `VdiProvisioningService` | Clones desktops from templates for VDI pools (lazy on connect). |
| `SessionTracker` | In-memory registry of live sessions; powers the sessions view + limits. |
| `AuditLogger` | Append-only structured audit trail. |
| `DevicePolicyService` | Central clipboard/audio/mic/camera policy enforcement. |
| `RecordingMuxService` / `RecordingCryptor` | Records, encrypts, and integrity-stamps sessions. |
| `ChannelPolicyGuard` | Inspects the relayed stream and refuses a session that asks for a channel device policy disables. |
| `HostCertificatePolicy` | Trust-on-first-use pinning of RDP host TLS certificates. |
| `RedirectionTargetPolicy` | Decides whether a broker's redirect to a different host may be followed. |
| `ExternalIdentityService` | Resolves an OpenID Connect sign-in to an account and syncs its groups and roles. |
| `AdminAccountSafety` | Refuses the changes that would leave nobody able to administer the deployment. |
| `VdiReconcileService` | Repairs instances the connect-time path could not finish (restart mid-clone, vanished VM). |
| `ServiceHeartbeats` | Background-worker liveness, shown on the operations page. |

## Data & state

- **Database:** SQLite by default (single-instance). Postgres/SQL Server support is on the roadmap.
- **Secrets:** ASP.NET DataProtection plus an AES-GCM keyring derived from a master passphrase.
  Stored VM credentials and VDI identity secrets are encrypted at rest.
- **In-memory singletons:** `SessionTracker`, the redirection-token cache, and the connection
  readiness service are per-process today — ZeroVDI currently runs as a **single instance** (HA is
  on the roadmap).

## Processes

| Process | Role |
|---|---|
| **Gateway** (`KSol.ZeroVDI`, .NET 9) | Everything above: web UI, broker, RDP relay. |
| **Connector** (`KSol.ZeroVDI.Connector`, .NET 10) | Optional, one per unreachable network. Outbound-only. |
| **Desktop client** (`KSol.ZeroVDI.Desktop`, Electron) | Optional. A window around the same web console; changes nothing server-side. |

## Next steps

- [Installation](installation)
- [Configuration](configuration)
- [Configuration keys reference](../reference/configuration-keys)
