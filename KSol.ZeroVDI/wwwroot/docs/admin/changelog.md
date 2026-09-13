# Changelog

All notable changes to ZeroVDI are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/). Dates are `YYYY-MM-DD`.

## [Unreleased]

---

## [0.6.39] — 2026-09-13 — WebKit surface coherence fix

### Fixed
- **RemoteFX Progressive garbled and ClearCodec black rects in Safari after 0.6.37.** 0.6.37 made the
  GFX surface canvases GPU-backed (dropped `willReadFrequently`). WebKit's accelerated OffscreenCanvas
  is not read-coherent: a `putImageData` (progressive tiles) is queued on the GPU side, and a later
  `drawImage` that uses the surface as a *source* — the coalesced output blit, SURFACE_TO_SURFACE window
  moves, SURFACE_TO_CACHE snapshots, the ClearCodec scratch composite and glyph readback — can sample the
  previous backing store. Result: duplicated/offset window fragments, black ClearCodec rects, stale cache
  slots replayed. Surfaces are now CPU-backed on WebKit only (`RdpGfx.cpuSurfaces`, override with
  `window.RDP_GFX_CPU_SURFACES`); Chromium and Firefox keep the GPU-backed surface. The coalesced
  per-surface blit, deferred frame acknowledgements and H.264 probe from 0.6.37 are all retained — the
  per-tile blit storm was the real Safari cost and is fixed by coalescing regardless of backing store.
- **Black desktop at session start in ClearCodec mode.** The "clearcodec" capset also advertises AVC420,
  so Windows opens with H.264 keyframes. The host encodes only the metablock region rects; the rest of
  the coded frame is black on a keyframe and stale afterwards. 0.6.38's full-frame draw stamped that over
  everything ClearCodec had painted. The region rects are honoured again, now through a clip path around
  a single full-frame draw instead of the per-rect source-sub-rect copy that WebKit mishandles.

---

## [0.6.38] — 2026-09-13 — H.264 rendering regression fix

### Fixed
- **Garbled desktop on H.264 (AVC420/444) sessions in Safari.** 0.6.37 changed the H.264 paint path to
  copy only the changed region rects out of the decoded `VideoFrame` with a source sub-rect. WebKit
  ignores the source rect on `drawImage(VideoFrame, sx, sy, sw, sh, …)` and squeezes the entire frame
  into each destination rect, so the whole desktop was rendered scaled into every changed tile. The
  decoded frame is again drawn in full at 0,0 (one GPU-side copy); the coalesced output blit from
  0.6.37 is kept.

---

## [0.6.37] — 2026-09-13 — Keyboard correctness (audit finding 6)

### Fixed
- **Arrow keys and the navigation cluster now carry the extended flag.** The four arrows were mapped
  onto keypad make codes with no `FASTPATH_INPUT_KBDFLAGS_EXTENDED` (and Up was sent as Numpad5's
  0x4C instead of 0x48), so the host read them as keypad digits: the cursor moved only while Num Lock
  happened to be off, and typed "5" the rest of the time. Home, End, Page Up, Page Down, Insert and
  Delete were not mapped at all and were swallowed silently.
- **Missing keys are mapped**: right Ctrl, right Alt / AltGr, both Windows (Command) keys, the context-
  menu key, the keypad's Enter and `/`, Print Screen, Scroll Lock, Pause and Break, the 102-key `<>`
  key, the F13–F24 row, the Japanese and Korean keys (Yen, Ro, Kana, Convert, Hangul, Hanja), the
  keypad `=` and ABNT `,`, and the ACPI/multimedia keys. Pause is sent as the Ctrl+Num Lock pair with
  the EXTENDED1 flag that [MS-RDPBCGR] 2.2.8.1.2.2.1 requires; Ctrl+Pause sends Break.
