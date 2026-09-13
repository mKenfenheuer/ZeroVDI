<p align="center">
  <img src="KSol.ZeroVDI/wwwroot/img/zerovdi-logo.svg" alt="ZeroVDI" height="72">
</p>

<p align="center">
  <strong>Virtual desktops in a browser tab. No client. No plugin. No agent. No <code>.rdp</code> file.</strong>
</p>

<p align="center">
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Source--Available-blue"></a>
  <a href="../../actions/workflows/ci.yml"><img alt="Build &amp; test" src="https://img.shields.io/github/actions/workflow/status/mkenfenheuer/zerovdi/ci.yml?label=build%20%26%20test"></a>
  <a href="../../pkgs/container/ksol-zerovdi"><img alt="Container image" src="https://img.shields.io/badge/ghcr.io-ksol--zerovdi-1f6feb"></a>
  <img alt="Last commit" src="https://img.shields.io/github/last-commit/mkenfenheuer/zerovdi/main">
</p>

---

**ZeroVDI** is a self-hosted VDI platform for **Proxmox VE**. It brokers Windows and Linux desktops
and delivers them straight into the browser — the RDP stream is decoded live in JavaScript over a
WebSocket, so a user needs nothing but a tab and a login.

It is not a wrapper around FreeRDP or a `guacd` front end. The RDP client is implemented from the
wire up, in C# on the gateway and in JavaScript in the browser: RemoteFX Progressive and H.264
(WebCodecs) decoding, dynamic virtual channels, clipboard, audio, microphone, camera, NLA/CredSSP
with host-certificate pinning, and server redirection all live in this repository.

```
┌──────────┐   HTTPS + WSS   ┌───────────────┐   RDP / TLS + NLA   ┌──────────────┐
│ Browser  │ ──────────────► │ ZeroVDI       │ ──────────────────► │ Windows /    │
│ console  │ ◄────────────── │ gateway       │ ◄────────────────── │ Linux desktop│
└──────────┘                 └───────┬───────┘                     └──────────────┘
                                     │ API                    ┌──────────────┐
                                     └───────────────────────►│ Proxmox VE   │
                                       clone · start · stop   └──────────────┘
```

## What you get

**For the people using it**
- A desktop in a browser tab — clipboard, sound, microphone, camera, HiDPI, and a resolution that
  follows the window.
- Nothing to install, nothing to download, nothing to configure. An optional Electron desktop app
  exists for people who want a window without browser chrome.
- Reconnects on its own after a network blip, keeping the last frame on screen instead of a black one.

**For the people running it**
- **Desktop pools** — clone from a Proxmox template on first connect, per-user or per-group, with a
  reconciler that cleans up after interrupted provisioning instead of leaving desktops stuck.
- **Publish individual machines** — an existing VM or a physical host, woken by Wake-on-LAN and put
  back to sleep when idle.
- **Connectors** — a small agent deployed inside an isolated network; it dials out to the gateway and
  tunnels desktop connections back, so no inbound firewall rule is needed.
- **Access control** as the union of direct grants and group membership, editable from either side.
- **Single sign-on** against any OpenID Connect provider, with directory groups driving who gets which
  desktops.
- **Device policy** enforced in the protocol, not just the UI: a modified client cannot turn the
  clipboard, audio, microphone or camera back on.
- **MFA** (authenticator app or e-mail) with an enrolment policy you set.
- **Session recording** with encryption at rest, a tamper-evident hash chain, and retention rules.
- **An audit trail** of who connected to what, when, and from where — plus every administrative action.
- **An operations page** that tells you whether the background workers are alive and lets you test
  SMTP, your identity provider and every Proxmox backend from one screen.

## Quick start

You need Docker, and a Proxmox VE cluster with an API token.

```bash
git clone https://github.com/mkenfenheuer/ksol-zerovdi.git
cd ksol-zerovdi
cp .env.example .env

# Generate the passphrase that protects every stored credential, and put it in .env
openssl rand -base64 48

docker compose up -d
```

Then:

1. Find the generated administrator password — it is printed **once**:
   ```bash
   docker compose logs ksol-zerovdi-app | grep "Bootstrapped initial admin"
   ```
2. Open `http://localhost:8080`, sign in as `admin@example.com`, and change the password.
3. **Admin → Backends** — add your Proxmox cluster (host, API token id and secret).
4. **Admin → Resources** — publish a machine, or **Admin → VDI pools** to clone desktops from a
   template on demand.
5. **Admin → Users** — create an account and grant it the resource. Sign in as that user and connect.

Two things to do before anyone else uses it:

- Put a **TLS-terminating reverse proxy** in front of it and forward the `Upgrade`/`Connection`
  headers — the console is a WebSocket. Then set `App__PublicBaseUrl` and `AllowedHosts` in `.env`.
