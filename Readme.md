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

## 📞 Support & Contribution

- Found a bug? Want to suggest a feature? Open an [issue](https://github.com/mKenfenheuer/ksol-rdpgw/issues).
- Contributions welcome via PRs!
- License: GNU GPL v3