- **AltGr no longer arrives as Ctrl+AltGr.** Windows browsers report AltGr as a synthetic left Ctrl
  immediately followed by right Alt. Forwarding both leaves Ctrl held on the host, which Linux and
  GNOME Remote Desktop targets do not read as AltGr — `@`, `\`, `{`, `}`, `[`, `]` and the other
  third-level characters of the European layouts never arrived. The synthetic Ctrl is now retracted
  when the right Alt follows it within the same key press.
- **Keystroke events are 2 bytes, not 3.** Every scancode event carried a trailing zero byte — a stray
  `FASTPATH_INPUT_EVENT_SCANCODE` header past the event the fast-path PDU declared.
- **Keys stuck down after a macOS Command shortcut.** macOS does not deliver keyup for ordinary keys
  while Command is held, so a Cmd+C left "C" pressed on the host indefinitely; the non-modifier keys
  Command hid are now released when Command itself comes up.

### Added
- **Toggle-key synchronisation.** A `TS_FP_SYNC_EVENT` is sent on activation (resetting the host's
  key-down state) and whenever the browser reports Caps Lock, Num Lock or Scroll Lock in a state the
  host has not been told about — a Caps Lock toggled outside the session no longer leaves the remote
  typing in capitals. Because the sync also resets the host to "all keys up", the keys actually held
  are re-pressed right after it, as the spec prescribes. Apple keyboards have no Num Lock and report
  it permanently off, so the Windows default (on) is reported instead to keep the remote keypad in
  numeric mode.
- **Ctrl+Alt+Del button** in the session toolbar. The combination is grabbed by the local OS and never
  reaches the page, so the remote security screen (lock, change password, Task Manager) was
  unreachable. `Client.sendKeyCombo(codes)` backs it and is available for any other combination the
  browser swallows.
- **Unicode keyboard events.** A key with no physical position we recognise (soft keyboards, exotic
  layouts) that still produced a printable character is now sent as a `TS_FP_UNICODE_KEYBOARD_EVENT`
  instead of being dropped. `Client.sendUnicodeText(text)` types a string into the session.
- `window.RDP_MAC_CMD_AS_CTRL` — opt-in for Mac users who want Command to act as the remote's Ctrl
  (Cmd+C, Cmd+V) rather than as the Windows key, which is the default because it is the physical
  truth and is what makes Win+R and Win+E reachable.

---

## [0.6.36] — 2026-09-13 — Security headers, public base URL, recorder direction fix (audit findings 5 and 7)

### Security
- **Security headers on every response**: a Content-Security-Policy (scripts from this origin only —
  inline still permitted for the Razor views, a nonce migration is the follow-up; styles/fonts from
  this origin plus the two CDNs the layout uses; `frame-ancestors 'none'`, `object-src 'none'`,
  `base-uri 'self'`, `form-action 'self'`), `X-Frame-Options: DENY`, `X-Content-Type-Options:
  nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, and a `Permissions-Policy` that grants
  camera and microphone to this origin only. The console drives keyboard and mouse into a remote
  desktop, so it must never be framed by another site.
- **Password-reset and e-mail-change links no longer trust the `Host` header.** New `App:PublicBaseUrl`
  (e.g. `https://vdi.example.com`) is used for every link that leaves the browser (`PublicUrl` helper);
  with `AllowedHosts` at `*` an attacker could previously request a reset for a victim with a forged
  Host header and the victim's e-mail carried a link to the attacker's domain. Without the setting the
  request host is still used (after the trusted forwarded headers) and a startup warning asks for it
  in production. The connector enrolment command shows the same public address.
- **ZGFX multipart allocation is bounded** by `segmentCount × 65536` ([MS-RDPEGFX] 2.2.5.1); the size
  came straight off the wire and a hostile host could request a 4 GB buffer from the recorder.

### Fixed
- **Recorder: per-channel stream state is keyed by direction** (`RdpSession`/`RdpChannels`). DVC
  reassembly, static-channel reassembly, MPPC contexts and both ZGFX contexts were keyed by channel id
  alone while both directions feed one session in wire order; the browser's per-frame FRAME_ACK/QOE
  PDUs on the Graphics channel landed inside a pending server frame's accumulator and its raw ZGFX
  segments polluted the host-direction history, corrupting every later cross-PDU match from Windows
  hosts. Recordings of Windows RDS sessions are usable again.
- **Recorder: a framing error no longer makes the decoder retain the rest of the stream in memory.** An
  invalid unit length used to leave the receive buffer unconsumed and growing for the whole session; it
  now emits a `raw.desync` event, forwards the bytes untouched and drops the buffer (bounded at 4 MB).
- **Opening a recording is audited** (`RecordingViewed`), as the audit docs had claimed all along.

### Changed
- Docs: configuration keys gained `App:PublicBaseUrl` / `AllowedHosts`; installation notes cover the
  public URL, host filtering, forwarded headers and the no-framing rule; the audit page lists the new
  events (`RecordingViewed`, `HostCertificate*`, `SessionRejectedPolicy`, `VdiInstanceReconciled`).

---

## [0.6.35] — 2026-09-13 — VDI broker reconciler and clone lifecycle (audit finding 4)

The clone-on-connect broker had no second half: anything the connect-time path could not finish
stayed that way forever. This release adds the reconcile loop, brings clones under the idle policy,
and removes the stubs an administrator could configure without effect.

### Added
- **`VdiReconcileService`** (every 2 min) driving `VdiProvisioningService.ReconcileAsync`:
  - a *Provisioning* instance nobody is working on is **resumed** if its clone task finished (notes
    stamp, identity, Ready) or **marked Failed** with the reason if the task failed, is unknown, or ran
    past a 2-hour cap — previously such a row was stuck forever and the unique (pool, owner) index made
    every later connect for that user fail with a database error;
  - *Failed* / *Deprovisioning* instances have their VM **destroyed through the notes-verified path**
    and their rows removed; a clone that finished after its wait is stamped first so it can be verified —
    a slow clone no longer leaks a VM that discovery later adopts as a plain resource;
  - a *Ready* instance whose VM was deleted in Proxmox is marked Failed (the dashboard stopped
    advertising a ghost desktop; the user gets a fresh one next connect);
  - a missing notes stamp is retried (an unstamped clone can never be destroyed safely).
  Every action is audited as `VdiInstanceReconciled`.
