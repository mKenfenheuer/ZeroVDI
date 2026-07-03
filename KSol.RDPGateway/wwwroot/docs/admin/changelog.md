# Changelog

All notable changes to ZeroVDI are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/). Dates are `YYYY-MM-DD`.

## [Unreleased]

---

## [0.6.8] — 2026-07-03 — Web client clipboard redirection fixes

### Fixed
- **Clipboard sync in the web console works again (both directions).** Four protocol-level bugs in the
  web client compounded to leave the MS-RDPECLIP channel dead — text copied in the session never
  reached the clipboard panel, and "Send to remote" never reached the host:
  - On hosts supporting the skip-channel-join MCS shortcut (modern Windows), the static-channel
    handlers — including the entire clipboard handler — were **never constructed**, so all cliprdr
    traffic was silently dropped. Graphics/audio were unaffected (they ride dynamic channels), which
    is why only the clipboard appeared broken.
  - Client→server clipboard PDUs were sent without the `CHANNEL_FLAG_SHOW_PROTOCOL` flag that
    MS-RDPBCGR requires on channels advertised with `CHANNEL_OPTION_SHOW_PROTOCOL` (cliprdr is; the
    host-side clipboard agent expects the channel header to be visible). PDUs now go out with the
    same flags mstsc uses.
  - Clipboard handshake PDUs (server capabilities, Monitor Ready) arriving while the connection was
    still in the licensing/capabilities/finalization phases were misparsed as share-control PDUs and
    dropped — FreeRDP-based hosts (GNOME Remote Desktop) start their channels this early. Inbound MCS
    data is now routed by source channel id in every post-join state. This also fixes a latent bug
    where a large inbound channel PDU could be eaten by the network-autodetect sniffer (its length
    field was misread as security flags).
  - Outbound channel messages larger than one Virtual Channel chunk (1600 bytes) were sent as a
    single oversized PDU, which hosts reject; they are now split into FIRST..LAST chunks per
    MS-RDPBCGR, so sending long text to the remote session works.
- **Remote→browser text no longer collapses to its first character.** Hosts often announce a single
  copy with two Format List PDUs, so the client issued two Format Data Requests and received two
  responses. The second response found the pending-request state already cleared, was mis-decoded as
  ANSI text (stopping at the first NUL byte of the UTF-16 payload — one character), and overwrote the
  correctly decoded text in the clipboard panel. Responses without an outstanding request are now
  ignored, and decoding always follows the format that was actually requested.

## [0.6.7] — 2026-07-03 — Web client keyboard layout detection

### Fixed
- **Web console sessions no longer force the US English keyboard layout.** The web client's RDP
  handshake hardcoded keyboard layout `0x0409` (US) in both the CS_CORE GCC block and the Input
  capability set, so the host interpreted every scancode through a US layout — QWERTZ/AZERTY users got
  swapped letters and no umlauts/accents regardless of their real keyboard. The client now detects the
  user's layout in the browser and advertises the matching Windows KLID: `navigator.languages` is
  mapped to a layout id (with regional variants like `de-CH`, `fr-BE`, `en-GB`, `pt-BR`), and on
  Chromium the Keyboard API (`navigator.keyboard.getLayoutMap()`) probes what characters a few physical
  keys actually produce to correct the layout family when the browser UI language doesn't match the
  keyboard (e.g. an English-UI browser on a German QWERTZ keyboard). An explicit override is available
  via `window.RDP_KEYBOARD_LAYOUT` (a Windows KLID, e.g. `0x0407`); unknown locales still fall back to
  US English.

---

## [0.6.6] — 2026-07-03 — Web client render-path diagnostics cleanup

### Changed
- **Stripped per-frame diagnostic logging and dead debug scaffolding from the web client's hot
  render/input paths.** A round of stall-chasing instrumentation (since resolved) had left work
  running on every graphics frame, every input event, and every surface paint — regardless of whether
  logging was enabled:
  - `rdpgfx.js`: removed the per-PDU GFX command sequence string-building and the per-blob/per-dispatch
    verbose logs; the per-frame `START_FRAME`/`END_FRAME`/`FRAME_ACK` logs; and a full-desktop
    `getImageData` pixel scan (`_surfSample`) that ran on the H.264 paint path every 60th frame. Also
    removed the dead `/debug/dump/wire1.bin` and `/debug/dump/stream.h264` upload paths (no such
    server endpoints exist) and the `RDP_GFX_NODECODE` frame-drop diagnostic.
  - `protocol.js`: removed the per-input-event pointer-flag/scancode decode block that fed an
    already-commented-out log (pure discarded work on every mouse move and keystroke).
  - `client.js`: removed two `getImageData` single-pixel GPU read-backs on the paint path whose sampled
    values were no longer used, and changed `window.RDP_LOG` to default **off** (set it to `1` in
    devtools to re-enable protocol/GFX breadcrumbs; `console.error`/`console.warn` for real failures
    still fire regardless).
  - `progressive.js`: removed the per-tile luma min/max diagnostic scan and a per-UPGRADE-tile array
    allocation. Decode output is byte-for-byte identical (verified against the prior implementation
    across 1200 synthetic streams / 7498 tiles covering all codec paths).

  Behavior of the render pipeline is unchanged; this only removes diagnostic overhead. Functional flags
  (`RDP_GFX_SUSPEND`, `RDP_AUTOCONNECT`) are preserved.

---

## [0.6.5] — 2026-07-03 — Web console HiDPI + session-start rendering fixes

### Fixed
- **HiDPI sessions now start at the correct scale instead of 100%.** The web client's RDP handshake
  (CS_CORE) never carried the display's DPI, so every session came up at 100% and a later
  Display-Control monitor layout had to win a race to fix it — which it often lost (the GFX-safe
  single-layout path was only taken once the GFX channel object existed, and Display Control
  frequently came up first, kicking off the multi-step dummy-resize sequence whose layouts the host
  coalesced or dropped). The client now sends the optional CS_CORE `desktopScaleFactor` tail on GFX
  connections — GNOME Remote Desktop builds its initial monitor configuration from exactly these
  fields, and Windows applies them as the connect-time session DPI — and the initial-scale logic
  branches on the session-wide GFX mode instead of the racy channel object.
- **No more black screen at session start with the RemoteFX Progressive codec.** GNOME Remote Desktop
  fully tears down and re-negotiates its PipeWire stream on every monitor-layout PDU (even a no-op
  one), deletes and recreates its GFX surface, and then streams nothing until the desktop actually
  changes — and the web client blitted the freshly created *empty* surface to the canvas when the host
  re-mapped it, wiping the last good frame. The result was a black console until the first interaction
  produced damage. The client now never paints a surface that has not received content yet, so the
  last decoded frame stays on screen through the host-side reconfigure.

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
