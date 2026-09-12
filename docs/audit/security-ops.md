# ZeroVDI — Security / Identity / Operations Review

Scope: `/Users/max/Git/ksol-rdpgw` (ASP.NET Core 9 + Identity + SQLite + Proxmox), read-only, code as of commit 68add31 / CHANGELOG 0.6.31. All paths relative to `KSol.ZeroVDI/` unless prefixed.

---

## 1. Authentication & identity

**Model: local ASP.NET Identity only.** `Program.cs:36-43` registers `AddDefaultIdentity<ApplicationUser>` with roles and default token providers. There is no OIDC/SAML/LDAP/AD federation anywhere. History: commit `8eeb4bf` ("More auth + OIDC", 2026-06-13) added `20260613121908_AddOpenIddict` + an `AuthorizationController`/`CertificateProvider` — this was ZeroVDI acting as an **OIDC provider** for the Windows App / RemoteApp-feed workspace, not user federation — and `20260628131929_RemoveOpenIddict` removed it when the native RDGW/feed path was dropped (no `RDG_OUT`/`remoteDesktopGateway` code remains). The Dockerfile comment at `../Dockerfile:29` about "OAuth/OIDC signing certificates" is stale.

**Password policy / lockout:** no `Configure<IdentityOptions>` anywhere → framework defaults: 6 chars + upper/lower/digit/symbol, lockout 5 failures / 5 min, `AllowedForNewUsers=true`. Login uses `lockoutOnFailure: true` (`Login.cshtml.cs:118`). No breached-password check, history, expiry, or max length.

**Registration:** closed — `Register.cshtml.cs:13-21` redirects GET/POST to Login. Users are admin-created with `EmailConfirmed=true` (`UsersController.cs:132`) so `RequireConfirmedAccount=true` (`Program.cs:36`) is satisfied. Bootstrap admin: `Program.cs:313-343` — default e-mail `admin@example.com`, random 20-char password **written to the log** if `Bootstrap:AdminPassword` unset (`:328-332`). **Foot-gun:** `Program.cs:345-354` re-grants Admin to whatever account matches `Bootstrap:AdminEmail` on every boot, silently.

**2FA:** TOTP authenticator and email OTP (`ApplicationUser.cs:29 EmailTwoFactorEnabled`; `LoginWith2fa.cshtml.cs:120-128`). Email/reset/confirm tokens capped at 5 min (`Program.cs:47-48`) — note this also shortens **password-reset and email-change links** to 5 minutes. `RememberMachine` is offered (`LoginWith2fa.cshtml.cs:78,123,128`) with the 14-day default. Failed 2FA counts toward lockout (`:138-146`). Recovery-code login is **not audited** (`LoginWithRecoveryCode.cshtml.cs:100-108`).

**MFA enforcement:** `MfaPolicy.cs:23-35` — default `RequiredRoles=[Admin]`, `RequireForAll` optional. `MfaEnforcementMiddleware.cs:33-56` redirects required-but-unenrolled users to EnableAuthenticator. Exemptions (`:63-89`): the entire `/Identity/Account/Manage/**` tree (so ChangePassword, Email, DeletePersonalData, Disable2fa are reachable un-gated), `/Identity/Account/Login*`, static paths, and **`/ws/**`**. `/ws` exemption is not a bypass by itself: `/ws/rdp/{id}` still needs the auth cookie and the resource grant, but an un-enrolled admin *can* open console sessions without ever enrolling. `/connect/*` and `/admin/*` are correctly gated. Role evaluation is from cookie claims (`MfaPolicy.cs:41-46`), so newly-promoted admins are not gated until the 30-min security-stamp refresh.