- **Back up the `zerovdi-data` volume.** It holds the database *and* the encryption keyring. Without
  the keyring, every stored credential and every encrypted recording is gone.

Full guides live in the app itself under **Admin → Docs**, and in
[`KSol.ZeroVDI/wwwroot/docs/admin/`](KSol.ZeroVDI/wwwroot/docs/admin/) —
[installation](KSol.ZeroVDI/wwwroot/docs/admin/getting-started/installation.md),
[configuration keys](KSol.ZeroVDI/wwwroot/docs/admin/reference/configuration-keys.md),
[VDI pools](KSol.ZeroVDI/wwwroot/docs/admin/features/vdi-pools.md),
[identity federation](KSol.ZeroVDI/wwwroot/docs/admin/features/identity-federation.md),
[security](KSol.ZeroVDI/wwwroot/docs/admin/features/security.md).

## Configuration you should not skip

| Setting | Why |
|---|---|
| `DataProtection__MasterKeyPassphrase` | **Required in production.** Encrypts the credential keyring at rest. The app refuses to start without it. Back it up; losing it makes stored credentials unrecoverable. |
| `App__PublicBaseUrl` | The address users type. Password-reset links and the OIDC redirect are built from it, so a forged `Host` header cannot point them somewhere else. |
| `AllowedHosts` | Reject requests carrying any other `Host` header. |
| `ForwardedHeaders__KnownProxies` | Your reverse proxy, so the audit log and rate limiting see real client IPs instead of the proxy's. |
| `Bootstrap__AdminEmail` / `Bootstrap__AdminPassword` | The first administrator. Without a password, one is generated and logged once — there is no default password. |

Everything else is in the
[configuration keys reference](KSol.ZeroVDI/wwwroot/docs/admin/reference/configuration-keys.md).

## What's in the repository

| Path | What it is |
|---|---|
| `KSol.ZeroVDI/` | The gateway: web console, broker, admin UI, and the C# RDP implementation (`RDP/`). |
| `KSol.ZeroVDI/wwwroot/lib/rdpweb/` | The browser RDP client — protocol, codecs, decode worker. |
| `KSol.ZeroVDI/wwwroot/docs/` | The documentation the app serves under **Admin → Docs** and **Docs**. |
| `KSol.ZeroVDI.Connector/` | The connector agent for networks the gateway cannot reach directly. |
| `KSol.ZeroVDI.Desktop/` | The optional Electron shell around the console. |
| `KSol.ZeroVDI.Tests/` | Unit tests for the protocol decoders and the security-critical policies. |
| `Spec/` | Pointers to the Microsoft Open Specifications the protocol code cites. |

Built on **.NET 9** (connector: .NET 10), **ASP.NET Core**, **EF Core / SQLite**, **Tailwind CSS**,
and **Proxmox VE**.

## Development

```bash
dotnet build KSol.ZeroVDI.sln -c Release
dotnet test  KSol.ZeroVDI.sln -c Release
dotnet run   --project KSol.ZeroVDI
```

The Tailwind bundle rebuilds automatically when Node is available; the compiled
`wwwroot/css/app.css` is committed, so a machine without Node still builds (`npm ci` in
`KSol.ZeroVDI/` if you want to work on the styles). Pass `-p:SkipTailwindBuild=true` on a build
agent.

Working on this with an AI coding agent? [`AGENTS.md`](AGENTS.md) explains the layout, the invariants
that are easy to break, and how to validate a change.

## Security

ZeroVDI terminates RDP for a whole organisation, so it is built to be pointed at hostile input:
the container runs unprivileged, the CSP allows no inline script, host certificates are pinned on
first use, device policy is enforced in the protocol, and every dependency is checked for known
vulnerabilities in CI.

Found something? Please report it privately — see [`SECURITY.md`](SECURITY.md).

## License

ZeroVDI is **source-available**, not open source. See [`LICENSE`](LICENSE).

- **Free to deploy and run**, including in commercial and enterprise infrastructure, with no user
  limit and no fee — an organisation running it to give its own users and administrators desktop
  access needs no further permission.
- **Not** free to modify, redistribute, resell, rebrand, or build another product out of — in whole
  or in part.

Need something the licence does not cover (a local change, an integration, redistribution)? Ask:
**maximilian.kenfenheuer@ksol.it**. A contribution merged upstream is usually the better path for
everyone, and pull requests are welcome.

## Support

- Bug or feature request → [open an issue](../../issues).
- Security vulnerability → [`SECURITY.md`](SECURITY.md), not an issue.
- Changes between versions → [`CHANGELOG.md`](CHANGELOG.md).

---

Copyright © 2026 KSol.IT. All rights reserved.
