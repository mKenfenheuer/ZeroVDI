# Changelog

All notable changes to ZeroVDI are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/). Dates are `YYYY-MM-DD`.

## [Unreleased]

### Added
- **VNC bridge — RDP server front-end (M1).** The VNC resolver now stands up a minimal in-gateway RDP
  *server* over an in-memory pipe: it answers the browser client's connection sequence (MCS
  Connect-Response → Attach-User/Channel-Join confirms → licensing → Demand-Active → finalization) and,
  on reaching the active state, paints a solid-color frame via a legacy fastpath **bitmap** update
  (16bpp RGB565). This proves the from-scratch server PDU encoders against the real client before any
  RFB/VNC pixels are wired in (that is M2). VNC resources still can't reach a real host yet.

---

## [0.6.18] — 2026-07-09 — Pluggable host-protocol resolvers (RDP seam + VNC groundwork)

### Added
- **Resources now carry a `Protocol` (RDP or VNC).** A new per-resource protocol selects how the
  gateway reaches the host. Native **RDP** (NLA/CredSSP) is the default and unchanged; **VNC** is
  introduced as an alternative that will bridge an RFB/VNC host into an RDP stream (bridge itself lands
  in following releases — selecting VNC currently fails the connection with a clear message). Set it in
  the resource Create/Edit screens; the connection port defaults to 3389 for RDP and 5900 for VNC.

### Changed
- **Introduced an `IRdpResolver` seam at the single point that produces the decrypted RDP stream.**
  The browser console relay and the session recorder already treat everything after connect as an
  opaque RDP byte stream; the logic that *produces* that stream (TCP + X.224 + TLS + CredSSP) now sits
  behind a resolver interface (`NlaRdpResolver`), chosen per resource by `Protocol`. This makes the VDI
  system extensible with additional host protocols without touching the web client or recorder. The
  native RDP path is byte-for-byte identical; this release is the extensibility groundwork only.

## [0.6.17] — 2026-07-09 — H.264 decode moved off the browser main thread

### Changed
- **H.264 (AVC420/AVC444) now decodes in the Web Worker, not on the main thread.** The WebCodecs
  `VideoDecoder`, the bitstream parsing (SPS scan, Annex-B NAL split), and the per-frame output
  handling previously all ran on the browser main thread, so heavy video contended with input
  dispatch — fast-moving desktop content could visibly stall keyboard/mouse handling. The decoder and
  all bitstream work now live in `decode-worker.js` (WebCodecs is available in Worker scope), joining
  RemoteFX Progressive which was already offloaded. Each finished frame is transferred back as a
  `VideoFrame` (zero-copy — `VideoFrame` is Transferable), leaving the main thread only a single
  `drawImage` per frame. Mixed-codec compositing (ClearCodec, progressive, surface-to-surface copies)
  stays on the main thread and is unchanged. If a locked-down environment can't start the worker or
  lacks WebCodecs-in-worker, decode transparently falls back to the previous main-thread path.
- **AVC420 submission now participates in the ordered-decode barrier.** Like progressive, an AVC
  `WIRE_TO_SURFACE_1` now advances the decode sequence so a following order-sensitive op
  (`SURFACE_TO_SURFACE`, `SOLIDFILL`, …) releases only once the PDU has been consumed by the decoder,
  closing a latent ordering gap that existed while AVC decoded synchronously but emitted frames async.

### Faster
- **Frame paint no longer allocates an intermediate `ImageBitmap` on capable browsers.** The decoded
  `VideoFrame` is drawn straight onto the surface's 2D context (a valid image source on every current
  engine), removing a per-frame allocation and an extra async event-loop turn. The old
  `createImageBitmap` path is kept only as a fallback for older Safari/iOS that reject a `VideoFrame`
  source.

---

## [0.6.16] — 2026-07-08 — End-to-end connection quality indicator, sampled off the main thread

### Changed
- **The console's connection-quality indicator now measures the whole path: browser → gateway → RDP
  host.** The relay periodically samples its gateway→host round-trip time over the session's actual
  transport path (a timed TCP connect — directly, or through the connector tunnel including the
  gateway→connector hop) and reports it alongside each quality pong; the browser adds its own
  browser↔gateway RTT and shows the end-to-end total. Hovering the signal bars breaks the latency
  down per leg ("You ↔ gateway" / "Gateway ↔ desktop"), so a slow session is attributable to the
  user's link or the datacenter path at a glance. (RDP itself offers no client-initiated in-band
  probe — MS-RDPBCGR auto-detect is strictly server-initiated — hence the transport-level sampling.)