**Cookie/session:** no `ConfigureApplicationCookie` → 14-day **sliding** cookie, `HttpOnly`, `SameSite=Lax`, `SecurePolicy=SameAsRequest`, security-stamp validation every 30 min. No absolute lifetime, no idle timeout, no per-user concurrent *login* limit (only console tunnels are capped via `Sessions:MaxConcurrentPerUser`, default 0 = unlimited, `appsettings.json:17-19`). Role changes and deletes do **not** call `UpdateSecurityStampAsync` (`UsersController.cs:255-294`, `:228-246`) → a demoted/deleted admin keeps a working cookie for up to 30 minutes. `app.UseAuthentication()` is never called explicitly (`Program.cs:396` only has `UseAuthorization`); WebApplication auto-inserts it before the user pipeline, which works but places it *before* `UseForwardedHeaders` (`:267`) — harmless today, fragile.

**Anti-forgery on WS/API:** no global `AutoValidateAntiforgeryToken`; `ConnectController` POSTs (`:60 begin`, `:89 save`, `:140 clear-credentials`) have no `[ValidateAntiForgeryToken]`; `UseWebSockets()` at `Program.cs:390` has no `AllowedOrigins` and `RdpWebSocketController.cs:72-78` never checks `Origin`. Both rest entirely on the browser's SameSite=Lax default (Lax cookies are not sent on cross-site POST/WS in current browsers). Defense-in-depth gap, not an active hole.

**Open redirect:** `LocalRedirect(returnUrl)` used (`Login.cshtml.cs:124`, `LoginWith2fa.cshtml.cs:136`) — fine.

## 2. Authorization

Roles `Admin`, `User`, `Auditor` seeded at `Program.cs:279-286`. Every controller carries `[Authorize]`: Admin-only for resources/users/groups/backends/pools/connectors/policies/appearance/recording-rules; `Admin,Auditor` for audit, recordings, sessions (view), docs; `SessionsController.cs:32-34` disconnect is Admin-only; `ConnectController`/`HomeController`/`RdpWebSocketController`/`UserDocsController` are any-authenticated. `ErrorController` is intentionally anonymous. No missing `[Authorize]` found. Documented role matrix (`wwwroot/docs/admin/reference/roles.md`) matches code.

**ResourceAccessService is the single gate** (`RDP/ResourceAccessService.cs:39-112`) and is used at every connect path: dashboard, `HomeController.cs:125`, `ConnectController.cs:51-57` (begin/status) and `:101` (save), `RdpWebSocketController.cs:92`. Pool entry points are re-bound to the clone id (`RdpWebSocketController.cs:120`). IDOR checks: recordings are Admin/Auditor-global by design (`RecordingsController.cs:41-45`); quality WS verifies `tracked.UserId == userId` (`RdpWebSocketController.cs:325-330`); per-user credential rows are always filtered by `UserId` (`ConnectController.cs:95-96, 146-147`). Recordings are never user-visible (route prefix `admin/recordings`, `:19-21`).

**DesktopAdminBlockMiddleware** (`RDP/DesktopAdminBlockMiddleware.cs:17-22`) 404s `/Admin*` when `DesktopClient.IsElectron` matches the substring `"Electron/"` in User-Agent (`RDP/DesktopClient.cs:11-12`). It is a UX nicety, trivially bypassed by changing the UA; correctness is fine but it must not be presented as a control.

**Admin foot-guns:** no last-admin guard, no self-demotion guard (`UsersController.cs:264-277`), no self-delete guard (`:228-246`); a user can also self-delete via `Manage/DeletePersonalData` (`DeletePersonalData.cshtml.cs:19`) — including the only admin.

## 3. Secrets & crypto