- `VdiInstance` gained `CloneUpid`, `NotesStamped`, `LastError` and `UpdatedUtc` (migration
  `AddVdiInstanceLifecycle`); the pool page shows state colours and the last error per desktop.
- `ProxmoxClient.GetTaskStatusAsync` (single probe, node taken from the UPID).
- Kerberos realm / KDC host are now editable on the backend form (they were consumed by the relay but
  could only be set by editing the database).

### Fixed
- **VDI clones are idle-managed and status-polled** like discovered VMs: the idle reaper and the status
  service filtered on `Source == Proxmox` and skipped `VdiClone`, so a clone ran forever once started.
- **Connect-time cleanup destroys the previous failed clone's VM** (notes-verified) instead of
  abandoning it, and refuses with a clear message while a previous clone task is still running.
- **VMID allocation is serialised** across pools and skips ids already claimed by in-flight or failed
  rows; two users provisioning at once could previously pick the same id and the second clone failed.
- **The connector path selector no longer caches "unreachable" for 60 s.** The readiness probe loops
  (2–3 s cadence) saw a stale negative result for a whole minute after the first miss on a booting VM,
  adding up to 60 s to every cold start.
- **The WebSocket relay reuses the preflight's Ready result** (≤ 90 s old) instead of re-running the
  whole readiness sequence, halving the Proxmox calls per connect.
- **A VM that started but reported no IP/RDP in time is recorded as Starting, not Stopped.**
- **Deprovisioning a desktop or deleting a pool is refused while a session is active on it** (the VM
  was hard-stopped under the user).
- **Deleting a backend that still backs VDI pools** explains instead of failing with a 500.
- **Pool Update validates** template, port, VMID range order and max desktops.

### Changed
- **Floating pools and guest-agent identity cannot be saved** until they exist: both were selectable
  and documented as shipped while the provisioner refused floating pools at connect time and never
  applied guest-agent rename/domain join. The options are shown disabled and the docs say so; the
  hostname pattern is documented as not applied yet (the clone's hostname is its VM name).
- Docs: the VDI pools page describes the actual lifecycle, the reconciler, the idle policy for clones
  and the full set of Proxmox token permissions (incl. `VM.Config.Options`, `VM.PowerMgmt`,
  `VM.Monitor`, `VM.Audit`).

---

## [0.6.34] — 2026-09-13 — Host identity: CredSSP binding verified, certificate pinning, readable NLA errors (audit finding 3)

The gateway accepted any TLS certificate from an RDP host on the strength of "CredSSP's public-key
binding detects a man-in-the-middle" — but that binding was never actually verified, so the protection
did not exist. Both halves are fixed.

### Security
- **CredSSP server public-key confirmation is verified** ([MS-CSSP] 3.1.5 step 5). The client now
  unseals the server's `pubKeyAuth` with the NTLM server-to-client keys, checks its signature and
  sequence number, and compares it with the expected Server-To-Client binding hash of the certificate
  the gateway saw. An active interceptor can forward the NTLM exchange but cannot seal that
  confirmation without the account password, so the handshake now fails before the credentials are
  delegated. Previously only the presence of the field was checked (`NtlmClient.UnsealAndVerify`,
  `CredSspClient`). The v2–4 form (raw public key, first byte incremented) is handled for old servers,
  and the client now sends the matching v2–4 form to them instead of a nonce hash they cannot verify.
- **Host certificate pinning (trust on first use).** The SHA-256 fingerprint of the certificate a
  resource presents on its first successful connection is stored on the resource; every later
  connection must present the same certificate or it is refused *before any credential is sent* (this
  also protects the TLS-only xrdp path, which has no CredSSP at all). Audited as
  `HostCertificatePinned`, failed `HostCertificateMismatch`, and `HostCertificateReset` when an
  administrator forgets the pin from the resource's **Backend & VM** tab (new **Host certificate**
  card with subject, fingerprint, pin date and a confirmed *Forget* action). `HostCertificates:Mode`
  = `Tofu` (default) / `Audit` (log only, for rollout) / `Off`. New `HostCertificatePolicy` service;
  `RdpResolveRequest`/`RdpHostConnection` gained a certificate-check hook. Migration
  `AddHostCertPinning` adds `HostCertFingerprint`, `HostCertSubject`, `HostCertPinnedUtc` to
  `RDPResources`.
- **Proxmox `Verify TLS certificate` defaults to on** for new backends (existing rows keep their
  saved value). The API token that manages every VM travels over that connection.

### Fixed
- **NLA failures say what went wrong.** The CredSSP `errorCode` NTSTATUS is mapped to a plain
  message: wrong password, account locked out / disabled / expired, password expired or must be
  changed, not allowed to sign in remotely, domain controller unreachable, NTLM disabled on the host.
  Every case used to read "bad credentials or NLA refused".
