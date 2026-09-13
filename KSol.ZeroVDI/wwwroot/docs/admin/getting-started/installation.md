# Installation

ZeroVDI is an ASP.NET Core 9 web application. This page covers a basic deployment. Adapt to your
own hosting standards (reverse proxy, TLS termination, service supervision).

## Prerequisites

- **.NET 9 runtime** (or SDK for building from source).
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

1. Set the required configuration (see [Configuration](configuration)), at minimum a master
   passphrase and at least one Proxmox backend.
2. Start the app. The SQLite database (`app.db`) is created/migrated automatically.
3. Sign in with the seeded administrator account (or the account configured in your environment) and
   immediately enrol MFA if your policy requires it.
4. Add a [backend](../administration/backends), publish a [resource](../features/resources) or define
   a [VDI pool](../features/vdi-pools), and grant [access](../features/access-control).

## Running in a container

The published image runs as the **unprivileged user 1654** and listens on **8080**. That matters for
an existing deployment: a host bind mount carries its own ownership, so chown it once before
upgrading, or the app cannot write its database, keyring or recordings.

```bash
chown -R 1654:1654 /mnt/data/rdpgw
```

The bundled `docker-compose.yml` additionally drops every Linux capability except `NET_RAW` — which
the resource status probe needs for ICMP — sets `no-new-privileges`, caps the log files, and health-
checks the container against `/healthz`.

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