- **Keyring:** DataProtection keys on disk (`Program.cs:60-62`, `Data/dp-keys`), sealed by `KeyringEncryptor` — PBKDF2-HMAC-SHA256 200k iterations, 16-byte random salt, AES-256-GCM 12-byte random nonce per blob (`RDP/KeyringEncryptor.cs:23-27, 81-102`). Production refuses to boot without `DataProtection:MasterKeyPassphrase` (`Program.cs:70-79`); Dev uses a constant (`KeyringEncryptor.cs:38`). Git-tracked-keyring guard `Program.cs:437-480`. No rotation story for the passphrase (re-encrypting all blobs) — DP key rollover (90-day default) is automatic but every key is sealed under the same passphrase.
- **CredentialProtector** (`RDP/CredentialProtector.cs:17`) = one DP purpose string for all stored VM/IPMI/Windows/SSH/cloud-init secrets; decrypt failures swallowed to null (`:31-32`). Column-level encryption via `EncryptedStringConverter` for `NtHash`, `ProxmoxBackend.ApiTokenSecret`, `Connector.RegistrationToken` (`Data/ApplicationDbContext.cs:146-149`); startup migrator encrypts legacy plaintext (`Program.cs:286-301`).
- **NtHash — dead-code liability.** `DerivingPasswordHasher.cs:22` computes MD4(UTF-16LE(password)) (`RDP/AuthCrypto.cs:17-21`) on every password set and stores it (`Models/ApplicationUser.cs:19`). Its only consumers, `MitmRdpStream`/`CredSspServer` (`RDP/MitmRdpStream.cs:38,173`, `RDP/CredSspServer.cs:99`), are **never instantiated** (`grep "MitmRdpStream("` → zero call sites). So the product stores an unsalted, fast-hash, pass-the-hash-equivalent secret for every user with no remaining purpose. Keyring compromise = every user's crackable NT hash.
- **Proxmox:** token secret encrypted; `ProxmoxBackend.VerifyTls` defaults to **false** (`Models/ProxmoxBackend.cs:46`) and `ProxmoxClient.cs:33-36` then accepts any cert.
- **Connector tokens:** 256-bit random, SHA-256 hash stored, constant-time compare (`RDP/ConnectorTokens.cs:14-32`); registration token single-use (`RDP/ConnectorAgentEndpoints.cs:37-41`), revoke = `Enabled=false` or Regenerate (`ConnectorsController.cs:87-102`). No expiry on registration tokens, no audit of connector lifecycle. Token also sent in `?token=` query (`Connector/Agent.cs:73-76`, accepted at `ConnectorAgentEndpoints.cs:101-102`) → lands in proxy/access logs. Agent persists token as plaintext `config.json` with default perms (`Connector/AgentConfig.cs:34`).
- **Recordings:** AES-256-CTR, key = PBKDF2(master passphrase, **static salt**, 200k) (`RDP/RecordingCryptor.cs:24-34`), random 16-byte nonce per file (`:43`); confidentiality only (documented). Integrity: per-track SHA-256 + chain `H(prev|id|hashes)` anchored at literal `"GENESIS"` (`RDP/RecordingIntegrity.cs:31-42`); prev link chosen by `StartedUtc` (`:55-59`), stored in the same SQLite DB. **`Verify()` never checks the chain** (`:69-81`, the comment at `:78-79` admits it) and retention purges break it by design (`RDP/RecordingRetentionService.cs:17`). No external anchor, no signature, so a DB-write-capable admin can rewrite hashes undetectably.
- **Audit chain:** none. `AuditEvent` is append-only "by convention" (`Models/AuditEvent.cs:20-23`); no hash, no signature; anyone with the SQLite file deletes rows.
- **Secrets in logs:** no password/token values logged (grep clean). Bootstrap password is logged once (`Program.cs:328-332`). `RDPGW_DUMP_DIR` (`RDP/RdpRelaySession.cs:250-257`, "Remove after debugging") tees the **decrypted host→browser RDP stream to disk** in the production binary; `RdpStreamRecorder.cs:41` similarly enables `RdpLogSink` which decodes input PDUs (keystrokes). Prod log level `KSol.ZeroVDI=Debug` (`appsettings.json:47`). `appsettings.json:52-57` ships an internal SMTP IP (`10.1.250.249`) and `noreply@ksol.it`.
- SMTP is plaintext when `UseSsl=false` — `SecureSocketOptions.None`, not opportunistic STARTTLS (`RDP/SmtpEmailSender.cs:42`); shipped config sets `UseSsl=false` (`appsettings.json:58`).

