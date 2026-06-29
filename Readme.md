# KSol.IT ZeroVDI
![GitHub License](https://img.shields.io/github/license/mkenfenheuer/ksol-rdpgw)
 ![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/mkenfenheuer/ksol-rdpgw/docker-publish.yml) ![GitHub last commit (branch)](https://img.shields.io/github/last-commit/mkenfenheuer/ksol-rdpgw/main)

**ksol-rdpgw** is a lightweight ASP.NET application that brings **Remote Desktop Gateway (ZeroVDI)** functionality to your infrastructure. With easy plug-and-play integration, you can securely expose RDP services.

## 🖥️ Subscribe in the Windows Remote Desktop app

Instead of downloading `.rdp` files from the web UI, users can subscribe to a **workspace feed** directly in the Windows **Remote Desktop** app (or the legacy *RemoteApp and Desktop Connections* control panel) and see all their authorized resources listed in the app.

1. Open the Remote Desktop app → **Add** → **Workspaces** (or *Subscribe with URL*).
2. Enter the feed URL: `https://<your-gateway-host>/rdweb/feed/webfeed.aspx`
3. Sign in with your gateway credentials when prompted.

The feed implements the RemoteApp and Desktop Connections web feed ([MS-RDWR](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdwr/)) and is authenticated with HTTP Basic. Only resources the user is authorized for are returned, and each generated `.rdp` is pre-configured to route through this gateway. The signed-in Home page also shows the feed URL with a copy button.

## 🔐 Required security configuration

Before deploying, set these environment variables (shown in Docker `__` form):

| Variable | Required | Purpose |
| --- | --- | --- |
| `DataProtection__MasterKeyPassphrase` | **Yes (Production)** | Strong secret that encrypts the credential keyring **at rest** (AES-256-GCM). All stored VM/IPMI/SSH credentials are sealed with the DataProtection keyring, and the keyring itself is encrypted with this passphrase. **The app refuses to start in Production without it.** Keep it out of source control and back it up — losing it makes stored credentials unrecoverable. In GitLab CI/CD it is supplied as the protected, masked variable **`DATAPROTECTION_MASTERKEYPASSPHRASE`**, which the deploy job exports into the compose env. |
| `Bootstrap__AdminPassword` | Recommended | Password for the initial admin account on first run. If unset, a strong random password is generated and **logged once** at startup — capture it from the logs and change it immediately. There is no longer a hardcoded default password. |
| `Bootstrap__AdminEmail` | Optional | Username/email of the initial admin (default `admin@example.com`). |
| `ForwardedHeaders__KnownProxies` / `ForwardedHeaders__KnownNetworks` | Recommended behind a proxy | Trusted reverse-proxy addresses (IPs or `cidr/prefix`). Only `X-Forwarded-*` headers from these hops are honored, preventing host-header / client-IP spoofing. Defaults to trusting loopback only; set `ForwardedHeaders__TrustAllProxies=true` only if the app is reachable solely through the proxy. |

> ⚠️ The DataProtection keyring (`Data/dp-keys/`) is git-ignored and must **never** be committed — anyone with it can decrypt every stored credential. The app refuses to start if it detects a keyring file tracked in git. If a key was ever committed, treat it as compromised: rotate it and re-enter all stored credentials.

**Everything sensitive is encrypted at rest.** No secret is stored in a plaintext database column: stored VM/IPMI/SSH/Windows credentials, the per-user NTLM `NtHash`, and the Proxmox API token secret are all sealed with the keyring (which is itself encrypted with `DataProtection__MasterKeyPassphrase`). On first start after upgrading, any pre-existing plaintext `NtHash` / API secret is automatically encrypted in place (idempotent). User login passwords are stored only as the standard salted ASP.NET Identity hash.

## 📞 Support & Contribution

- Found a bug? Want to suggest a feature? Open an [issue](https://github.com/mKenfenheuer/ksol-rdpgw/issues).
- Contributions welcome via PRs!
- License: GNU GPL v3
