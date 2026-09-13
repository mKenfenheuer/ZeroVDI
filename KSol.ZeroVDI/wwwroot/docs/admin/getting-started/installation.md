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
dotnet build KSol.ZeroVDI. -c Release
dotnet run  --project KSol.ZeroVDI. -c Release
```

The Tailwind CSS bundle is rebuilt automatically when Node is present; the compiled
`wwwroot/css/app.css` is committed, so hosts without Node still work (the build target is skipped).

## First run

1. Set the required configuration (see [Configuration](configuration)), at minimum a master
   passphrase and at least one Proxmox backend.
2. Start the app. The SQLite database (`app.db`) is created/migrated automatically.
3. Sign in with the seeded administrator account (or the account configured in your environment) and
   immediately enrol MFA if your policy requires it.
4. Add a [backend](../administration/backends), publish a [resource](../features/resources) or define
   a [VDI pool](../features/vdi-pools), and grant [access](../features/access-control).

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