## 4. Transport & headers

- No in-app TLS: Kestrel HTTP only (`../Dockerfile:6,28`; compose `ASPNETCORE_HTTP_PORTS=8084` + `network_mode: host`, `../docker-compose.yml:9,22`). HTTP/1.1 forced (`Program.cs:24-28`) for an NTLM feed that no longer exists. HSTS 30-day default outside Dev (`Program.cs:368`), `UseHttpsRedirection` (`:374`).
- Forwarded headers: correct-by-default — only loopback trusted unless `ForwardedHeaders:KnownProxies/KnownNetworks/TrustAllProxies` set (`Program.cs:216-263`). **These keys are undocumented** (absent from `reference/configuration-keys.md`); `installation.md:41-42` only says "forward X-Forwarded-For".
- `AllowedHosts: "*"` (`appsettings.json:63`) and reset/confirm links are built from `Request.Scheme`+`Host` (`ForgotPassword.cshtml.cs:68-72`, `Manage/Email.cshtml.cs:121-125`) → **password-reset host-header poisoning**: an attacker POSTs ForgotPassword for a victim with `Host: attacker.tld`; the victim receives a valid reset token pointing at the attacker.
- **Zero security headers**: no CSP, `X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy` (grep across `.cs/.cshtml/.json` → 0). The console (`Views/Home/Console.cshtml`) drives keyboard/mouse into a VM and is clickjackable; camera/mic use would benefit from Permissions-Policy.
- Inline scripts everywhere (`_ThemeHead.cshtml:24-45`, `Console.cshtml:463,495,504`); external CSS from `cdn.jsdelivr.net` and `fonts.googleapis.com` **without SRI** (`_ThemeHead.cshtml:20-21`) — supply-chain + privacy leak (font fetch reveals the gateway to Google). Admin-controlled raw CSS injection `@Html.Raw(customCss)` (`:47`) and SVG logo upload (`AppearanceController.cs:20`) served same-origin from `/uploads` (`Program.cs:378-386`) with no `Content-Disposition`/CSP → stored-XSS vector for a malicious admin.

## 5. Rate limiting & abuse

- Two IP-keyed fixed-window policies (`Program.cs:150-179`): `auth` on **all** Razor pages (`:404-406`), `ws` on `/ws/rdp` and `/ws/rdp-quality` (`RdpWebSocketController.cs:73,315`). Shipped values 30/60s and 45/60s (`appsettings.json:21-26`). Keyed on `RemoteIpAddress` → if the reverse proxy is not in `KnownProxies`, **every client shares one bucket**: 30 requests/min locks out the whole login page (trivial DoS). Docs are wrong: `configuration-keys.md:24-31` and `rate-limiting.md:17-22` document `RateLimiting:Auth:PermitLimit` (nested) with defaults 10/30; code reads `RateLimiting:AuthPermitLimit` (`Program.cs:157-158,169-170`).
- Email-OTP resend (`LoginWith2fa.cshtml.cs:154-166`) and ForgotPassword are only throttled by the shared IP bucket — mail-bombing a user is cheap.
- `/agent/*` endpoints have no rate limit (`Program.cs:415`); `/agent/register` is unauthenticated and decrypts every pending registration token per request (`ConnectorAgentEndpoints.cs:29-33`).
- Session cap is evaluated **after** `ResolveAsync` (which starts/clones VMs) and after socket accept (`RdpWebSocketController.cs:101, 152-163`) → a capped user can still burn Proxmox resources. Default cap 0.
- WS buffers: 8 KB/16 KB receive buffers (`RDP/RdpRelaySession.cs:230,292,361`), credentials frame accumulated in an unbounded `MemoryStream` (`:231-238`); no idle timeout on relay sessions; no per-connection byte budget; Kestrel limits untouched.
- No user enumeration on ForgotPassword (`:57-61`) or login (`Login.cshtml.cs:141`).

## 6. RDP-side security

