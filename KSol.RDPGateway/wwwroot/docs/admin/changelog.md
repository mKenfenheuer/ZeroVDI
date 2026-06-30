# Changelog

All notable changes to ZeroVDI are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/). Dates are `YYYY-MM-DD`.

## [Unreleased]

---

## [0.6.4] — 2026-06-30 — VDI pool SSO sign-in fix

### Fixed
- **Generated desktop credentials now sign you in automatically.** When a VDI pool generates a unique
  per-user account (cloud-init + per-resource SSO), connecting through the pool used to still pop an
  empty login window. The connect path looked up the stored credentials by the *pool* id, but they are
  stored against the user's provisioned *clone* — so the lookup always missed. The relay now resolves
  the pool to the user's clone and reads the SSO credentials (and binds the recording, redirection and
  session rows) against that clone, and the console suppresses the login overlay for credential-
  generating pools so the very first connect signs in without prompting.

### Fixed
- **Deprovisioning a desktop now stops a running VM before destroying it.** Proxmox refuses to delete a
  running VM, so the tear-down could fail mid-way and leave an orphaned clone. Deprovision now checks the
  VM's status, stops it only if it is running, waits for it to actually report `stopped`, and then
  destroys it — a VM that is already gone is treated as success.

---

## [0.6.2] — 2026-06-30 — VDI reprovision fix

### Fixed
- **Dedicated desktops can be reconnected after a failed provision.** When a clone failed to provision,
  its tracking row was left behind in a `Failed` state that connect-time lookups ignored but the unique
  `(pool, owner)` index did not — so every later connect attempt hit a `UNIQUE constraint failed`
  error and the user was permanently stuck. Provisioning now clears a stale `Failed`/`Deprovisioning`
  instance (and its orphaned resource) before creating fresh tracking rows.
- **Vanished desktops are recloned on connect.** If a dedicated clone's VM no longer exists on the
  backend (deleted out-of-band in Proxmox, or its node lost), connecting used to fail trying to start a
  ghost VM. The connect path now verifies the clone still exists and, if not, tears down the stale
  instance and provisions a fresh desktop automatically. A transient backend outage is treated as
  "still exists" so it never triggers a needless reclone.

---

## [0.6.1] — 2026-06-30 — Logo serving fix

### Fixed
- **Uploaded logos now load in production.** Custom branding logos are served by a dedicated
  static-file handler at `/uploads` and stored under the persisted `Data` volume, so they no longer
  404 after deploy (the build-time static-asset pipeline never served runtime uploads) and survive
  container recreation.

---

## [0.6.0] — 2026-06-30 — Branding & documentation

### Added
- **Appearance & branding.** New tenant-wide appearance policy at **Admin → Appearance**
  (`/admin/appearance`): force a color theme (preset or custom), force light/dark mode, and upload a
  custom company logo. See [appearance & branding](features/appearance.md).
- **Custom color themes.** A custom primary color now re-derives a full matching palette (surfaces,
  borders, sidebar, focus ring) from its hue, for both light and dark mode — not just the primary
  button.
- **Dark-mode logo.** Upload a separate logo for dark mode; the light logo is used as the fallback.
  Logos swap instantly when the mode changes, with no page reload.
- **Administrator documentation.** New in-app documentation at **Admin → Documentation**
  (`/admin/docs`) with a navigable contents tree, breadcrumbs, and this changelog. Available to
  Admin and Auditor roles.
- **Styled error pages.** 4xx and 5xx responses now render a branded error page consistent with the
  app shell instead of the framework default.
- **VDI generated credentials.** New per-pool option `GenerateCredentials` that derives a unique
  random username + password per desktop owner and injects them via cloud-init, also storing them as
  the owner's SSO credentials for seamless sign-in. See [VDI pools](features/vdi-pools.md).

### Changed
- Soft line breaks in documentation markdown render as hard breaks for readability.

---

## [0.5.0] — 2026-06-29 — VDI template provisioning

### Added
- **VDI pools**. Clone-from-template desktop pools with per-user/per-group assignment,
  dedicated and floating models, lazy on-connect provisioning, and cloud-init / cloudbase-init
  identity injection. Admin UI at `/admin/vdi-pools`. See [VDI pools](features/vdi-pools.md).
- **User groups & central access control.** `ResourceAccessService` is the single authorization gate
  (effective access = direct ∪ group). Group management at `/admin/groups`. See
  [access control](features/access-control.md).

## [0.4.0] — 2026-06-29 — Enterprise hardening

### Added
- **Audit log**. Structured, append-only audit trail of logins, sessions, credential
  changes, access grants, and admin actions. Viewer at `/admin/audit`. See [audit](features/audit.md).
- **Session management**. Live sessions view with force-disconnect and per-user
  concurrency limits at `/admin/sessions`. See [sessions](features/sessions.md).
- **MFA enforcement**. Required-MFA policy by role with authenticator-app and
  email-code methods. See [security & MFA](features/security.md).
- **Device policy**. Central, admin-enforced clipboard/audio/microphone/camera policy
  at `/admin/device-policy`. See [device policy](features/device-policy.md).
- **Recording lifecycle**. Encryption-at-rest, tamper-evidence (hash chain), and
  retention/auto-purge for session recordings. See [recordings](features/recordings.md).
- **Rate limiting**. IP fixed-window throttling on auth pages and the WS relay, plus
  Identity lockout-on-failure.

## [0.3.0] and earlier

- Browser-based RDP console (no client install), Proxmox backend integration, single-resource
  publishing, SSO credential injection, and session recording with in-browser playback.

[Unreleased]: #unreleased
