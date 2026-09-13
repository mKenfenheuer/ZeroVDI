# Connectors

A **connector** is a lightweight agent you deploy inside a network that ZeroVDI cannot reach
directly. It lets the gateway broker desktops and reach Proxmox backends that live behind NAT,
in a branch office, or in an isolated VLAN — without opening any inbound ports to that network.

Manage connectors at **Admin → Connectors** (`/admin/connectors`).

## How it works

The connector makes a single **outbound** TLS connection to the gateway and holds a control
WebSocket open. No inbound firewall rule is needed on the connector's side.

- **Enrollment.** You create a connector in the admin UI, which mints a **one-time
  registration token**. The agent exchanges that token for a long-lived **auth token**
  (`POST /agent/register`). Only a hash of the auth token is stored on the gateway; the
  plaintext is shown to the agent exactly once.
- **Control channel.** After enrolling, the agent connects `wss://<gateway>/agent/control`
  and keeps it open, sending a heartbeat every 20 seconds. Its live status, last-seen time,
  and last remote address appear on the Index page.
- **On demand.** When a user connects to a resource routed through a connector, the gateway
  asks the agent to dial the target `host:port`. The agent opens a per-request data channel
  (`wss://<gateway>/agent/data/<id>`) and bidirectionally pumps TCP ↔ WebSocket. RDP traffic
  runs with Nagle disabled to keep the session responsive.
- **Reachability probes.** The gateway can ask a connector to probe a host's RDP port and report
  round-trip time, so it can pick the best connector for a given target. The probe opens a TCP
  connection and sends an RDP Connection Request before closing — never a bare connect-and-close,
  which locks GNOME Remote Desktop 50 hosts out of the prober's address. Keep connector agents on the
  same release as the gateway for this reason.

All connector transport is **TLS only** — the gateway URL must be `https://` (the agent
refuses a plaintext URL and connects over `wss://`).

## Creating a connector

1. Go to **Admin → Connectors → Create**.
2. Give it a **name** and optional **description**, and leave it **Enabled**.
3. Optionally set an **Allow scope** — newline- or comma-separated host names or CIDR
   patterns this connector may serve. Empty means it is a candidate for every host; a scope
   limits which hosts it is probed and used for.
4. On save, a **one-time registration token** is displayed. Copy it now — it is shown once.

If a token is lost or you need to re-enroll, use **Regenerate** on the connector. This issues
a fresh registration token and **invalidates the old auth token**, so the running agent stops
working until it re-registers.

## Deploying the agent

The agent ships as a container image, `ghcr.io/mkenfenheuer/ksol-zerovdi/connector`. The reference
`docker-compose.yml` in the repository root carries it as the `ksol-zerovdi-connector` service behind
a profile, so it stays out of the way of deployments that do not need one:

```bash
docker compose --profile connector up -d
```

It persists no state unless you give it a volume.

Configure it with environment variables:

| Variable | Purpose |
|---|---|
| `ZEROVDI_URL` | Gateway base URL (`https://…`). Required. |
| `ZEROVDI_AUTH_TOKEN` | Long-lived auth token → run **stateless**, no config file needed. |
| `ZEROVDI_REGISTRATION_TOKEN` | One-time token → **self-enroll on first boot** and persist the auth token to the mounted volume. |
| `ZEROVDI_CONNECTOR_ID` | Optional, informational when injecting an auth token. |
| `ZEROVDI_CONNECTOR_HOME` | Override the config directory (default `~/.zerovdi-connector`). |

Supply **either** `ZEROVDI_AUTH_TOKEN` (stateless) **or** `ZEROVDI_REGISTRATION_TOKEN`
(self-enrolls, then persists). With a persisted volume, a registration token only needs to be
present on first boot.

### CLI

The same binary can be driven directly:

```
connector register --url https://<gateway> --token <registration-token>   # enroll + run
connector run                                                             # run from saved config
```

Config is written to `~/.zerovdi-connector/config.json` (override with
`ZEROVDI_CONNECTOR_HOME`).

## Routing a resource through a connector

A [resource](resources) or [backend](../administration/backends) can be pinned to a specific
connector (its `ForcedConnectorId`). When set, the gateway always reaches that target's
`host:port` through the chosen connector rather than dialing directly. Leave it unset to let
the gateway connect directly (or auto-select among scoped connectors).

## Reliability

The agent reconnects automatically with exponential backoff (1 s → capped at 30 s) if the
control channel drops, and resets its backoff on a clean reconnect. A connector that stops
sending heartbeats is shown offline on the Index.

## Related

- [Backends](../administration/backends) — Proxmox endpoints a connector can reach.
- [Resources](resources) — pin a published machine to a connector.
- [VDI pools](vdi-pools) — pooled desktops behind a connector.
