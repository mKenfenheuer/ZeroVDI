# Changelog

All notable changes to ZeroVDI are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/). Dates are `YYYY-MM-DD`.

## [Unreleased]

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
