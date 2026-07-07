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

1. A user signs in (ASP.NET Identity, optionally with MFA).
2. They open a **resource** (a published machine) or a **VDI pool** (auto-provisioned desktop).
3. ZeroVDI ensures the target is powered on and reachable, then opens an RDP connection and relays
   the protocol to the browser over a WebSocket.
4. Stored SSO credentials are injected **server-side** — they are never sent to the browser.
5. Optionally the session is recorded for audit.

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

## Data & state

- **Database:** SQLite by default (single-instance). Postgres/SQL Server support is on the roadmap.
- **Secrets:** ASP.NET DataProtection plus an AES-GCM keyring derived from a master passphrase.
  Stored VM credentials and VDI identity secrets are encrypted at rest.
- **In-memory singletons:** `SessionTracker`, the redirection-token cache, and the connection
  readiness service are per-process today — ZeroVDI currently runs as a **single instance** (HA is
  on the roadmap).

## Next steps

- [Installation](installation)
- [Configuration](configuration)
- [Configuration keys reference](../reference/configuration-keys)