- **Host certificate: accepted unconditionally** (`RDP/RdpHostConnection.cs:124-131`, callback returns `true`). No TOFU, no per-resource pin, no admin toggle. The gateway holds plaintext VM creds and runs CredSSP as client to whoever answers, so an on-path attacker gets a crackable NTLMv2 response and can DoS; the CredSSP pubkey binding only protects the *relay* case. TLS 1.2/1.3 enforced (`:137-139`). NLA is always performed (CredSSP client, `RdpRelaySession.cs:13`); there is no "require NLA" per resource because non-NLA is simply not supported.
- **Credentials never reach the browser** — confirmed: `HomeController.cs:154-173` surfaces only a boolean, `Console.cshtml:12` uses the placeholder `"sso"`, relay injects server-side (`RdpWebSocketController.cs:130-137`). Redirect one-time creds cached in memory 30 s (`RDP/RedirectionTokenCache.cs:26-41`).
- **Device policy is UI/persistence-only, not protocol-enforced.** `DevicePolicyService.Apply` is called in `ConnectController.cs:122-131` and `HomeController.cs:137-138`, but **nothing in `RdpWebSocketController`, `RdpRelaySession`, `RdpHostConnection` or `RdpChannels` references it** (grep 0). The comments "The relay also clamps at connect" (`HomeController.cs:133`) and "The relay clamps too, so this is purely UX" (`Console.cshtml:23`) are false. A user editing `client.js`/WS frames can join cliprdr/rdpsnd/audin/rdpecam regardless of a `Disabled` policy.
- Recording notice: banner when the matched rule has `NotifyUser` (`Models/RecordingRule.cs:51`, `Console.cshtml:20`); admins can record silently; no consent capture, no audit that notice was displayed.

## 7. Audit & compliance

Fields (`Models/AuditEvent.cs:27-59`): UTC timestamp, category, action, actorId/name, targetType/id/name, IP, success, JSON detail. **Missing:** user-agent, session/correlation id, integrity hash. Writer swallows failures to a warning (`RDP/AuditLogger.cs:70-74`) — silent audit loss.

**Logged** (44 call sites): Login succeeded/failed/locked-out, 2FA succeeded/failed, MFA enabled/disabled/reset/email-enabled/email-disabled/enrollment-required, Session connected/disconnected(+forced, duration)/rejected-limit/force-disconnected, Credentials stored/cleared (user path), User created/deleted, Roles changed, Access/GroupAccess granted/revoked, Group CRUD + membership, VdiPool CRUD/assignment/instance-deprovisioned, DevicePolicy/Appearance updated, Recording verified/deleted/purged.

**Not logged:** logout (`Logout.cshtml.cs:28`), recovery-code login, password change (`Manage/ChangePassword.cshtml.cs:97-120`), password-reset request/completion, email change, user edit (`UsersController.cs:163-190`), **admin storing credentials on behalf of a user** (`RDPResourcesController.cs:400-420`), resource create/edit/delete (`RDPResourcesController.cs:103,200`, only group-access events at `:367,390`), backend CRUD (`ProxmoxBackendsController` — 0 calls), connector CRUD/regenerate (0), recording-rule CRUD (0), recording **playback/download** (`RecordingsController.cs:72-103`; docs claim "viewed" is a category), session-limit config changes, VM power actions from the resource page.

Retention: none (`audit.md:29-31` defers to "database-level retention"); export: none — viewer only (`AuditController.cs:28-62`), no CSV/JSON/syslog/webhook/SIEM. Time zone: stored UTC; display conversion not verified.

## 8. Operations