- User troubleshooting docs gained entries for the certificate-changed and desktop-sign-in-rejected
  messages; admin docs (security, resources, configuration keys) describe pinning and the new key.

---

## [0.6.33] — 2026-09-13 — Device policy enforced in the relay (audit finding 2)

### Security
- **Device policy is now enforced at the protocol level.** A feature set to *Disabled* (clipboard,
  remote audio, microphone, camera) was only clamped in the console UI and the settings save endpoint;
  the WebSocket relay was a transparent byte tunnel, so a modified client could still join
  `cliprdr`/`rdpsnd` or accept the `AUDIO_INPUT` / camera dynamic channels. Code comments claimed the
  relay clamped — it did not. A new `ChannelPolicyGuard` inspects the relayed RDP stream: it parses the
  client's MCS Connect Initial (CS_NET) and refuses a session that requests a blocked static channel,
  learns dynamic-channel names from the host's DYNVC_CREATE PDUs on `drdynvc`, and ends the session the
  moment the client *accepts* a blocked dynamic channel (creation status 0) — before any channel data
  can flow. The user sees "… redirection is disabled by your administrator's device policy"; the event
  is audited as a failed `SessionRejectedPolicy` (category Session) with the feature and channel name.
  The stock browser client never trips it: it already omits blocked static channels and rejects blocked
  DVC creates. Static-channel bulk compression (MPPC 8K/64K) on `drdynvc` is inflated so the check
  cannot be dodged by compressing the CREATE exchange. The guard is fail-open on a framing error
  (logged) rather than dropping healthy sessions, its buffers are bounded, and it is not instantiated
  at all when no feature is Disabled. *Forced* has no protocol-level meaning and stays UI-only.

### Changed
- Docs: the device-policy page's "limitation" paragraph is gone; it now documents the four enforcement
  points including the relay guard, the disconnect behaviour and the new audit event.

---

## [0.6.32] — 2026-09-13 — Web client render path rebuilt (Safari usable, real frame pacing, H.264 probe)

The browser client was very slow in Safari/WebKit and could accumulate seconds of lag in every browser
under load. Both traced to the same rendering decisions, fixed here. This is the first slice of the
2026-09-12 audit (`docs/zerovdi-audit.html`, finding 1 plus the preflight fix from finding 5).

### Fixed
- **Output compositing is coalesced.** Surface writes now only mark a per-surface dirty rectangle; ONE
  blit per surface goes to the visible canvas at END_FRAME (and at the next animation frame for
  updates that land outside a frame, such as H.264 output). Previously EVERY 64×64 RemoteFX
  Progressive tile, every ClearCodec rect, every SOLIDFILL rect and every cache blit was drawn straight
  to the output canvas — hundreds of `drawImage` calls per PDU. On WebKit each such draw materialises a
  native image of the whole surface and the next `putImageData` copies the whole backing store, so one
  progressive PDU cost gigabytes of memcpy. That was the Safari slowness.
- **GFX surfaces are GPU-backed again** (the `willReadFrequently` hint from 0.6.31 is gone). With it,
  every decoded H.264 `VideoFrame` was read back to CPU and colour-converted, and every blit to the
  output re-uploaded the surface. The two readback consumers changed instead: the black-cache scan on
  SURFACE_TO_CACHE now runs only with `RDP_GFX_DIAG` set, and the ClearCodec glyph snapshot reads the
  canvas only when the decoded glyph has uncovered pixels (rare, ≤1024 px).
- **H.264 frames copy only their region rects** into the surface ([MS-RDPEGFX] 2.2.4.4) instead of the
  whole coded frame, and only those rects are blitted to the output.
- **FRAME_ACKNOWLEDGE is truthful.** The ack (and QOE ack) for a frame is sent once the frame's content
  has landed — every async decode submitted before END_FRAME settled and every accepted H.264 chunk
  produced its output frame — with `queueDepth` = codec bytes still in flight and a real `timeDiffEDR`.
  Acking at parse time with depth 0 told the host we were keeping up while the decode backlog grew
  without bound ([MS-RDPEGFX] 3.2.1.2 bounds unacknowledged frames; that IS the throttle). A 300 ms
  safety timer force-acks a frame whose decoder output is being held back (a decoder that keeps one
  frame in its pipeline releases it only when the next chunk arrives), so the ack loop can never stall
  the host.
- **H.264 support is probed before capabilities are advertised.** `VideoDecoder.isConfigSupported`
  (High and Main profile) runs at script load; a browser without a usable decoder now negotiates
  RemoteFX Progressive instead of AVC. If the decoder still fails at runtime the session ends with
  "This browser cannot decode H.264 video…" and the next connect uses Progressive — previously the
  worker silently dropped every frame and the user saw a frozen or black desktop.