- **Quality sampling moved off the browser main thread.** RTT probing now runs in a dedicated Web
  Worker (`quality-worker.js`) with its own WebSocket to the new authenticated, session-scoped
  `/ws/rdp-quality/{sessionId}` endpoint (the session id is handed to the browser in the relay's
  "ready" frame). Ping scheduling and pong timestamping happen on the worker thread, so heavy
  main-thread work (progressive decode, large paints) no longer inflates the RTT reading or starves
  the probe interval. The session socket still answers legacy pings as a fallback.
- **Throughput is now an active speed test, not passive traffic counting.** An idle desktop session
  relays almost no bytes, which previously made the indicator falsely report "poor" on a healthy but
  quiet connection. The quality worker now periodically requests a bounded burst (256KB, every ~20s)
  over the `/ws/rdp-quality` channel and times the transfer, giving a real throughput reading
  regardless of desktop activity. Quality is now judged solely from this active speed test and the
  end-to-end RTT — no session traffic is counted towards it anymore.

---

## [0.6.15] — 2026-07-07 — Console session options honour saved/admin defaults via one shared editor

### Fixed
- **Console "Session options" now reflect the saved and admin-enforced defaults.** The visual-quality
  (performance-flag) checkboxes in the RDP console popup were hard-coded to all-on, so a manual login
  ignored the user's/resource's stored `PerformanceFlags` and only auto-connect sessions applied them.
  The popup now renders every checkbox — device toggles, display mode, HiDPI and all perf flags — from
  the pre-saved connection defaults (clamped by tenant Device policy), never hardcoded.

### Changed
- **Single shared connection-defaults editor.** `Views/Shared/_ConnectionDefaultsEditor.cshtml` gained a
  `"console"` render mode (`ViewData["Mode"]`) so the console popup and the resource Create/Edit/home
  settings forms render from the *same* partial and can no longer drift. Console mode emits the ids the
  connect handler reads, the named `RDP_PERF` perf keys, the admin per-feature lock UI, and the extra
  RemoteFX Progressive display option; form mode is unchanged.

---

## [0.6.14] — 2026-07-07 — Connector admin documentation, custom license, repositioned README

### Added
- **Connector administrator documentation.** A new [Connectors](features/connectors.md) page
  covers what a connector is, its outbound TLS control/data-channel model, enrollment via a
  one-time registration token (and regeneration), containerised deployment with the
  `ZEROVDI_URL` / `ZEROVDI_AUTH_TOKEN` / `ZEROVDI_REGISTRATION_TOKEN` variables, the CLI, and
  pinning a resource/backend to a connector. Linked from the docs index and "Where to start".

### Changed
- **Licensing.** The project is now distributed under the **KSol.IT Non-Commercial License**
  (see `LICENSE`): free for personal/educational non-commercial, non-enterprise use;
  commercial/enterprise use, copying, modification, and redistribution require a separate
  license from KSol.IT.
- **README rewritten** to position ZeroVDI as a fully browser-based **VDI solution** (not an
  "RDP gateway"), document the gateway + connector components, and drop the removed Windows
  Remote Desktop workspace-feed section. The admin docs index intro was updated to match.

---

## [0.6.13] — 2026-07-07 — Dashboard cards show the pool name instead of the generated VM name

### Changed
- **VDI desktops now display their pool's name on the dashboard, not the clone's generated VM name.**
  Once a user's pooled desktop is provisioned, its card previously surfaced through the concrete
  clone resource whose name is an auto-generated VM name (e.g. `pool-user-abc123`). The card now
  shows the friendly pool name for both the pre-provision pool entry point and the provisioned
  clone, giving the user a stable, recognisable label. Ordinary (non-pool) resources are unaffected.

---

## [0.6.12] — 2026-07-07 — Web console cursor no longer double-sized on HiDPI panels

### Fixed
- **HiDPI remote cursor rendered at 2× (or scale×) its intended size.** In HiDPI mode the remote
  desktop is streamed at the panel's device-pixel resolution, so the host sends pointer bitmaps in
  device pixels (e.g. a 32 px cursor arrives as 64 px on a 200 % panel). A CSS `cursor: url()` has no
  size parameter and the browser draws the PNG at its natural pixel size, so the cursor appeared at
  200 % of its correct on-screen size. The cursor bitmap and its hotspot are now downscaled before
  export so the pointer renders at its true physical size at any scale.
  - The downscale ratio is measured directly from the canvas — its backing-store (device-pixel) width
    ÷ its on-screen CSS width — rather than reading `devicePixelRatio` or assuming a fixed 2×, so it
    always matches the resolution actually in effect (including after live resolution changes).