- **Config surface:** documented keys (`reference/configuration-keys.md`) cover DataProtection/Mfa/Sessions/RateLimiting/Recording only, with wrong RateLimiting names/defaults. Undocumented: `ForwardedHeaders:*`, `Bootstrap:AdminEmail/AdminPassword`, `DataProtection:KeysDir`, `Recording:Directory/FfmpegPath/DefaultEnabled/RetentionSweepHours`, `Smtp:*`, `ConnectionStrings`, `RDPGW_DUMP_DIR`. Stale `IdentityDataContextConnection` localdb string (`appsettings.json:4`).
- **Health/metrics/logging:** no `/health`, no `AddHealthChecks`, no OpenTelemetry/Prometheus, no structured sink (console only). Compose has no `healthcheck`, only `restart: always`.
- **SQLite:** `Cache=Shared` (`appsettings.json:3`, discouraged for EF), no WAL/`PRAGMA` tuning (only FK toggles in a migration), unconditional `Migrate()` at boot (`Program.cs:275-279`), no backup/restore procedure anywhere (Readme has no upgrade/backup text). Volume `/mnt/data/rdpgw` holds DB + keyring + recordings together.
- **HA/scale-out blockers:** in-memory `SessionTracker` (`RDP/SessionTracker.cs:34-35`, self-documented), `RedirectionTokenCache` (`:27`), `ConnectionReadinessService`, `ConnectorHub`, rate-limiter partitions, `DevicePolicyService`/`AppearanceService` caches, local DP keyring, SQLite file, migrate-on-start races. Single instance only.
- **Shutdown/upgrade:** no `HostOptions.ShutdownTimeout`, no drain mode; deploy pulls `:latest` and `compose up -d` (`../.gitlab-ci.yml:113-130`) → every deploy kills all live consoles with no warning and no pinned rollback tag. Browser auto-reconnect exists but not a server-side drain.
- **Docker:** floating `aspnet:9.0` base (`../Dockerfile:3`), runs as **root** (no `USER`; compose `APP_UID` is just an env var), no `HEALTHCHECK`, `EXPOSE 80`, ships `ffmpeg mkvtoolnix ipmitool samba-common-bin` (`:25`). `network_mode: host` (`../docker-compose.yml:22`, for WOL broadcast) removes all network isolation. Connector image builds on `sdk:10.0`/`runtime:10.0` floating tags, root, no healthcheck (`../KSol.ZeroVDI.Connector/Dockerfile:17-28`).
- **CI:** **no tests exist** (repo-wide search for `*test*` → none) and no test stage. No SAST, dependency audit, or image scan in `.gitlab-ci.yml` or `.github/workflows/docker-publish.yml`. GitHub workflow pins action SHAs and cosign-signs images (good). GitLab uses `docker login -p` on the CLI (`:31,51,112`); stale Postgres/ES256 comments (`:95-98,142`). Desktop installers are unsigned and published to a mutable `latest` package then vendored into the web image (`:27-30,78-79,93`).
- **Version display:** none in `_AdminLayout`/`_UserLayout`; csproj has no `<Version>`; CHANGELOG says 0.6.31; desktop `package.json` 0.1.0. Operators cannot see what is deployed.
- Razor runtime compilation and `CodeGeneration.Design` shipped in production (`KSol.ZeroVDI.csproj:24,28`).

## 9. Desktop (Electron) wrapper

`KSol.ZeroVDI.Desktop/src/main.js`: main window `contextIsolation:true, sandbox:true`, no preload (`:66-77`) — good. Settings window `contextIsolation:true` with an IPC preload (`:52-58`, `settings-preload.js:1-6`), loads a local file — fine. Remote content: `loadURL(serverUrl)` (`:82`); `normalizeServerUrl` allows `http://` (`:41-42`). `setWindowOpenHandler` calls `shell.openExternal(url)` for **any** non-server URL (`:85-90`) — a compromised gateway page can launch arbitrary URL schemes on the client. No `will-navigate` guard, no `certificate-error` handler (default = reject, good). No auto-updater; no code-signing/notarization config in `package.json` build section; Electron `^33.4.11` is past EOL (2026-09). Cookies persisted in `persist:zerovdi` (`:74`).

## 10. Concrete vulnerabilities / bugs

