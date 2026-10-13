# Installation

ZeroVDI is an ASP.NET Core 9 web application. This page covers a basic deployment. Adapt to your
own hosting standards (reverse proxy, TLS termination, service supervision).

## Prerequisites

- **.NET 9 runtime** to run the gateway. Building from source additionally needs the .NET 10 SDK —
  the [connector agent](../features/connectors) targets it — or use the container image and need
  neither.
- A **Proxmox VE** cluster reachable from the ZeroVDI host, with an API token (see
  [Backends](../administration/backends)).
- A reverse proxy terminating **HTTPS** in front of the app (nginx, Caddy, IIS, Traefik, …).
  WebSockets must be allowed through — the console relay depends on them.
- SMTP server details if you intend to use email-based MFA or notifications.

## Build from source

```bash
# from the repository root
dotnet build KSol.ZeroVDI.sln -c Release
dotnet test  KSol.ZeroVDI.sln -c Release
dotnet run   --project KSol.ZeroVDI -c Release
```

The Tailwind CSS bundle is rebuilt automatically when Node is present; the compiled
`wwwroot/css/app.css` is committed, so hosts without Node still work (the build target skips itself,
and `-p:SkipTailwindBuild=true` says so explicitly on a build agent).

Unit tests live in `KSol.ZeroVDI.Tests` and cover the code where a mistake is expensive and a manual
check is impractical: the protocol decoders against hostile input, the redirect-target rules, the
device-policy channel guard, and the account-lockout predicates. CI runs them, plus a NuGet
vulnerability audit, before any image is built.

## First run

1. Set the required configuration (see [Configuration](configuration)) — at minimum
   `DataProtection:MasterKeyPassphrase`, without which the app refuses to start in Production.
2. Start the app. The SQLite database (`Data/app_db.sqlite` by default) is created and migrated
   automatically, as is the DataProtection keyring beside it.
3. Sign in as the initial administrator. Unless you set `Bootstrap:AdminPassword`, a strong random
   password was generated and written to the log **once** at first start — take it from there:
   ```bash
   docker compose logs ksol-zerovdi-app | grep "Bootstrapped initial admin"
   ```
   Change it immediately, and enrol MFA (it is required for Admins by default).
4. Add a [backend](../administration/backends), publish a [resource](../features/resources) or define
   a [VDI pool](../features/vdi-pools), and grant [access](../features/access-control).
5. Open **Admin → [Operations](../administration/operations)** and check that the background workers
   are healthy and the SMTP and backend tests pass.

## Running in a container

This is the intended deployment. The reference stack is in `docker-compose.yml` at the repository
root, with every setting documented in `.env.example`:

```bash
cp .env.example .env
openssl rand -base64 48        # put this in DataProtection__MasterKeyPassphrase
docker compose up -d
docker compose logs ksol-zerovdi-app | grep "Bootstrapped initial admin"
```

Images are published to the GitHub Container Registry and signed with
[cosign](https://docs.sigstore.dev/):

- `ghcr.io/mkenfenheuer/zerovdi` — the gateway
- `ghcr.io/mkenfenheuer/zerovdi/connector` — the [connector agent](../features/connectors)
  (start it with `docker compose --profile connector up -d`)

`latest` and `main` follow the main branch. A tagged release also publishes its version (`0.6.46`)
and minor line (`0.6`); pin one of those rather than `latest` if you want reproducible rollbacks
(`APP_IMAGE` and `CONNECTOR_IMAGE` in `.env`).

Both images run as the **unprivileged user 1654**, and the gateway listens on **8080**. That matters
if you use a host bind mount instead of the named volume: the mount carries its own ownership, so
chown it once, or the app cannot write its database, keyring or recordings.

```bash
chown -R 1654:1654 /path/to/zerovdi-data
```

The compose file additionally drops every Linux capability except `NET_RAW` — which the resource
status probe needs for ICMP — sets `no-new-privileges`, caps the log files, and health-checks the
container against `/healthz`.

**Wake-on-LAN** needs host networking: the magic packet goes to `255.255.255.255`, which a bridged
container network traps in its own subnet. If you publish physical machines, swap the `ports` block
for `network_mode: "host"` as described in the comments in `docker-compose.yml`.

`/healthz` is anonymous and deliberately shallow: it answers *is this process serving requests?*, not
*is Proxmox reachable?* A probe that failed on a backend hiccup would have the orchestrator restart a
healthy gateway and drop everyone's desktops. Dependency health belongs on the
[operations page](../administration/operations).

Everything that must survive a restart lives under `/app/Data`: the SQLite database, the
DataProtection keyring and the recordings. **Back up that whole volume** — without the keyring, every
stored VM credential and every encrypted recording is unrecoverable.

## Reverse proxy notes

- Forward the `Upgrade`/`Connection` headers so WebSocket upgrades reach `/ws`.
- Forward the real client IP (`X-Forwarded-For`) — it is recorded in the [audit log](../features/audit)
  and used by [rate limiting](../administration/rate-limiting).
- Keep idle/read timeouts generous on the WebSocket path; console sessions are long-lived.
- Set `App__PublicBaseUrl` to the address users type (e.g. `https://vdi.example.com`) and restrict
  `AllowedHosts` to that name, so password-reset links can never be poisoned through a forged `Host`
  header. List the proxy in `ForwardedHeaders__KnownProxies` so rate limiting and the audit log see
  real client addresses.
- The app sends a Content-Security-Policy and `X-Frame-Options: DENY`; do not embed the console in an
  iframe on another site.

## Next steps

- [Configuration](configuration)
- [Configuration keys reference](../reference/configuration-keys)