### Changed
- **HiDPI desktop resolution is now derived from the canvas, not `devicePixelRatio`.** Once a session
  exists, the requested desktop resolution and its RDP DesktopScaleFactor are computed from the canvas's
  own native/logical ratio, so every live resize reuses the scale the panel is actually rendering at.
  Even the very first connect (before any canvas is laid out) no longer reads the `devicePixelRatio`
  property: it measures the panel's true ratio with a CSS `(resolution: …dppx)` media-query probe, which
  stays correct under browser/OS zoom and fractional scales where the property can be rounded or stale.
  This removes the class of stalls where a mid-resize `devicePixelRatio` momentarily reading 1 sent a
  spurious scale change to the host.

## [0.6.11] — 2026-07-06 — Web console HiDPI is now an opt-in setting

### Added
- **HiDPI (native resolution) toggle for the web console.** The console previously always requested the
  remote desktop at the panel's full device-pixel resolution (CSS × `devicePixelRatio`) with a matching
  RDP DesktopScaleFactor — crisp on Retina/HiDPI screens, but a 2× panel streams ~4× the pixels. HiDPI
  is now a setting, **off by default** for better performance: with it off the desktop is requested at
  the logical CSS size (scale factor 100) and upscaled to fit the panel. The choice is available in the
  console **Session options** popup, persists via *Remember settings*, and is honoured on later live
  resolution changes within the session.
  - Configurable as a starting default in the admin resource **connection defaults** (resource-wide and
    per-(user, resource) override) via the shared connection-defaults editor.

---

## [0.6.10] — 2026-07-06 — Connectors: reach RDP hosts and Proxmox clusters behind NAT

