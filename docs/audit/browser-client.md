# ZeroVDI browser RDP client audit — Safari/WebKit performance + spec coverage

Scope: `KSol.ZeroVDI/wwwroot/lib/rdpweb/*` (client.js, protocol.js, rdpgfx.js, decode-worker.js, progressive.js, clear.js, zgfx.js, input/*, update/*, rle/*, cliprdr.js, rdpsnd.js, audin.js, rdpecam.js), `Views/Home/Console.cshtml`, `Views/Shared/_ConnectionDefaultsEditor.cshtml`, `Models/ConnectionDefaults.cs`, `RDP/RdpRelaySession.cs`, `RDP/RdpChannels.cs`, `Spec/[MS-*].md`. Paths below are relative to `KSol.ZeroVDI/wwwroot/lib/rdpweb/` unless absolute. Read-only; nothing executed.

---

# PART A — SAFARI / WEBKIT PERFORMANCE

## A.0 What the client actually does per frame (the pipeline that Safari runs)

Default mode is **avc420** (`Models/ConnectionDefaults.cs:31`, dropdown `Views/Shared/_ConnectionDefaultsEditor.cshtml:125-129`, read in `Views/Home/Console.cshtml:803-809` → `window.RDP_GFX_MODE` → `protocol.js:37-53 rdpGfxMode()` → `protocol.js:2412 new RdpGfx({mode})`). There is **no browser/capability sniffing anywhere** (grep `Safari|WebKit|userAgent|isConfigSupported` → only comments at `rdpgfx.js:1249-1269`). Safari therefore receives exactly the Chromium pipeline:

1. WS `binaryType="arraybuffer"` (`client.js:308`) → `proto.feed()` (`protocol.js:1242-1275`) → DVC reassembly `_dvcOnData` (`protocol.js` ~2500-2560; one `.slice()` per chunk + concat) → `RdpGfx.onChannelData` (`rdpgfx.js:474-495`) → JS ZGFX inflate (`zgfx.js:86-245`, bit-serial Huffman, 2.5 MB history) → `_dispatch` per PDU, **synchronously inside the WebSocket `onmessage`**. No `requestAnimationFrame` anywhere (grep: 0 hits in all client files).
2. **AVC420**: whole PDU copied (`rdpgfx.js:1092`) and transferred to `decode-worker.js`; worker parses metablock, feeds `VideoDecoder` (`decode-worker.js:242 configure({codec, optimizeForLatency:true})`, no `hardwareAcceleration`, no `isConfigSupported`), output `VideoFrame` transferred back (`decode-worker.js:262-265`). Main thread: `onDecodedFrame` (`rdpgfx.js:1207-1281`) does `surf.ctx.drawImage(frame, 0, 0, cw, ch)` onto a **`willReadFrequently:true` OffscreenCanvas** (`rdpgfx.js:640`), then `_afterSurfaceUpdate` with the **full coded-frame rect** (region rects ignored, `rdpgfx.js:1244-1257`) → `_paintSurface` → `client.js:1332-1342 this.ctx.drawImage(surfaceCanvas → visible canvas)`. So every H.264 frame = GPU VideoFrame → CPU readback+YUV→RGB → full-surface CPU→GPU upload.
3. **Progressive** (GNOME RD, or Windows when AVC is off): decoded in worker (pure JS RLGR/DWT), each tile `rgba.slice(0)` (`decode-worker.js:81`), sparse result = N transferred 16 KB buffers; main thread does **per tile** `new ImageData` + `putImageData` (`rdpgfx.js:947-952`) and, via `_afterSurfaceUpdate(regions)` (`rdpgfx.js:953, 1315-1338`), **one `drawImage(surface→output)` per tile**. 200 tiles ⇒ 200 putImageData + 200 drawImage per PDU.
4. **ClearCodec** (Windows UI chrome; Windows mixes it with AVC/Progressive): decoded **on the main thread** (`rdpgfx.js:1149-1169`), painted via `putImageData` to an alpha scratch canvas + `drawImage` to the surface (`rdpgfx.js:1188-1194`), then **`getImageData` of the surface rect** for every GLYPH_INDEX tile (`rdpgfx.js:1199-1202`).
5. Cache ops: SURFACE_TO_CACHE snapshots with `drawImage(surface→slot)` where slot canvases are **GPU-backed** (`rdpgfx.js:1451-1456`, no `willReadFrequently`) and CACHE_TO_SURFACE is `drawImage(slot→surface)` (`rdpgfx.js:1517`) — so each direction crosses the CPU/GPU boundary. Plus an **always-on `getImageData` scan** on every interior SURFACE_TO_CACHE (`rdpgfx.js:1458-1470`, comment: "This check runs ALWAYS (even in prod, flag off)").
6. END_FRAME: FRAME_ACK with `queueDepth=0` + QOE ack are sent **immediately when END_FRAME is parsed**, before any worker decode has landed (`rdpgfx.js:753-777`). The host never learns the client is behind.

## A.1 Ranked root-cause hypotheses

### H1 — Per-tile `drawImage` from a CPU-backed OffscreenCanvas into the GPU-backed visible canvas (progressive/ClearCodec/cache paths) — HIGH impact
Evidence: `_afterSurfaceUpdate` blits every region individually (`rdpgfx.js:1315-1338`); progressive sparse path calls it with one region per tile (`rdpgfx.js:947-953`); ClearCodec per PDU (`rdpgfx.js:1206`); SOLIDFILL per rect (`rdpgfx.js:1400`); CACHE_TO_SURFACE per dest point (`rdpgfx.js:1557`). The `_dirty` list intended for END_FRAME flushing is allocated (`rdpgfx.js:138, 742`) but never consumed. Source surface is `willReadFrequently:true` (`rdpgfx.js:640`) ⇒ CPU `ImageBuffer` in WebKit. WebKit's CoreGraphics backend materialises a `NativeImage` (CGImage) of the *whole* source buffer for a `drawImage` from an unaccelerated buffer; CG's bitmap-context images are copy-on-write, so the *next* `putImageData` into the same surface forces a full backing-store copy. Interleaving write→blit→write→blit per 64×64 tile can therefore cost a full-surface copy per tile: 2560×1440×4 = 14.7 MB × 200 tiles ≈ 3 GB memcpy per PDU (59 MB per copy at 2×). Chromium's Skia software canvas only touches the subrect, which matches "fine in Chromium, very slow in Safari".
Confirm: Web Inspector → Timelines → JavaScript & Events / CPU while scrolling a window; `_finishProgressive` self-time dominated by `drawImage`/`putImageData`. Better: Instruments Time Profiler on the `com.apple.WebKit.WebContent` process; look for `WebCore::ImageBuffer::copyNativeImage`, `CGBitmapContextCreateImage`, `memmove`/`memcpy`, `CGContextDrawImage`. Quick A/B in the console: `RdpGfx.prototype._afterSurfaceUpdate = function(id,s){s.touched=true; this._pendingRepaint=true;}` and call `gfx.repaintAll()` once per `_onEndFrame` (monkey-patch) — if fps jumps, H1 is confirmed.

### H2 — H.264 frame readback onto a CPU canvas + full-surface re-upload every frame (default avc420 mode) — HIGH impact
Evidence: `drawImage(VideoFrame)` into the `willReadFrequently` surface (`rdpgfx.js:1247-1257`, `640`), then `_afterSurfaceUpdate` with `{0,0,cw,ch}` (full frame, regions discarded) → whole-surface `drawImage` to output (`client.js:1339`). Per 2560×1440 frame that is ≈15 MB YUV→RGB readback + ≈15 MB upload on the main thread; at 30 fps ≈ 0.9 GB/s. On WebKit `VideoFrame` output from VideoToolbox is a `CVPixelBuffer`; drawing it into a software `ImageBuffer` goes through CoreImage/CG conversion, materially slower than Chromium's libyuv SIMD path. If `drawImage(VideoFrame)` throws (older Safari/iPadOS), the fallback is `createImageBitmap(frame)` per frame (`rdpgfx.js:1270-1281`) — an extra async hop and copy per frame.
Confirm: console shows `rdpgfx(worker): H264 configured codec=avc1.…`; Timelines shows `onDecodedFrame` time ≈ frame period; Instruments shows `CIContext`/`CVPixelBufferLockBaseAddress`/`vImage` under `drawImage`. A/B: set `window.RDP_GFX_MODE="progressive"` before connect (or dropdown) and compare.

### H3 — No frame pacing, no coalescing, no client backpressure ⇒ latency balloons instead of fps degrading — HIGH impact (perceived "very slow")
Evidence: everything paints synchronously inside WS `onmessage` (`client.js:327-336` → `protocol.js:1242`), no rAF, no frame dropping. FRAME_ACK `queueDepth=0` is sent at END_FRAME parse time (`rdpgfx.js:753-777`), before the worker has decoded (`_decodeSeq` vs `_decodeSettledSeq`, `rdpgfx.js:148-160`), so per [MS-RDPEGFX] 3.2.5.13 (`Spec/[MS-RDPEGFX].md:3543`, "server SHOULD use this value to … throttle the graphics frame rate") the host gets no throttle signal. QOE `timeDiffSE` is `Date.now()` START→END *parse* time, `timeDiffEDR=0` (`rdpgfx.js:797-809`). The gateway also cannot help: it forwards at the browser's pace into a 256×16 KB bounded channel and **fails the session** when full (`RDP/RdpRelaySession.cs:259-285`). On Safari, once the main thread runs behind (H1/H2), WS messages queue in the browser, every frame is still decoded and painted, and the picture lags by seconds — which users describe as "very slow" rather than "low fps".
Confirm: in console, sample `client.proto.gfx._decodeSeq - client.proto.gfx._decodeSettledSeq` and `performance.now()` between `_onEndFrame` and the matching `_finishProgressive`/`_paintH264Frame`; Web Inspector → Network → WebSocket frame timeline vs. paint events.

### H4 — Always-on canvas readbacks in production — MEDIUM
Evidence: `_scanBlack` `getImageData` on every interior SURFACE_TO_CACHE (`rdpgfx.js:1458-1470`, up to `w*h*4` bytes copied then ≤65 536 samples scanned); ClearCodec GLYPH_INDEX re-snapshot `getImageData` (`rdpgfx.js:1199-1202`); `_diagPaint` is gated (`rdpgfx.js:350-352`) but `_scanBlack` in SURFACE_TO_CACHE is not. Changelog 0.6.31 (`wwwroot/docs/admin/changelog.md:43-45, 49-55`) introduced both the `willReadFrequently` hint and the unconditional scan. Windows issues SURFACE_TO_CACHE heavily during scrolling/window moves, so on Safari this is a steady stream of multi-MB copies; if WebKit ignores `willReadFrequently` for OffscreenCanvas contexts, each is a GPU readback stall. The diag ring (`rdpgfx.js:239-246`) itself is cheap (string retention only; `_diagLog` is only called on gated paths — 51 `_log/_diagLog` sites total in rdpgfx.js).
Confirm: Timelines → look for `getImageData` under `_onSurfaceToCache`; Instruments `WebCore::ImageBuffer::getPixelBuffer`.

### H5 — Canvas count/memory and mixed backing pushes WebKit past its accelerated-canvas budget — MEDIUM (only at 2×/HiDPI)
Canvases: visible output (`client.js:59`, GPU), pointer-cache canvas (`client.js:61`), per-surface OffscreenCanvas (`rdpgfx.js:631-640`, CPU, `willReadFrequently`), orphan surfaces sized to full output (`rdpgfx.js:648-660`), ClearCodec scratch (grows to the largest Clear rect, `rdpgfx.js:1186-1191`), one GPU OffscreenCanvas per cache slot (`rdpgfx.js:1451-1456`, never evicted — EVICT_CACHE_ENTRY is not in `_dispatchNow`, `rdpgfx.js:541-563`; SMALL_CACHE advertised ⇒ up to 16 MB / 4 096 slots per `Spec/[MS-RDPEGFX].md:3817-3819`), camera canvas (`client.js:787`). At 2560×1440 CSS on a 2× panel with HiDPI on (5120×2880): output 59 MB + surface 59 MB + Clear scratch up to 59 MB + cache ≤16 MB + in-flight NV12 VideoFrames (22 MB each, decoder keeps several) ≈ 200-250 MB; at 1× ≈ 50-70 MB. WebKit silently drops canvases to unaccelerated buffers past its per-page budget, and HiDPI 2× is 4× the decode/blit work in every JS loop (`rdpgfx.js:1360-1366`, `decode-worker.js:81-90`). HiDPI defaults **off** (`Console.cshtml:501`, `client.js:166-172`) so this only bites users who enable it; there is no scale cap other than 8192 px (`client.js:178-179`).
Confirm: Web Inspector → Canvas tab lists contexts and memory cost; compare fps with HiDPI off.

### H6 — Main-thread JS decode of ClearCodec + JS ZGFX inflate + DVC copies — MEDIUM on Windows hosts
ClearCodec decodes on the main thread by design (`rdpgfx.js:1140-1149`, `decode-worker.js:4-6`); Windows sends a lot of it for UI chrome even in AVC mode. ZGFX inflate is bit-serial JS (`zgfx.js:86-97` `_getBits(1)` per prefix bit at `zgfx.js:190-194`) on the main thread for every GFX message; per DVC chunk there is a `.slice()` (`protocol.js` `_dvcOnData` `let chunk = r.bytes(...).slice()`) and a reassembly concat. `_dispatch` also `body.slice()`s every queued order-sensitive PDU (`rdpgfx.js:536`). JSC handles typed-array loops reasonably, so this is a multiplier, not the root cause.

### H7 — Input flood: one WebSocket send + forced layout per mousemove — LOW-MEDIUM
`handleMouseMove` (`client.js:1560-1565`) → `_canvasCoords` calls `getBoundingClientRect()` per event (`client.js:1517`) → `new MouseMoveEvent().serialize()` (new ArrayBuffer, `input/mouse.js:79-92`) → `proto.sendInputEvent` (new ByteWriter, `protocol.js:1954-1972`) → `socket.send`. No coalescing, no pointer events, no `passive` listeners (`client.js:1127-1133`). Safari on ProMotion delivers ~120 mousemove/s; each competes with the blocked main thread and the forced layout can trigger style recalcs while the canvas CSS size is being fitted.

### H8 — Audio scheduled from the (blocked) main thread — LOW
`_playPcm` de-interleaves every wave in JS on the main thread and schedules `BufferSource`s at `max(now, playhead)` (`client.js:620-660`); when the main thread stalls longer than a wave (~tens of ms) audio gaps and the playhead jumps. Mic uses the deprecated `ScriptProcessorNode(4096)` (`client.js:704-708`) whose callback runs on the main thread. Not a cause of slow video, but it makes stalls audible.

### H9 — Diagnostics: mostly gated, one unconditional — see H4. `FF()` tracer is gated on `RDP_LOG` (`client.js:31-39`); `_verbose()`/`_diag()` read `window.*` per PDU (cheap). `_censusPdu` gated (`rdpgfx.js:493`). Verdict: only the SURFACE_TO_CACHE scan and Clear glyph snapshot are unconditional production readbacks.

### H10 — WASM: only legacy RLE is WASM (`rle/build.sh:8`, `-O3`, no `-msimd128`, no threads); progressive/Clear/ZGFX/RLGR are JS. No SharedArrayBuffer/Atomics anywhere (grep 0 hits; `Program.cs` sets no COOP/COEP — grep `Cross-Origin` 0 hits), so no threads regardless. Not Safari-specific.

## A.2 Pitfall checklist (applies / doesn't, with citations)

| # | Pitfall | Status | Where |
|---|---|---|---|
| 1 | Codec selection gated on WebCodecs | **No.** CAPS_ADVERTISE built purely from `mode` (`rdpgfx.js:401-459`); v8..v11.3 with SMALL_CACHE/SCALEDMAP_DISABLE flags copied from macOS RD; no `isConfigSupported`; `VideoDecoder` existence checked only lazily at first keyframe *in the worker* (`decode-worker.js:216-219`). Codec string `avc1.PPCCLL` from SPS (`decode-worker.js:207-211`), Annex-B in-band (no `description`), `optimizeForLatency:true`, no `hardwareAcceleration`. If configure throws or `VideoDecoder` is missing in the worker: `unsupported=true`, every later PDU silently dropped (`decode-worker.js:253-257`) — **black/frozen desktop, no fallback, no renegotiation**. Windows hosts confirm v11.1 and stream AVC420 (or AVC444v2 at `rdpgfx.js:405`); with `progressive` mode Windows sends RemoteFX Progressive; GNOME RD always sends Progressive over WIRE_TO_SURFACE_2 (`rdpgfx.js:863-866`). Spec footnote `Spec/[MS-RDPEGFX].md:5686-5692`: Win11 24H2/25H2/Server 2025 treat 0x000B0101/0x000B0200 as v10.7 behaviour — the client comment's "v11.x is the AVC gate" (`rdpgfx.js:390-395`) is at odds with the spec note; worth re-verifying per host build. | `rdpgfx.js:390-459`, `decode-worker.js:214-249` |
| 2 | `willReadFrequently` / canvas backing | **Applies.** Every GFX surface CPU-backed (`rdpgfx.js:640`); cache slots GPU (`1456`); Clear scratch default (`1191`); output canvas default (`client.js:59`); camera `willReadFrequently` (`client.js:787`, legitimate). Mixed backing ⇒ readbacks/uploads on every cache op and every output blit. Memory estimate in H5. | |
| 3 | Pixel primitives per frame (200 tiles) | sparse path: 200 `new Uint8ClampedArray` + 200 `new ImageData` + 200 `putImageData` + 200 output `drawImage` (`rdpgfx.js:947-953`); non-sparse: 1 putImageData + 1 drawImage but a `bw*bh*4` composite alloc in the worker (`decode-worker.js:110-122`). Worker: 200 × `rgba.slice(0)` (`decode-worker.js:81`). `createImageBitmap` only on the Safari fallback path (`rdpgfx.js:1270-1281`, per frame). `getImageData`: Clear glyph store + SURFACE_TO_CACHE scan (H4). | |
| 4 | Threading | Progressive + H.264 in one dedicated worker (`rdpgfx.js:164-184`); ClearCodec, ZGFX, DVC/MCS parsing, all canvas ops, audio, input on main thread. Transfers used correctly (`rdpgfx.js:931-936`, `decode-worker.js:136-141`, `265`). No SAB/COOP/COEP. Worker `onerror` nulls `_worker` but never settles pending reqIds ⇒ barrier wedge (`rdpgfx.js:176-179` vs `570-600`). | |
| 5 | Frame pacing / acks | No rAF; no timer-based pacing; acks immediate with `queueDepth=0` (H3). QOE uses `Date.now()` (ms). quality-worker.js only measures RTT via its own WS (`quality-worker.js:45-118`) and does **not** feed back into codec/ack decisions. Safari's 30 fps rAF throttling is moot because rAF isn't used — but so is any vsync alignment. | `rdpgfx.js:753-809` |
| 6 | WASM vs JS hot loops | RLGR (`progressive.js:100-238`) uses a 5-byte `_bitsAt` reload per shift (`progressive.js:65-83`); DWT (`322-453`), YCbCr→RGB (`458-468`) fixed-point, no `Math.imul` (safe: operands < 2^15·2^17), no per-tile allocs except `renderCell` first use (`563-564`). Per-component `new BitStream` (`104`, `894-895`). ZGFX bit-serial (`zgfx.js:190-194`). Adequate; not the Safari differentiator. | |
| 7 | WebSocket | `binaryType` set (`client.js:308`, `quality-worker.js:46`). No `bufferedAmount` backpressure on send (`client.js:496-500`). Per-message: `new Uint8Array(e.data)` view (no copy), join copy only when a partial PDU is pending (`protocol.js:1252-1261`). Fastpath (legacy) copies twice (`protocol.js:1948-1949`, `client.js:1256`). | |
| 8 | Input | One send per mousemove, `getBoundingClientRect` per event, no passive, `preventDefault` on wheel (non-passive listener required — correct), keydown `preventDefault` even for unmapped keys (`client.js:1544`). No touch/pointer events at all. | `client.js:1127-1133, 1516-1600` |
| 9 | Audio | Web Audio `createBuffer`/`BufferSource` per wave on main thread; mic `ScriptProcessorNode`; no AudioWorklet. | `client.js:599-660, 694-730` |
| 10 | HiDPI | Off by default; when on, desktop = CSS×probed dpr with **no cap**; scale carried as `desktopScaleFactor` (`protocol.js:453-454`); output canvas CSS-fitted (`client.js:279-286`). Cursor scaled correctly (`client.js:197-204`). | `client.js:160-183, 238-260` |
| 11 | Diagnostics in prod | Unconditional: SURFACE_TO_CACHE black scan (`rdpgfx.js:1458-1470`); diag ring string retention (`rdpgfx.js:239-246`, bounded 2000). Gated: everything else. | |
| 12 | Memory | `VideoFrame.close()` discipline is correct on all paths (`rdpgfx.js:1210-1281`, `1283-1307`); `ImageBitmap.close()` ✓. Cache slots never evicted (EVICT unhandled). Progressive per-tile state 3×(cur+sign)×8 KB = 48 KB per 64×64 cell per surface, in worker (`progressive.js:545-556`): 2560×1440 ⇒ 900 cells ⇒ 43 MB, plus `cell.rgba` 16 KB each. Orphan surfaces sized to full output (`rdpgfx.js:655-657`). | |
| 13 | Server-side levers | Gateway is a byte relay (`RdpRelaySession.cs:259-355`); `RdpChannels.cs` only *decodes* GFX for recording (`RdpChannels.cs:489-570`, feeds `GfxProgressiveCompositor`). No CAPS rewriting, no throttling, no transcoding. libx264 only in offline `RecordingMuxService.cs:134,173,366,400` (ffmpeg CLI). Levers that exist cheaply: (a) rewrite the client's CAPS_ADVERTISE per User-Agent at the WS upgrade to force Progressive/Clear for WebKit (gateway already parses cmd 0x0012 at `RdpChannels.cs:543`); (b) delay/hold FRAME_ACK forwarding to pace the host (it tracks `GfxFrameId` at `RdpChannels.cs:566`) — crude but effective; (c) real live transcode (decode Progressive/AVC in C# → x264 → send as AVC420 to the browser) would require a real-time encoder in-process (ffmpeg pipe or a managed wrapper) and re-emitting GFX PDUs — large effort, and it would still hit H2 on Safari unless the browser render path is fixed first. | |

## A.3 Fixes, ordered by payoff / effort

1. **Coalesce output blits to END_FRAME (or one rAF) — highest payoff, small change.** In `_afterSurfaceUpdate` (`rdpgfx.js:1315-1318`) push regions to `this._dirty` per surface instead of calling `_paintSurface`; in `_onEndFrame` (`rdpgfx.js:753`) union the rects per surface and issue **one** `cb.onPaint` per surface (or per a handful of merged rects); also flush when H.264 frames land outside a frame (`_paintH264Frame`) via a rAF-scheduled flush. Keep `MAP_*` full blits. This removes the 200×-per-PDU CPU→GPU crossing (H1) and gives natural vsync alignment.
2. **Drop `willReadFrequently` from GFX surfaces (`rdpgfx.js:640`) and remove the unconditional `_scanBlack` in `_onSurfaceToCache` (`rdpgfx.js:1458-1470`)** — gate it behind `_diag()` like everything else. For the ClearCodec glyph re-snapshot (`rdpgfx.js:1199-1202`) keep the *decoded* composite instead of reading the surface back: composite the Clear result onto a copy of the previously painted rect maintained in JS (or read back only when `glyphEntry` is set and the rect is small — it is ≤1024 px by spec, `clear.js:5`), so no production `getImageData` remains. Then all surfaces are GPU-backed and `drawImage(surface→output)` and `drawImage(VideoFrame→surface)` stay on the GPU (fixes H2 too).
3. **H.264: paint the VideoFrame directly to the output canvas when the surface is mapped 1:1** (single surface at origin, the normal Windows case): in `onDecodedFrame` (`rdpgfx.js:1207`) if `outputMap[surfaceId]` exists and no other surface overlaps, `client.ctx.drawImage(frame, ox, oy)` and skip the intermediate surface entirely (the `_onGfxDirectFrame` hook already exists, `client.js:1345-1358`, `protocol.js:2424-2426`, and is unused). Also honour `regions` (`rdpgfx.js:1244`) to blit only changed rects.
4. **Real backpressure**: send FRAME_ACK when the frame's last decode has *settled* (hook `_decodeSettled`, `rdpgfx.js:570`) and report `queueDepth` = bytes of GFX data received but not yet painted (track in `onChannelData`); when the backlog exceeds N frames, drop-to-latest for AVC (skip painting stale VideoFrames — close them) and skip paints for progressive tiles superseded within the same frame set. This turns "seconds of lag" into "lower fps".
5. **Capability-driven codec selection before CAPS_ADVERTISE**: in `_initGfxDvc` (`protocol.js:2409`) await `VideoDecoder.isConfigSupported({codec:"avc1.640028", hardwareAcceleration:"prefer-hardware"})` (both in window and via a worker ping) and, if unsupported or the UA is WebKit and the surface path can't stay on GPU, fall back to `progressive` mode automatically. Buffer the DVC create-response until the probe resolves (it is async; send CREATE_RSP first, CAPS after). Add a "Auto" dropdown value that does this.
6. **Move ZGFX inflate + Clear decode into the worker** (Clear's glyph re-snapshot is the only blocker — see 2) so the main thread only does canvas ops.
7. **Input**: coalesce mousemove to one send per animation frame (keep last position; flush immediately on button/wheel), cache `getBoundingClientRect()` on resize, add `{passive:true}` for mousemove. Add `blur`/`visibilitychange` → release all pressed keys.
8. **Audio**: schedule waves via an `AudioWorklet` ring buffer (or at minimum keep a 60-100 ms jitter buffer so main-thread stalls don't gap); replace `ScriptProcessorNode`.
9. **Cap HiDPI**: allow `desktopScaleFactor ≤ 200` and cap the requested native resolution (e.g. ≤ 3840×2160) unless the user opts into "full native"; expose per-resource default.
10. **Fix `requestKeyframe`** (undefined, `rdpgfx.js:196, 1775`; `decode-worker.js:277`) — a `sendRefreshRect()` (`protocol.js:1990-1994`) is the obvious implementation; and handle `unsupported` by surfacing an error / auto-reconnect in `progressive` mode rather than a silent black desktop.
11. Server: add UA-aware default mode (Safari ⇒ progressive until 1-5 land), and optionally an ack-pacing knob in the relay as a stopgap.

---

# PART B — SPEC COMPLIANCE / FEATURE COVERAGE

## B.1 MS-RDPEGFX coverage

| PDU / feature | Status | Citation |
|---|---|---|
| CAPS_ADVERTISE | ✓ three fixed lists by mode; v8/8.1/10/10.2/10.3/10.4/10.7 + undocumented 11.1/11.3 (avc), 10.x AVC_DISABLED (progressive), v8+8.1 (clearcodec). Flags 0x02/0x20/0x82 copied from macOS RD. Not capability-probed. | `rdpgfx.js:401-459`, `76-80` |
| CAPS_CONFIRM | ✓ parsed, logged; result not used to adapt anything | `rdpgfx.js:602-610` |
| RESET_GRAPHICS | ✓ width/height; monitorCount/monitor defs ignored | `rdpgfx.js:614-622` |
| CREATE/DELETE_SURFACE | ✓ incl. orphan adopt; pixelFormat stored, alpha ignored (per 3.3.8) | `rdpgfx.js:662-712` |
| START/END_FRAME | ✓; END_FRAME → immediate FRAME_ACK(queueDepth=0)+QOE | `rdpgfx.js:738-777` |
| FRAME_ACKNOWLEDGE | ✓ sent, but `queueDepth` always QUEUE_DEPTH_UNAVAILABLE, `totalFramesDecoded` counts parsed END_FRAMEs; SUSPEND only via `window.RDP_GFX_SUSPEND` | `rdpgfx.js:779-789`; spec `[MS-RDPEGFX].md:1219-1225, 3543` |
| QOE_FRAME_ACKNOWLEDGE | ✓ per frame; timeDiffEDR=0 | `rdpgfx.js:794-809` |
| WIRE_TO_SURFACE_1 | ✓ AVC420, AVC444/AVC444v2 (stream 1 only), CLEARCODEC, UNCOMPRESSED; ✗ PLANAR 0x0A, ✗ CAVIDEO 0x03 (legacy RFX in GFX), ✗ ALPHA 0x0C → logged & skipped | `rdpgfx.js:814-861`, `93-96` |
| WIRE_TO_SURFACE_2 | ✓ CAPROGRESSIVE / V2 | `rdpgfx.js:867-910` |
| SOLID_FILL | ✓ alpha forced opaque (3.3.5.4) | `rdpgfx.js:1370-1400` |
| SURFACE_TO_SURFACE | ✓ multi-dest | `rdpgfx.js:1403-1428` |
| SURFACE_TO_CACHE / CACHE_TO_SURFACE | ✓; cacheKey ignored (slot-only) | `rdpgfx.js:1433-1557` |
| EVICT_CACHE_ENTRY | ✗ not dispatched (falls to "unhandled" log) → slots never freed | `rdpgfx.js:541-563`; spec 3.3.5.8 |
| CACHE_IMPORT_OFFER / REPLY | ✗ offer never sent (no keys kept); reply ignored → no cache reuse across reconnect | `rdpgfx.js:559`, `1436` |
| MAP_SURFACE_TO_OUTPUT | ✓ | `rdpgfx.js:714-722` |
| MAP_SURFACE_TO_SCALED_OUTPUT | parsed, **scaling ignored** in `_paintSurface`; mitigated by advertising SCALEDMAP_DISABLE on 10.7/11.x | `rdpgfx.js:724-736`, `1315-1338`, `420` |
| MAP_SURFACE_TO_WINDOW / SCALED_WINDOW | ✗ (RemoteApp unsupported) | `rdpgfx.js:59-61` |
| DELETE_ENCODING_CONTEXT | ignored (progressive ctx keyed by surface, so harmless) | `rdpgfx.js:560`, `decode-worker.js:55-57` |
| Codec: ClearCodec | ✓ glyph cache (4000 entries), residual, bands/VBar + short VBar caches, subcodecs Uncompressed/NSCodec/RLEX, CACHE_RESET | `clear.js:35-60, 71-164, 302-350, 358-500` |
| Codec: Progressive | ✓ SYNC/CONTEXT/REGION/TILE_SIMPLE/FIRST/UPGRADE, RLGR1 (RLGR3 path present but progressive always uses RLGR1), SRL+RAW upgrade, DWT reduce-extrapolate and legacy layouts, subband diffing, per-frame tile set | `progressive.js:28-42, 100-238, 258-320, 597-668, 707-969` |
| Codec: AVC420 | ✓ (WebCodecs); region rects and quant values ignored — whole frame painted | `rdpgfx.js:1244-1257`, `decode-worker.js:291-311` |
| Codec: AVC444 / v2 | partial: luma view only, chroma-444 stream discarded (LC=2 skipped) | `rdpgfx.js:1118-1136` |
| Codec: Planar (RLE/delta), Alpha | ✗ | — |
| Legacy RFX inside GFX (CAVIDEO) | ✗ | — |

## B.2 Legacy fast-path (GFX mode "off")

| Item | Status | Citation |
|---|---|---|
| Bitmap updates | ✓ 16bpp only (`rowDelta=width*2`, `rgb2rgba`); CS_CORE asks 16bpp in legacy, 24bpp in GFX; RLE via WASM (`-O3`, no SIMD); no bitmap cache; per rect 2 wasm allocs + 2 JS allocs + putImageData | `client.js:1267-1323`, `protocol.js:424`, `rle/build.sh:8` |
| Fragment reassembly FIRST/NEXT/LAST | ✓ | `client.js:1214-1239` |
| Fastpath compression flag | dropped (never requested) | `client.js:1241-1245` |
| Palette | ✗ dropped | `client.js:1265` |
| Pointer: COLOR / NEW (1/24/32bpp) / CACHED | ✓ via CSS cursor classes, cropped to opaque bounds | `client.js:1385-1400, 1407-1500`, `update/pointer.js` |
| Pointer: NULL / DEFAULT / POSITION / LARGE_POINTER | ✗ dispatched but no branch → cursor never hidden/reset; LARGE_POINTER (384×384) dropped although advertised | `client.js:1259-1265, 1385-1400`, `protocol.js:981-983` |
| Surface commands / frame markers | ✗ dropped, yet SURFACE_COMMANDS (0x12), BITMAP_CODECS (RemoteFX GUID), FRAME_ACKNOWLEDGE(2) are advertised in *both* capset lists incl. legacy → a RemoteFX-enabled host may stream surface bits we never render, and TS_FRAME_ACKNOWLEDGE_PDU is never sent | `protocol.js:984-1001, 1044-1052`, `client.js:1265` |
| Primary/secondary orders | correctly declined: `orderSupport[32]=0`, NEGOTIATEORDERSUPPORT set | `protocol.js:905-914` |
| Bitmap cache caps | Rev2 body byte-copied from mstsc (3 cell caches, PERSISTENT_KEYS_EXPECTED) — inert since orders are declined but misleading | `protocol.js:1025-1031` |
| MultifragmentUpdate | ✓ 0x0009482B | `protocol.js:966-970` |

## B.3 Input

| Item | Status | Citation |
|---|---|---|
| Scancode mapping | `e.code` → Set-1 scancode table of 89 entries; **arrows mapped to numpad codes 0x4B/0x4D/0x48/0x50 without EXTENDED** → NumLock-on sends digits; PrintScreen→0x46 (wrong); missing Insert/Delete/Home/End/PageUp/PageDown/NumpadEnter/NumpadDivide/ControlRight/AltRight/MetaLeft/MetaRight/ContextMenu/IntlBackslash/ScrollLock | `input/keymap.js:1-92 (88-91)`, spec `[MS-RDPBCGR].md:7069` |
| EXTENDED / EXTENDED1 flags | ✗ eventFlags only 0 or RELEASE (1 byte scancode) | `input/keyboard.js:7-38` |
| Unicode events | ✗ (cap UNICODE advertised) | `protocol.js:933` |
| Dead keys / IME / composition | ✗ no `compositionstart`/`input` handling; non-US layouts rely on KLID detection (`client.js:_detectKeyboardLayout`, refined via Keyboard API on Chromium only) | `client.js:96-100, 470-492` |
| AltGr / Win key | ✗ (no AltRight/Meta mapping) | keymap |
| Ctrl+Alt+Del | ✗ no helper (Console.cshtml has fullscreen only) | `Console.cshtml:340, 858-862` |
| Toggle-key sync | ✗ `sendInputSync` defined, never called; would send toggleFlags=0 | `protocol.js:1974-1982` |
| Focus-loss key-up flush | ✗ no blur/visibilitychange handler | `client.js:1127-1148` |
| Mouse buttons | 1-3 only; buttons 4/5 up-event degenerates to PTRFLAGS_MOVE; MOUSEX advertised but PTRXFLAGS never sent | `input/mouse.js:22-56`, `protocol.js:933` |
| Wheel | ✓ vertical+horizontal, sign/magnitude per 2.2.8.1.1.3.1.1.3 (two's-complement low byte), capped 120/event | `input/mouse.js:9-14, 58-77`, `client.js:1577-1595` |
| Relative mouse / pointer lock | ✗ | — |
| Touch / gestures / pen | ✗ (no touch/pointer listeners; mobile Safari gets nothing) | `client.js:1127-1133` |
| CS_CORE keyboardLayout/type | ✓ KLID detected; keyboardType=4, functionKeys=12 | `protocol.js:408-415, 931-940` |
| Key repeat | browser auto-repeat forwarded as repeated DOWN (acceptable) | `client.js:1541-1548` |

## B.4 Display

Dynamic resize ✓ single monitor via DISPLAYCONTROL_MONITOR_LAYOUT with settle guard (`protocol.js:2677-2745`, `client.js:1007-1022`); DISPLAYCONTROL caps parsed only for maxMonitors (`protocol.js:2657-2666`). Multi-monitor ✗ (RESET_GRAPHICS monitor list ignored). HiDPI ✓ via `desktopScaleFactor` 100..500 with `deviceScaleFactor=100` (`protocol.js:453-454`), dummy-resize step machine for legacy hosts (`client.js:1044-1097`). Fullscreen ✓ (`Console.cshtml:858-862`). Compositing/perf flags ✓ from popup (`Console.cshtml:755-763`). Output scaling: browser CSS fit only (`client.js:279-286`); MAP_SURFACE_TO_SCALED_OUTPUT not honoured.

## B.5 Clipboard (MS-RDPECLIP)

Caps: only `CB_USE_LONG_FORMAT_NAMES` (`cliprdr.js:176-180`); no STREAM_FILECLIP / CAN_LOCK / HUGE_FILE (spec `[MS-RDPECLIP].md:774-777`). Formats: CF_UNICODETEXT (+CF_TEXT fallback on read) only (`cliprdr.js:10, 205-206, 221-229`); no HTML/RTF/DIB/PNG, no FileGroupDescriptorW/FileContents/Lock. Short vs long format-name form negotiated correctly (`cliprdr.js:38-46, 92-108`). Chunking: `_sendOnChannel` splits at CHANNEL_CHUNK_LENGTH=1600 with FIRST/LAST flags (`protocol.js:2003, 2076-2085`) ✓; inbound reassembly ✓ (`protocol.js:2095-2115`). Browser side: remote→local via `onRemoteText` (async clipboard write needs a gesture on Safari — handled in Console UI, not audited here); local→remote requires explicit `setLocalText` (paste/refresh action) — no `paste` event capture in client.js. TEMP_DIRECTORY sent although file transfer is unsupported (`cliprdr.js:152-167`, harmless).

## B.6 Audio / video / camera

rdpsnd (`rdpsnd.js`): PCM only (44.1k/22.05k, 16-bit, 1-2ch, `30-39`), wVersion 6, HIGH_QUALITY quality mode, Training echo, WAVE + WAVE2, no UDP, no compressed formats (AAC/Opus/ADPCM) → higher bandwidth. Playback latency: back-to-back scheduling with underrun snap (`client.js:648-657`), no jitter buffer. audin ✓ PCM, v2, ScriptProcessor capture + linear resample (`audin.js:27-40`, `client.js:694-760`). RDPECAM: v2, NV12 raw only (`rdpecam.js:59-69`), frames produced by canvas `getImageData` + JS RGB→NV12 on a `setInterval` (`client.js:787-841`) — CPU heavy, no H.264/MJPG. MS-RDPEVOR ✗ (Video::Control/Data rejected unless `RDP_EVOR_ACCEPT`, `protocol.js:2318-2321`); MS-RDPEGT accepted but payloads dropped (`protocol.js:2306-2317, 2372-2384`). Legacy rdpsnd static channel + AUDIO_PLAYBACK_DVC both handled.

## B.7 DVC (MS-RDPEDYC)

Caps: responds min(server, 3) (`protocol.js:2255-2277`); v3 compressed DATA_FIRST/DATA (0x06/0x07) inflated via per-channel ZGFX-LITE context ✓ (`protocol.js:2228-2232`, `_dvcOnData`); Soft-Sync logged, never negotiated (correct — no multitransport). DATA_FIRST reassembly by total length ✓; `sp` width honoured; cbId echoed on responses ✓ (`protocol.js:2140-2160`). Outbound fragmentation at 1600 ✓ (`protocol.js:2162-2210`). CLOSE ✓ (`protocol.js:2554`). Create rejections use 0xC0000001 like mstsc (`protocol.js:2344-2351`). Channel-id reuse handled for DisplayControl (`protocol.js:2333-2341`). Static-channel MPPC compression of caps deliberately off (`protocol.js:2428-2436`). Bug: `_onDrdynvcData` assumes single-chunk CHANNEL_PDU (`protocol.js:2212-2214`) — fine while the host keeps drdynvc chunks ≤1600, which Windows does.

## B.8 Reconnect / resilience / decode ordering

Auto-reconnect with frame preservation ✓ (`Console.cshtml:597-660`, memory note), server redirection ✓ (`client.js:365-386`). `RdpGfx.reset()` clears surfaces/cache/ZGFX/worker state (`rdpgfx.js:369-383`). Ordered-decode barrier (`rdpgfx.js:148-160, 520-540, 570-600`) with re-entrancy guard and worker-side "always reply" (`decode-worker.js:325-345`) — the 0.6.31 black-cache wedge fix. Remaining holes: (1) worker `onerror` sets `_worker=null` without settling in-flight reqIds → queue wedges (`rdpgfx.js:176-179`); (2) H.264 `unsupported` is silent (`decode-worker.js:216-219, 253-257`); (3) `requestKeyframe` never defined → decoder-error recovery does nothing (`rdpgfx.js:196, 1775`, `decode-worker.js:277`); (4) no CACHE_IMPORT_OFFER on reconnect; (5) relay hard-fails the session at 4 MB of backlog (`RdpRelaySession.cs:270-285`).

## B.9 Concrete bugs (file:line → spec)

1. Arrow keys sent as numpad scancodes without FASTPATH_INPUT_KBDFLAGS_EXTENDED — `input/keymap.js:88-91`, `input/keyboard.js:7-17` → `[MS-RDPBCGR] 2.2.8.1.2.2.1` (`Spec/[MS-RDPBCGR].md:7069`). With NumLock on, remote receives 8/4/6/2.
2. PrintScreen mapped to 0x46 (`keymap.js:71`); Pause requires the EXTENDED1 CTRL+NUMLOCK sequence (`[MS-RDPBCGR].md:7070`) — not implemented.
3. Missing scancodes (Insert/Delete/Home/End/PgUp/PgDn/AltRight/ControlRight/Meta/NumpadEnter/NumpadDivide/IntlBackslash) — `keymap.js`; `handleKeyDown` swallows them with `preventDefault` (`client.js:1544`).
4. Mouse buttons 4/5: `MouseUpEvent` leaves `PTRFLAGS_MOVE` (`mouse.js:41-56`) and no PTRXFLAGS extended event despite INPUT_FLAG_MOUSEX (`protocol.js:933`) → `2.2.8.1.2.2.4`.
5. `sendInputSync` never called (`protocol.js:1979`); toggle state never synced (`2.2.8.1.2.2.5`).
6. `requestKeyframe` undefined → H.264 decoder errors freeze the picture (`rdpgfx.js:196,1775`; `decode-worker.js:277`).
7. H.264 unsupported/configure-failure → silent drop after caps already advertised AVC (`decode-worker.js:214-249`); no fallback.
8. Worker `onerror` → barrier wedge (`rdpgfx.js:176-179` vs `570-600`).
9. FRAME_ACK sent before decode/paint with `queueDepth=0` (`rdpgfx.js:753-789`) → `[MS-RDPEGFX] 3.2.5.13` throttling never engages; `totalFramesDecoded` is "frames parsed".
10. EVICT_CACHE_ENTRY unhandled (`rdpgfx.js:541-563`) → `3.3.5.8`; cache slot canvases leak until slot reuse.
11. AVC444 chroma stream discarded (`rdpgfx.js:1118-1136`) → `2.2.4.6`; "avc444" mode costs bandwidth for no gain.
12. AVC420 `regionRects` ignored; whole coded frame painted (`rdpgfx.js:1244-1257`) → `2.2.4.4` (stale pixels can be resurrected outside the region when the host mixes codecs — same class as the progressive clip fix at `decode-worker.js:70-76`).
13. LARGE_POINTER / PTR_NULL / PTR_DEFAULT / PTR_POSITION ignored (`client.js:1385-1400`) while LARGE_POINTER cap advertised (`protocol.js:981-983`) → `[MS-RDPBCGR] 2.2.9.1.2.1.x`.
14. SURFACE_COMMANDS + BITMAP_CODECS(RemoteFX) + FRAME_ACKNOWLEDGE advertised in legacy mode (`protocol.js:1044-1052`) but SURFCMDS dropped (`client.js:1265`) and TS_FRAME_ACKNOWLEDGE_PDU never sent → black screen on RemoteFX-capable hosts in "off" mode.
15. MAP_SURFACE_TO_SCALED_OUTPUT target size ignored (`rdpgfx.js:724-736, 1315-1338`) → `2.2.2.20`.
16. Legacy bitmap path assumes 16 bpp (`client.js:1269-1273`) — only safe because CS_CORE asks 16 bpp (`protocol.js:424`).
17. Unconditional `getImageData` scan in production (`rdpgfx.js:1458-1470`) — perf, not spec.
18. `_onDrdynvcData` assumes single-chunk CHANNEL_PDUs (`protocol.js:2212-2214`) → `[MS-RDPBCGR] 2.2.6.1` allows fragmentation.

## B.10 Prioritised gap list vs. Windows App web / Guacamole / Kasm / Citrix HTML5 / Horizon HTML Access

1. **Keyboard correctness** (extended keys, AltGr/Win, unicode/dead keys/IME, toggle sync, blur flush) — every peer has this; ZeroVDI currently cannot type arrows reliably. Blocks non-US users.
2. **Frame pacing + backpressure + Safari-safe render path** (Part A fixes 1-5) — Citrix/Horizon/Windows App adapt fps and codec to the client; ZeroVDI has a fixed pipeline and no capability probe.
3. **Pointer NULL/DEFAULT/POSITION/LARGE** and cursor hide while typing.
4. **Touch/pen/mobile** (pointer events, gestures, on-screen keyboard, pinch-zoom) — Guacamole/Kasm/Citrix/Horizon all support iPad; ZeroVDI has no touch path.
5. **Clipboard rich formats + file transfer** (HTML/RTF/image, FileGroupDescriptorW/FileContents/Lock, stream chunking) — Windows App web, Guacamole (via SFTP/RDPDR), Kasm, Citrix, Horizon all offer files.
6. **AVC444 chroma** (text sharpness parity with Windows App).
7. **Legacy surface commands / RemoteFX (or stop advertising them)** for "off" mode against RemoteFX hosts.
8. **Multi-monitor** (RESET_GRAPHICS monitor list, DISPLAYCONTROL multi-layout, MAP_SURFACE_TO_SCALED_OUTPUT).
9. **RDPDR** (drive/printer redirection) — Guacamole, Citrix, Horizon have it; ZeroVDI requests the rdpdr static channel but has no handler audited here.
10. **Audio quality/latency** (AAC/Opus formats, AudioWorklet, jitter buffer) and camera H.264/MJPG.
11. **Cache import on reconnect** (CACHE_IMPORT_OFFER) and EVICT handling — bandwidth after reconnect.
12. RemoteApp (MAP_SURFACE_TO_WINDOW) and MS-RDPEVOR video redirection — lower priority for a VDI desktop product.