- **`requestKeyframe` was undefined** at both call sites; it now sends a refresh-rect for the whole
  desktop so a rebuilt H.264 decoder gets a fresh keyframe instead of staying black.
- **Mouse moves are coalesced to one per animation frame** (they were one WebSocket send plus a layout
  query per pointer report, 120+/s on ProMotion displays); the canvas rectangle is cached for moves and
  invalidated on resize, scroll and fit.
- **Held keys are released on window blur** (Alt+Tab / Cmd+Tab / a dialog stealing focus left the host
  with a stuck Ctrl/Alt/Shift).
- **HiDPI is capped** at a 200 % scale factor and 3840×2160 — a 5K Retina panel at 2× requested a
  5120×2880 desktop, four times the decode work of 1440p.
- **Preflight shows the clone step.** The readiness API reports a `provisioning` phase during the first
  connect to a VDI pool, but the console's step list did not include it, so the progress bar sat on
  "Checking resource… Step 1 of 6" for the whole clone. It now shows "Creating your desktop…".

### Changed
- `totalFramesDecoded` in FRAME_ACKNOWLEDGE counts frames whose content landed, not END_FRAMEs parsed.
- User docs: connection settings now describe the automatic H.264 → Progressive fallback and the HiDPI cap.

---

## [0.6.31] — 2026-07-19 — GFX black content areas fixed (decode barrier could wedge)

Large regions of the desktop rendered permanently black over GFX (ClearCodec / RemoteFX Progressive) —
intermittently, and only for some areas (e.g. a strip of the Windows taskbar). Hovering the mouse
repainted the region; leaving restored black. Other clients (mstsc, FreeRDP) rendered the same host fine.

### Fixed
- **`SURFACE_TO_CACHE` no longer snapshots a surface mid-decode (the black-cache bug).** Two defects in the
  ordered-decode queue let a cache snapshot run before its content had painted, so the slot stored black
  and every later `CACHE_TO_SURFACE` replayed it (hover repainted the region; leaving restored the black):
  - **Re-entrant drain.** Draining the queue dispatches PDUs, and a queued Progressive `WIRE_TO_SURFACE_2`
    submits a *new* async decode whose worker reply could land while the drain loop was still running —
    re-entering the drain, advancing the settle counter again and releasing ops whose content decode was
    still in flight. The drain is now serialised with a re-entrancy guard and a re-run flag.
  - **A long-queued cache op could outlive its barrier's meaning.** `SURFACE_TO_CACHE` *reads* the surface,
    so "every decode queued before me landed" is not enough — a newer decode carrying the content for its
    rect could still be running (observed: `barrier=4, settledSeq=4` releasing it while decode #5 was
    inflight, with 1225 ops backed up). It now additionally waits for the decode pipeline to fully quiesce.
- **The ordered-decode barrier could wedge, poisoning the GFX cache with black.** Every async decode
  submitted to the decode worker must advance a settle counter exactly once; order-sensitive ops
  (`SURFACE_TO_CACHE`, `SURFACE_TO_SURFACE`, `CACHE_TO_SURFACE`, `SOLIDFILL`) queue behind it until the
  content has painted. If a worker decode threw **before** posting its reply — a throw outside the inner
  try (e.g. building the per-surface decode context) — that reply never came, the settle counter stalled
  below the submit counter, and **every** later order-sensitive op queued forever. `SURFACE_TO_CACHE` then
  snapshotted the still-black surface, and `CACHE_TO_SURFACE` tiled that black across the desktop; the
  region only refreshed on a fresh hover-triggered decode. Diagnosed from a persisted trace showing the
  queue piling into the thousands (`queuedOps=2979`) with the settle counter stuck (`settledSeq=2 <
  decodeSeq=5`, `inflightDecodes=3`). The decode worker's message handler now always emits a (failed)
  result carrying the request id when a handler throws, and the main thread advances the barrier in a
  `finally`, so the queue can never wedge regardless of a decode failure.
- **SOLIDFILL is always opaque.** The fill pixel's alpha is ignored per [MS-RDPEGFX] 3.3.5.4 (FreeRDP
  hardcodes `0xFF`); the client previously honored it on ARGB surfaces, where Windows sends alpha 0 for an
  opaque fill, blacking out filled rects.
- **GFX surface canvases are created with `willReadFrequently`.** They are read back regularly (ClearCodec
  re-snapshots its glyph from the composed destination; the black-cache detection scans cache source rects),
  and without the hint each `getImageData` stalled on a GPU readback.