### Added
- **Connectors** — a new admin-managed proxy agent (the `KSol.ZeroVDI.Connector` console app) that lets
  the gateway reach RDP hosts and Proxmox clusters in networks it has no direct line of sight to. A
  connector is deployed inside the remote network and enrolls GitLab-runner style: an admin mints a
  one-time **registration token** under **Connectors**, the agent redeems it once for a long-lived auth
  token (only the token's hash is stored), then holds an **outbound** WebSocket control channel open. No
  inbound firewall rules or public IP are needed at the remote site.
  - **Tunnelled RDP** — on connect the gateway opens a per-session data channel over which the connector
    dials the target and relays plain TCP; the existing X.224 / TLS / CredSSP (NLA) pipeline runs over it
    unchanged.
  - **Tunnelled Proxmox API** — a Proxmox backend can be set to *reach via connector*, tunnelling its
    HTTPS API socket over the connector.
  - **Fastest-path selection** — for each host the gateway probes the direct route and every eligible
    connector in parallel and uses the lowest round-trip time (cached briefly). A directly reachable host
    is unaffected; a connector is used only when it wins. Connectors may optionally declare an allow-scope
    (host names / CIDRs) limiting which hosts they are considered for.
  - Connect-readiness now treats a host reachable through a connector as reachable (ICMP, which cannot
    traverse the tunnel, is no longer required for such hosts).
  - The **Connectors** admin page shows live online/offline status, last-seen time, and remote address,
    and can re-issue a connector's registration token.

---

## [0.6.9] — 2026-07-03 — Web client supports arbitrary-size and alpha pointers

### Fixed
- **The web console now renders every mouse pointer the host sends, at any size.** The client
  previously hard-rejected any pointer that was not exactly 32×32 (`unsupported pointer size: 41 39`
  in the console), discarding it entirely — so large cursors such as the Windows text I-beam, resize
  arrows, and hand pointer left the cursor frozen on its last shape. Pointer decoding now computes the
  correct WORD-padded scan-line stride for the reported width (matching FreeRDP and MS-RDPBCGR
  2.2.9.1.1.4.4/.5), and the pointer cache canvas is resized per pointer, so pointers up to the
  large-pointer maximum (96×96) are drawn correctly.
- **The text-edit (I-beam) cursor is now drawn correctly instead of an all-white shape.** Two bugs
  combined here:
  - The "white → inverted" pointer pixels (which the host uses for the whole I-beam glyph) were
    compared against the constant `0xFFFFFFFF`, but the decoded pixel is built with JavaScript's `<<`
    operator, which produces a **signed** 32-bit integer (`-1`), so the comparison never matched and
    every glyph pixel was written out as opaque white. The comparison now normalises to unsigned
    first, so inverted pixels are detected.
  - "Inverted" pointer pixels are meant to be XOR-combined with whatever is on screen behind them
    (this is how mstsc keeps the I-beam visible on any background). A CSS `cursor` image cannot XOR
    against the page, and the previous checkerboard fallback rendered as a faint, near-invisible
    white smear. Inverted pixels are now drawn as solid black, which is legible on the light document
    backgrounds where I-beams almost always appear.
- **The web console now renders 32-bpp color pointers with their real per-pixel alpha.** These carry
  their own alpha channel in the XOR mask (used for anti-aliased edges); the source alpha is now
  preserved rather than being forced fully opaque/transparent from the 1-bit AND mask. 24-bpp and
  1-bpp pointers keep their AND-mask transparency/inversion handling. Pointers are also encoded to the
  CSS `cursor` data URI as **PNG instead of lossy WebP**, since WebP from `canvas.toDataURL`
  drops/flattens the alpha channel in several browsers.
- Fixed a latent bug where the AND mask was only sampled from its first byte per scan line, so the
  transparency mask of any pointer wider than 8 px was read incorrectly.
- **The cursor no longer intermittently reverts to the OS default.** Color pointer updates
  (`PTR_COLOR`, the implicitly-24-bpp form of `PTR_NEW`) were silently dropped, so their pointer-cache
  slots were never populated; a later `PTR_CACHED` update referencing one of those slots resolved to
  an empty CSS class and the browser fell back to its own default cursor. `PTR_COLOR` is now decoded
  and cached like `PTR_NEW`, and a `PTR_CACHED` reference to an unknown slot now keeps the current
  cursor instead of reverting.
- **The remote cursor now covers the whole console, not just the canvas.** The pointer class was
  applied to `#canvas`, so the black letterbox margins around the session (and any area the canvas did
  not exactly fill) showed the browser's own arrow cursor — which read as the cursor "reverting" as
  the mouse moved off the canvas. It is now applied to the full-viewport `#screen-wrap`, inheriting
  down to the canvas, while overlays and dialogs (floatbar, clipboard, preflight, reconnect, login)
  are stacked above it and keep their own cursors. The class is now swapped without clobbering the
  element's other classes.
- **The browser no longer flips to the OS cursor near the bottom/right window edges.** Browsers
  deliberately revert a custom CSS cursor to the default whenever the cursor *image* would extend past
  the viewport edge (an anti-cursor-spoofing measure) — and RDP cursor bitmaps are fixed 32×32/96×96
  frames that are mostly transparent padding, so the dead zone was up to the full frame size. Cursor
  images are now cropped to the bounding box of their visible pixels (with the hotspot re-based), so
  the dead zone shrinks to the few pixels of actual glyph. A fully transparent bitmap is now treated
  as "hide the pointer".

### Added
- **Session recordings now include the mouse cursor.** The desktop video stream never contains the
  cursor (RDP sends pointer shapes out of band and clients draw them locally), so recordings played
  back without one. The gateway now mirrors the pointer state — shapes decoded from `PTR_NEW` /
  `PTR_COLOR` (same mask semantics as the web client), the shape cache, `PTR_CACHED` / `PTR_NULL` /
  `PTR_DEFAULT` transitions, and the position from the client's own mouse input plus `PTR_POSITION` —
  and alpha-blends the active cursor into each decoded desktop frame before it is H.264-encoded at
  mux. No post-processing re-encode is needed. When the host selects the system default pointer, a
  classic arrow is drawn. *Limitation:* this covers RemoteFX Progressive sessions (e.g. GNOME Remote
  Desktop), where frames are decoded server-side anyway; AVC sessions copy the host's H.264 stream
  verbatim at mux, and burning a cursor in there would require a full decode + re-encode pass — those
  recordings remain cursor-less for now.
- Added gated pointer-update tracing (`window.RDP_LOG = 1` in devtools) covering every pointer PDU —
  cache hits/misses, decode failures, and null/default transitions — to make future cursor issues
  diagnosable without rebuilding.

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