| # | Sev | Finding | Location |
|---|---|---|---|
| 1 | **High** | Password-reset / email-change link host-header poisoning (`AllowedHosts:*` + `Request.Host` in callback URL) | `appsettings.json:63`; `ForgotPassword.cshtml.cs:68-72`; `Manage/Email.cshtml.cs:121-125` |
| 2 | **High** | Device policy (clipboard/audio/mic/camera) not enforced in the relay; comments claim it is | `RdpWebSocketController.cs` / `RDP/RdpRelaySession.cs` (0 refs); `HomeController.cs:133`; `Console.cshtml:23` |
| 3 | **High** | No security headers (CSP/XFO/nosniff/Referrer/Permissions) on a remote-input console; CDN CSS without SRI | `Program.cs` (none); `_ThemeHead.cshtml:20-21` |
| 4 | **High** | NT hash (MD4) of every user password stored for a code path that no longer exists | `RDP/DerivingPasswordHasher.cs:22`; `Models/ApplicationUser.cs:19`; `MitmRdpStream` uninstantiated |
| 5 | **High** | RDP host TLS cert accepted unconditionally (no TOFU/pin); Proxmox `VerifyTls` defaults false | `RDP/RdpHostConnection.cs:124-131`; `Models/ProxmoxBackend.cs:46` |
| 6 | **High (ops)** | IP rate limiter shares one bucket for all users when proxy isn't in `KnownProxies`; key names in docs wrong | `Program.cs:157,169,216-263`; `configuration-keys.md:24-31` |
| 7 | Medium | Role removal / user delete don't rotate security stamp → up to 30 min residual access | `UsersController.cs:255-294, 228-246` |
| 8 | Medium | No last-admin / self-demote / self-delete guards; bootstrap re-grants Admin on every boot | `UsersController.cs:264-277`; `DeletePersonalData.cshtml.cs:19`; `Program.cs:345-354` |
| 9 | Medium | Audit gaps: admin-set credentials, resource/backend/connector/rule CRUD, playback, logout, password/email changes unlogged; write failures swallowed | §7; `RDPResourcesController.cs:400-420`; `AuditLogger.cs:70-74` |
| 10 | Medium | Recording chain never verified; anchored in same DB; purge breaks chain; static-salt recording key | `RDP/RecordingIntegrity.cs:55-81`; `RDP/RecordingCryptor.cs:25` |
| 11 | Medium | Connector auth token in query string; plaintext `config.json`; agent tunnels to any host:port | `Connector/Agent.cs:73-76,150,173`; `AgentConfig.cs:34`; `ConnectorAgentEndpoints.cs:101-102` |
| 12 | Medium | Debug hooks in prod binary dump decrypted RDP stream / keystrokes when env vars set | `RDP/RdpRelaySession.cs:250-257`; `RDP/RdpStreamRecorder.cs:41` |
| 13 | Medium | Session cap checked after VM start; `/agent/*` unthrottled; unbounded credentials-frame buffer | `RdpWebSocketController.cs:101,152`; `Program.cs:415`; `RdpRelaySession.cs:231` |
| 14 | Medium | Container root, host network, floating base, no healthcheck, `:latest` deploys, no tests/scans | `../Dockerfile:3,25`; `../docker-compose.yml:22`; `../.gitlab-ci.yml:113` |
| 15 | Low | Missing CSRF tokens on `ConnectController` POSTs; no WS Origin check (SameSite-only) | `ConnectController.cs:60,89,140`; `Program.cs:390` |
| 16 | Low | 5-min DP token lifespan also applies to reset/confirm links | `Program.cs:47-48` |
| 17 | Low | Electron `openExternal` on any URL; `http://` gateway allowed; unsigned, EOL Electron, no updater | `Desktop/src/main.js:41,85-90`; `package.json` |
| 18 | Low | SVG upload + raw CSS injection served same-origin (admin-only stored XSS) | `AppearanceController.cs:20`; `_ThemeHead.cshtml:47` |
| 19 | Low | Plaintext SMTP default; internal SMTP IP committed; Debug log level in prod config | `SmtpEmailSender.cs:42`; `appsettings.json:47,52-58` |
| 20 | Low | `UseAuthentication` implicit; HTTP/1.1 forced for removed feature; stale OIDC/Postgres comments | `Program.cs:24-28,396`; `../Dockerfile:29`; `.gitlab-ci.yml:95-98` |

