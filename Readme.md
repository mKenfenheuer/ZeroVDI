# KSol.IT ZeroVDI

![GitHub License](https://img.shields.io/github/license/mkenfenheuer/ksol-rdpgw)
![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/mkenfenheuer/ksol-rdpgw/docker-publish.yml)
![GitHub last commit (branch)](https://img.shields.io/github/last-commit/mkenfenheuer/ksol-rdpgw/main)

**ZeroVDI** is a self-hosted, **fully browser-based virtual-desktop (VDI) platform** built
on ASP.NET Core and Proxmox VE. It publishes Windows and Linux desktops to your users
through a **pure web console — no client, no plugin, no agent, no downloaded file** — while
centrally enforcing access, device policy, and a complete, tamper-evident audit trail.

---

## ✨ What it does

- **Browser-native desktop console.** Connect to any authorized desktop straight from the
  web UI. The remote-desktop stream (including RemoteFX Progressive and H.264) is decoded
  live in the browser over WebSocket — clipboard, audio, HiDPI scaling, and dynamic
  resolution included.
- **VDI desktop pools.** Broker automatically-provisioned desktops cloned from a Proxmox
  template. Supports dedicated and floating assignment, per-user and per-group scoping,
  and lazy on-connect provisioning.
- **Publish individual machines.** Expose a single existing VM or physical host as an RDP
  resource, with credentials injected server-side.
- **Access control.** Authorization is the union of direct grants and group membership,
  managed on both the user and the resource pages.
- **Device & security policy.** Centrally gate clipboard, drive, printer, camera, and
  other redirections; enforce MFA.
- **Session management & recordings.** See who is connected in real time; record sessions
  for compliance review (Admin/Auditor-only).
- **Tamper-evident audit log** of who connected to what, and when.
- **Appearance & branding** of the console for your organization.

Everything runs in the browser — there is no client, plugin, agent, or `.rdp` file for
end users to install.

## 🧩 Components

| Component | Description |
|---|---|
| **KSol.ZeroVDI** | The gateway web application — console, broker, admin UI, API. |
| **KSol.ZeroVDI.Connector** | A lightweight agent deployed near your backends. It enrolls with the gateway and proxies connections into networks the gateway cannot reach directly. |

Built on **.NET 9 / ASP.NET Core**, backed by **SQLite** and **Proxmox VE**.

## 🚀 Deployment

ZeroVDI ships as container images and is deployed with Docker Compose. See
[`docker-compose.yml`](docker-compose.yml) for the reference stack (gateway + connector).

```bash
docker compose up -d
```

Full setup guides live in the in-app documentation under **Admin → Docs**
(installation, configuration, architecture) or in
[`KSol.ZeroVDI/wwwroot/docs/admin/`](KSol.ZeroVDI/wwwroot/docs/admin/).

## 🔐 Required security configuration

Before deploying, set these environment variables (shown in Docker `__` form):

| Variable | Required | Purpose |
| --- | --- | --- |
| `DataProtection__MasterKeyPassphrase` | **Yes (Production)** | Strong secret that encrypts the credential keyring **at rest** (AES-256-GCM). All stored VM/IPMI/SSH credentials are sealed with the DataProtection keyring, and the keyring itself is encrypted with this passphrase. **The app refuses to start in Production without it.** Keep it out of source control and back it up — losing it makes stored credentials unrecoverable. |
| `Bootstrap__AdminPassword` | Recommended | Password for the initial admin account on first run. If unset, a strong random password is generated and **logged once** at startup — capture it from the logs and change it immediately. There is no hardcoded default password. |
| `Bootstrap__AdminEmail` | Optional | Username/email of the initial admin (default `admin@example.com`). |
| `ForwardedHeaders__KnownProxies` / `ForwardedHeaders__KnownNetworks` | Recommended behind a proxy | Trusted reverse-proxy addresses (IPs or `cidr/prefix`). Only `X-Forwarded-*` headers from these hops are honored, preventing host-header / client-IP spoofing. Defaults to trusting loopback only; set `ForwardedHeaders__TrustAllProxies=true` only if the app is reachable solely through the proxy. |

The connector agent authenticates to the gateway with either `ZEROVDI_AUTH_TOKEN`
(stateless) or `ZEROVDI_REGISTRATION_TOKEN` (self-enrolls on first boot and persists to
its mounted volume).

**Everything sensitive is encrypted at rest.** No secret is stored in a plaintext database
column: stored VM/IPMI/SSH/Windows credentials, the per-user NTLM `NtHash`, and the Proxmox
API token secret are all sealed with the keyring (which is itself encrypted with
`DataProtection__MasterKeyPassphrase`). On first start after upgrading, any pre-existing
plaintext `NtHash` / API secret is automatically encrypted in place (idempotent). User login
passwords are stored only as the standard salted ASP.NET Identity hash.

## 📄 License

ZeroVDI is distributed under the **KSol.IT Non-Commercial License** — see [`LICENSE`](LICENSE).

- **Free** for personal, private, and educational **non-commercial, non-enterprise** use.
- **Commercial and enterprise use** (including internal business operations of any legal
  entity), as well as any copying, modification, or redistribution, require a separate
  license from KSol.IT.

For commercial or enterprise licensing, contact **maximilian.kenfenheuer@ksol.it**.

## 📞 Support

- Found a bug? Want to suggest a feature? Open an
  [issue](https://github.com/mKenfenheuer/ksol-rdpgw/issues).

Copyright © 2026 KSol.IT. All rights reserved.