### Added
- **Persisted GFX diagnostic ring buffer, always on (including production).** The last ~2000 `[DIAG]`
  lines are retained in memory even with logging off; dump them from devtools with `window.rdpDiagDump()`
  (`copy(rdpDiagDump().join('\n'))` to grab the history after an artifact appears, `rdpDiagDump(true)` to
  clear). The interior black-cache-snapshot detection that produces the decisive line runs unconditionally
  (it's cheap and skips the benign black-background edges), so a production occurrence is captured without
  pre-enabling anything. `window.RDP_GFX_DIAG=1/2` still gates the heavier per-tile scans, hex dumps, and
  PDU census; `window.rdpDiagScan()` scans every surface on demand.

Two admin-console papercuts. Scrolling **down** inside the web RDP client ran far faster than
scrolling up, and saving anything inside a tab on the resource or user edit pages bounced you back
to the first tab.

### Fixed
- **Web RDP client: downward scrolling no longer races.** The RDP wheel rotation field is only 8
  bits with a separate sign flag, but the client masked the magnitude to 9 bits (`0x01FF`), letting
  it collide with the negative-direction flag and inflate the delta the host decoded — so downward
  (negative) scrolls moved much faster than upward ones. The magnitude is now masked to 8 bits and
  encoded as a two's-complement value for negative directions, and per-event wheel deltas are
  normalised across the browser's `deltaMode` and capped at one notch so both directions move at the
  same speed.
- **Resource & user edit pages stay on the active tab after saving.** Forms inside a tab POST and
  redirect, which drops the URL fragment and previously landed the user back on the default tab
  ("General" / "Account"). The active tab is now remembered per resource/user in `sessionStorage`
  and restored on load, while an explicit URL fragment (deep link) still wins.

## [0.6.25] — 2026-07-17 — Wake-on-LAN reaches the physical LAN (host networking)

Wake-on-LAN never woke the target machine. The app sends the magic packet as a UDP broadcast to
`255.255.255.255`, but the app container ran on a **bridged** Docker network, so the broadcast was
trapped inside the container's isolated subnet and never reached the physical LAN where the target
machine sits.

### Fixed
- **Wake-on-LAN magic packet now reaches the LAN.** The app container runs with `network_mode: host`
  so the broadcast egresses on the host's real network interface.

### Changed
- The app now binds directly on the host at port **8084** (via `ASPNETCORE_HTTP_PORTS`) instead of
  the bridged `8084:80` port mapping, which is invalid under host networking. The external port is
  unchanged.

---

## [0.6.24] — 2026-07-15 — Console auto-reconnect on network/protocol drops (keeps the last frame)

When a live console session **dropped because of a network or protocol failure** — the WebSocket died, or the
connection tore down without the RDP host actually ending the session (no logoff / restart / admin disconnect) —
the console immediately blanked the screen and dumped the user back to the login / reconnect overlay. A brief
Wi-Fi blip or gateway restart cost the whole session.

The console now distinguishes a **graceful host disconnect** (session really over) from a **drop** (the desktop is
still there) and, on a drop, tries to reconnect automatically instead of tearing down.

### Added
- **"Trying to reconnect…" state** on network/protocol drops. The last rendered desktop frame **stays on the
  canvas** (no more instant black screen) behind a small centered card.
- **Countdown to a hard deadline (2 min).** The card shows the seconds remaining until the connection is
  considered dead for real; when it hits zero the console falls back to the normal disconnected UI.
- **Automatic retry every 15 s** for the duration of the countdown, replaying the session's credentials/options
  and re-sizing to the current viewport. A **Retry now** button forces an immediate attempt; **Cancel** gives up.
- A successful reconnect (session goes active again) silently clears the state and restores the session.
- **Reconnect keeps the session's DPI scale** instead of re-applying it. A frame-preserving reconnect leaves
  the framebuffer at its established device-pixel resolution, so the client no longer re-runs the initial-scale
  sequence on top of an already-scaled framebuffer — which had rendered the reconnected desktop at double size.

### Changed
- The web client now emits a distinct **`reconnecting`** status for ungraceful drops, reserving `closed` for a
  genuine end of session (user Disconnect, or a host logoff / restart / admin disconnect PDU). Hard gateway
  errors (bad credentials, host unreachable) still surface as `error` and do **not** trigger the retry loop.

### Fixed
- **A user-initiated Disconnect no longer starts a reconnect.** `disconnect()` ran teardown directly and
  then closed the socket, whose `onclose` handler re-ran teardown a second time — by which point the
  "intentional close" flag had been consumed, so the second pass emitted `reconnecting` and kicked off the
  retry loop on a session the user had deliberately ended. Teardown now runs exactly once (the socket
  handlers are detached before the explicit close).

---

## [0.6.23] — 2026-07-15 — Ubuntu/xrdp connect: cancellation no longer masquerades as "cannot reach host"

Connecting to an Ubuntu (xrdp) VM could fail with a misleading **"cannot reach 10.x.x.x:3389"** even though the
host was reachable. The real trigger was a **caller cancellation** (browser closed the WebSocket, or the connect
outran the client's patience) landing mid-handshake — but the host-connect helper laundered every
`OperationCanceledException` into a generic `ConnectException`. That misclassified the cancel as an NLA rejection,
fired a pointless TLS-only retry (whose bare TCP connect then also cancelled), and surfaced "cannot reach host"
for a live VM.

### Fixed
- **Caller cancellation now propagates as cancellation** at every host-connect stage (TCP connect, X.224
  negotiation, TLS handshake). It no longer becomes a `ConnectException`, so the HYBRID→SSL fallback does not
  fire on a torn-down request and the log stops reporting an unreachable host for a reachable one.
- **The NLA→TLS-only retry is guarded** against a cancelled token, so a request that was cancelled during the
  HYBRID attempt never triggers a second doomed attempt.

### Added
- **Per-attempt connect/negotiation timeout (10s).** An xrdp host that stalls the socket on an NLA request
  (instead of promptly RSTing) can no longer consume the whole client-patience budget on attempt #1 — the
  HYBRID attempt now times out and falls through to the TLS-only retry that xrdp actually accepts.

---

## [0.6.22] — 2026-07-15 — GFX black content areas fixed (ClearCodec state desync) + diagnostics

Large areas of the desktop rendered permanently black over GFX (ClearCodec / RemoteFX Progressive), while
other clients (mstsc, FreeRDP) rendered the same host fine. Hovering revealed the real content; leaving the
hover restored black from the GFX cache. New opt-in diagnostics traced it to a **codec state desync**, and
this release fixes the underlying cause.

### Fixed
- **ClearCodec content no longer decodes black after a dropped tile.** When the host streamed a
  `WIRE_TO_SURFACE_1/2` for a surface id that was transiently absent (its `DELETE_SURFACE → … → RESET_GRAPHICS
  → CREATE_SURFACE` reuse window, or the first paint arriving before `CREATE_SURFACE`), the client dropped
  the whole PDU. ClearCodec is a **stateful stream** — each tile carries a `seqNumber` and populates the
  session-global glyph / VBar caches. Dropping a tile skipped that state: the `seqNumber` jumped
  (`seqNumber N != expected`) and later tiles that `VBAR_CACHE_HIT` the never-populated slots decoded blank
  (black), which `SURFACE_TO_CACHE` then snapshotted and `CACHE_TO_SURFACE` tiled across the desktop.
- **Content painted before `CREATE_SURFACE` is now preserved.** A PDU for an absent surface now lazily
  materialises a real **orphan surface** (sized to the known output), decoded and painted normally so codec
  state *and* pixels stay correct; the next `CREATE_SURFACE` for that id **adopts** the orphan's canvas
  instead of allocating a fresh black one, keeping everything already drawn.

### Added
- **`window.RDP_GFX_DIAG` web-client flag** (default off, set live in devtools — no reconnect). At level 1,
  every ClearCodec / Progressive paint scans the region it just wrote; if it came out black/transparent,
  the client logs the frame's surface + geometry and a hex dump of the **full codec message** that produced
  it, so the exact wire bytes can be replayed and inspected. Level 2 additionally dumps every ClearCodec /
  Progressive message (at receipt and after paint), black or not.
- **Blit/fill ops are scanned too.** The same black-region check runs after `SOLIDFILL`,
  `SURFACE_TO_SURFACE`, and `CACHE_TO_SURFACE`, so a black area spread by a black fill or an empty/black
  cache slot — not only a codec decode — is caught and attributed (the log names the fill color or the
  source surface / cache slot + rect).
- **Cache-slot provenance for the "black returns from cache" case.** `SURFACE_TO_CACHE` now records
  whether the source rect was black *at snapshot time* (`slot.snapBlack`), and the `CACHE_TO_SURFACE`
  black report is coalesced into one line per PDU that states whether the slot was cached black (content
  never landed upstream — the root cause) or cached non-black yet replays black (a blit/surface-state
  problem). This turns the hover-reveals / un-hover-restores-black symptom into a single decisive log line
  instead of hundreds of per-tile lines that overflow the console.
- **`window.rdpDiagScan()` devtools helper** (`RdpGfx.diagScanAll`). Call it anytime — even with the flag
  off, and regardless of when the black appeared — to scan every GFX surface right now and print an 8×8
  black-cell grid plus each surface's size, `touched` state, and mapped output origin, pinning a stale
  black region to a specific surface.
- Diagnostic helpers in `rdpgfx.js`: `_scanBlack` (stride-sampled non-black/opacity scan of a painted
  region), `_hexDump`, and `_diagPaint`, wired through the worker, sync-fallback, sparse, and single-bbox
  paint paths for both codecs and the blit/fill ops. All work is gated behind the flag (the pixel readback
  and byte copies are skipped entirely when it is off), so normal rendering is unaffected.

## [0.6.21] — 2026-07-13 — Idle reaper: startup grace period and an off switch

The idle reaper could pause a VM immediately after the gateway started, before it had any way of knowing
how long that VM had actually been idle. On a fresh start `LastActivityUtc` is null, and the old check
treated "never observed active this run" as "idle forever" and reaped at once. Backends also had no way
to opt out of auto-suspend entirely.

### Fixed
- **Never reap within one idle window of startup.** The reaper records its start time and defers any pause
  until at least a full idle timeout has elapsed since then, so a restart no longer suspends VMs that were
  in use moments earlier.
- **Unknown last activity is treated as active-at-startup, not idle-forever.** A null `LastActivityUtc`
  now gets the full idle grace period instead of being reaped on the first scan.

### Added
- **Per-backend "Auto-suspend idle VMs" toggle** (`ProxmoxBackend.IdleReapEnabled`, default on). Turn it
  off to keep a backend's VMs running indefinitely regardless of the idle timeout. Shown on the backend
  edit form and summarised in the backends list.

### Migration
- `AddBackendIdleReapEnabled` adds the `IdleReapEnabled` column, defaulting existing backends to enabled.

## [0.6.20] — 2026-07-13 — Removed the VNC/SPICE bridge; RDP-only

The experimental VNC and SPICE→RDP bridges did not work well enough to keep, and their machinery (a
full server-side RDP encoder, H.264/RemoteFX-Progressive output paths, the RFB/SPICE client stacks, and
the Proxmox spiceproxy tunnel) carried a large, fragile surface area. This release **removes** all of it.
The gateway now speaks **only native RDP** to hosts. There is no data migration to perform beyond the
schema change below; any resources previously marked VNC or SPICE will simply connect over RDP on their
configured port.

### Removed
- **VNC and SPICE host bridges.** The entire bridge stack is gone: `RfbClient`/`SpiceClient` and the
  `IProtocolSource` seam, the shared `RdpEncoderSession` + RDPEGFX/DVC server, the H.264 (libx264/ffmpeg)
  and RemoteFX-Progressive encoders, the Tight/ZRLE VNC decoders, and the Proxmox `spiceproxy` ticket +
  tunnel path. The console, recorder and the native-RDP resolver (`NlaRdpResolver`) are unaffected.
- **Resource `Protocol` and `Keyboard layout` settings.** Resources no longer carry a wire-protocol or
  keyboard-layout choice — every resource is native RDP, which negotiates its own layout with the client.
  The fields are dropped from the resource create/edit forms.
- **Backend `Default VNC port` / `Default SPICE port` settings.** Only **Default RDP port** remains.
  Discovery no longer auto-detects a protocol from the VM's display adapter; discovered VMs default to the
  backend's RDP port.

### Database
- Migration `RemoveVncSpiceProtocol` drops `RDPResources.Protocol`, `RDPResources.KeyboardLayout`,
  `ProxmoxBackends.DefaultVncPort` and `ProxmoxBackends.DefaultSpicePort`. Applied automatically on
  startup.

### Fixed
- **Linux/xrdp hosts (e.g. Ubuntu) now connect.** The gateway asked every host for NLA (X.224 `HYBRID`).
  Windows answers an unsupported request with a clean negotiation failure, but xrdp (the common Ubuntu RDP
  server) simply **resets the TCP connection** the moment it sees an NLA request — so the connect died at
  X.224 with `Connection reset by peer`, never reaching credentials. The gateway now retries with plain
  `SSL` (TLS-only, in-band login) when the NLA negotiation fails, exactly as mstsc/FreeRDP do, and tells the
  browser which protocol was selected so it stamps the matching `serverSelectedProtocol` into CS_CORE.
- **HiDPI console setting is now honored.** The "HiDPI (native resolution)" option had no effect on the
  first connect: the web client mis-read the `<canvas>` element's static placeholder dimensions as a
  laid-out session canvas and derived a device-pixel ratio of 1, so the requested desktop resolution never
  scaled up on Retina/HiDPI displays. It now probes the real device-pixel ratio when no prior session
  canvas exists, so HiDPI requests the full native resolution as intended.
- **Discovered VMs keep their stamped identity.** If a VM's notes already carry a `ksol-rdpgw-id` but no
  matching row exists in our DB, discovery now **adopts that id** for the resource row instead of minting
  a fresh GUID and re-stamping the VM — so issued `.rdp` files, authorizations and recordings stay bound.

---

## [0.6.19] — 2026-07-09 — VNC bridge: live desktop over the bitmap path (M1+M2)

### Added
- **VNC resources now display the real remote desktop in the browser console.** The VNC resolver
  connects to the host over RFB/VNC (VNC-Authentication or None; connector-tunnelled hosts supported),
  reads the framebuffer, and bridges it to the unmodified browser RDP client by running a minimal
  in-gateway RDP *server*: it answers the full connection sequence (MCS Connect-Response →
  Attach-User/Channel-Join confirms → licensing → Demand-Active → finalization) and streams the
  framebuffer as legacy fastpath **bitmap** updates (32bpp BGRX → 16bpp RGB565, bottom-up, banded into
  ~15 KB SINGLE-fragment PDUs to match what a Windows host sends). Server encoders and framing were
  cross-checked against the macRDP reference server. This is the first end-to-end VNC→RDP path; it shows
  a static full frame — live incremental updates, mouse, and keyboard follow in the next releases, and
  H.264/GFX + RemoteFX-Progressive/ClearCodec output after that.

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