## 11. Prioritized gap list vs. Citrix / Horizon / Guacamole / Kasm / Teleport

**Critical**
- Enforce device policy in the relay (refuse channel joins / strip capabilities server-side) — Citrix/Horizon/Kasm all enforce redirection policy at the protocol layer.
- Fix host-header poisoning: set `AllowedHosts` or a `PublicBaseUrl` and build e-mail links from it.
- Add a security-header middleware (CSP with nonces, `frame-ancestors 'none'`, nosniff, Referrer-Policy, Permissions-Policy scoped to the console) and vendor the CDN CSS/fonts or add SRI.

**High**
- Drop `NtHash` derivation/column and the dead MITM/CredSSP-server code; migrate the column away.
- RDP host identity: TOFU with per-resource fingerprint storage + admin "reset trust", optional strict mode; default Proxmox `VerifyTls=true`.
- Identity federation (OIDC/SAML/LDAP) and SCIM/group sync — every competitor has it; today only local accounts.
- Security-stamp rotation on role change/delete; last-admin and self-action guards; remove per-boot Admin re-grant.
- Rate limiter: document/require `KnownProxies`, warn at startup when all traffic appears to come from one IP; fix docs key names; throttle `/agent/*` and OTP resend separately.
- Audit completeness (§7 list) + tamper-evident chain + export (CSV/JSON, syslog/webhook) + retention — Teleport/Citrix ship SIEM export; ZeroVDI has a viewer only.
- Session lifetime controls: absolute + idle timeouts, configurable cookie lifetime, admin "sign out everywhere", concurrent-login cap (Citrix/Horizon/Teleport all expose these).
- Container hardening: non-root `USER`, pinned digests, `HEALTHCHECK`, drop `samba-common-bin`/`ipmitool` into a sidecar or make optional, reconsider `network_mode: host` (use a WOL sidecar/macvlan).

**Medium**
- Recording integrity: verify the chain in `Verify()`, order deterministically (completion sequence, not `StartedUtc`), export an external anchor (e.g. daily signed digest), per-recording keys wrapped by the master key to allow rotation.
- Session cap before VM start; bounded credentials frame; relay idle timeout; WS Origin allow-list; global `AutoValidateAntiforgeryToken`.
- Ops: `/health` (liveness + DB + keyring), OpenTelemetry metrics/traces, structured JSON logging with a shipper, SQLite WAL + documented online backup (`VACUUM INTO`), version in UI/`/health`, drain mode + `ShutdownTimeout`, pinned image tags with documented rollback, migration dry-run/backup step.
- Connector: header-only auth (document proxy requirements), token file mode 0600, optional target allow-list on the agent, connector lifecycle audit, registration-token expiry.
- Remove `RDPGW_DUMP_DIR`/PDU-dump hooks from production builds (or gate behind a compile symbol).
- Password policy: raise minimum length, add breached-password check, expose lockout settings in config/docs.
- Recording consent: explicit acknowledgement + audit that the notice was shown; "view/download" audit events.

**Low**
- Electron: restrict `openExternal` to http(s), add `will-navigate` guard, enforce https, sign/notarize, add auto-update with signed manifests, bump Electron.
- CSRF tokens on JSON POSTs; separate DP token lifespans (5 min OTP, 1 h reset).
- Docs: add all undocumented keys, correct rate-limit section, add upgrade/backup/restore runbook, note that DesktopAdminBlock is cosmetic.
- Remove runtime Razor compilation and `CodeGeneration.Design` from the production build; clean stale comments/config (`IdentityDataContextConnection`, OIDC certs, Postgres).
- Add a test project (auth gates, ResourceAccessService, MFA middleware, keyring round-trip) and SAST/dependency/image scanning to CI.
