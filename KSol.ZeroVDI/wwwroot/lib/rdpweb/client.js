// Protocol/GFX diagnostic logging is OFF by default (set window.RDP_LOG = 1 in devtools to enable).
// console.error/console.warn for real failures always fire regardless of this flag.
// RDP_LOG = 2 additionally turns on verbose per-tile GFX decode tracing (ClearCodec/Progressive cache
// hits, region/tile headers, quant indices) — noisy, but the fastest way to chase a specific black or
// stale tile back to the PDU that produced it. Use 1 for normal diagnostic logging.
window.RDP_LOG = window.RDP_LOG || 0;

// Black-partial-frame diagnostics (see rdpgfx.js _diag/_diagPaint). Independent of RDP_LOG.
//   window.RDP_GFX_DIAG = 1  — after each ClearCodec/Progressive paint, scan the written region; if it
//                              came out black/transparent, log its geometry + a hex dump of the FULL
//                              codec message that produced it (so the exact bytes can be replayed).
//   window.RDP_GFX_DIAG = 2  — additionally dump EVERY ClearCodec/Progressive message (at receipt and
//                              after paint), black or not. Very noisy; use only when hunting a codec bug.
// Black-frame scanning also covers the blit/fill ops (SOLIDFILL / SURFACE_TO_SURFACE / CACHE_TO_SURFACE),
// so a black area spread by a fill or an empty cache slot — not just a codec decode — is caught too.
// Set in devtools with no reconnect needed (read live per PDU). Off by default (the scan reads back
// canvas pixels via getImageData, which isn't free).
// window.rdpDiagScan() — call anytime (even with the flag off) to scan every surface NOW and print an
// 8x8 black-cell grid per surface; use it to pin a stale black area that appeared before diag was on.
window.RDP_GFX_DIAG = window.RDP_GFX_DIAG || 0;

// ---- first-frame timing tracer (TEMP: chasing the "first frame only after mouse move" delay) ----
// One line per lifecycle milestone, gated behind RDP_LOG like the rest of the client's diagnostics
// (set window.RDP_LOG = 1 in devtools to enable). Each line carries a wall-clock time and Δms since
// connect start (t0, set in connect()) so interleaved logs are unambiguous. Remove once the delay is fixed.
Client_ffT0 = 0;
var Client_ffSeen = {};
function FF(tag, extra) {
    if (window.RDP_LOG < 1) return;
    var now = (typeof performance !== "undefined" ? performance.now() : Date.now());
    var dt = Client_ffT0 ? Math.round(now - Client_ffT0) : 0;
    var wall = new Date().toISOString().substr(11, 12); // HH:MM:SS.mmm
    console.log("rdp: [ff " + wall + "] +" + dt + "ms " + tag + (extra ? " " + extra : ""));
}
function FF_once(tag, extra) { if (Client_ffSeen[tag]) return; Client_ffSeen[tag] = 1; FF(tag, extra); }

// client.js — browser RDP client over the gateway WebSocket relay.
//
// Flow:
//   1. Open the WebSocket to /ws/rdp/{id}.
//   2. Send the VM credentials as the FIRST text frame (JSON). The gateway holds them only for the
//      CredSSP handshake and never persists them.
//   3. Wait for the gateway's control text frames: {"status":"connecting"|"ready"|"error",...}.
//      On "ready", the gateway has completed X.224 + TLS + CredSSP and is now relaying the decrypted
//      RDP byte stream. We then start the RdpProtocol handshake (MCS connect onward).
//   4. RdpProtocol drives the handshake; once the session is active, server fastpath update PDUs are
//      delivered to onUpdate() for bitmap/pointer rendering, and local input is serialized and sent
//      as fastpath input PDUs.
//
// Rendering and input serialization reuse the ported modules (update/*, input/*, rle/*, color.js).

function Client(websocketURL, canvasID) {
    this.websocketURL = websocketURL;
    this.canvas = document.getElementById(canvasID);
    this.ctx = this.canvas.getContext("2d");
    this.pointerCacheCanvas = document.getElementById("pointer-cache");
    this.pointerCacheCanvasCtx = this.pointerCacheCanvas.getContext("2d");
    // The RDP cursor is applied to the whole console area (#screen-wrap fills the viewport),
    // not just the canvas, so the letterbox margins around the canvas also show the remote
    // cursor. Overlays/dialogs (floatbar, clipboard, preflight, login) are separate elements
    // stacked above it and keep their own cursors. Falls back to the canvas if not present.
    this.cursorEl = document.getElementById("screen-wrap") || this.canvas;
    this.connected = false;     // input/render enabled (session active)
    this.pointerCache = {};
    this.proto = null;
    this.statusCb = null;       // optional (status, message) => void for the UI
    // Distinguishes an intentional teardown (user clicked Disconnect, or the host ended the session
    // gracefully via a logoff/restart PDU) from a network/protocol drop (WebSocket died, or a
    // non-graceful protocol close). On a drop we emit "reconnecting" instead of "closed" so the UI can
    // keep the last frame on screen and auto-retry; on an intentional close we emit "closed" as before.
    this._intentionalClose = false;

    // Connection-quality tracking runs entirely in quality-worker.js (own thread + own WebSocket to
    // /ws/rdp-quality/{sessionId}) so a busy main thread can't skew the RTT reading. The gateway's pongs
    // fold in its sampled gateway→host RTT and relayed-byte counter, so the published numbers cover the
    // WHOLE path: web → gateway → RDP host. The main thread only forwards results to the UI callback.
    this.qualityCb = null;      // optional ({rtt, browserRtt, hostRtt, kbps, level}) => void for the UI
    this._qualityWorker = null;
    this._gatewaySessionId = null; // from the gateway's "ready" control frame

    // Windows keyboard layout id (KLID) sent in the RDP handshake (CS_CORE + Input capset) so the
    // host loads the layout matching the user's physical keyboard instead of always US English.
    // Synchronous locale-based guess now; refined asynchronously via the Keyboard API (Chromium)
    // before connect() completes its gateway handshake.
    this.keyboardLayout = this._detectKeyboardLayout();
    this._refineKeyboardLayout();

    this.handleKeyDown = this.handleKeyDown.bind(this);
    this.handleKeyUp = this.handleKeyUp.bind(this);
    this.handleMouseMove = this.handleMouseMove.bind(this);
    this.handleMouseDown = this.handleMouseDown.bind(this);
    this.handleMouseUp = this.handleMouseUp.bind(this);
    this.handleWheel = this.handleWheel.bind(this);
    this.onUpdate = this.onUpdate.bind(this);
    this.deinitialize = this.deinitialize.bind(this);
}

Client.prototype.setStatusCallback = function (cb) { this.statusCb = cb; };
Client.prototype._status = function (status, message) { if (this.statusCb) this.statusCb(status, message); };

// ---- connection quality (end-to-end RTT + throughput, sampled in quality-worker.js) ----------------
// optional ({rtt, browserRtt, hostRtt, kbps, level}) => void for the top bar. rtt is the END-TO-END
// estimate (browser↔gateway measured by the worker + gateway↔host sampled by the relay); browserRtt/
// hostRtt carry the per-leg split; level is "good"|"fair"|"poor"|null.
Client.prototype.setQualityCallback = function (cb) { this.qualityCb = cb; };

// URL of quality-worker.js, resolved relative to THIS script (document.currentScript is only valid
// while the script is initially executing, so capture it at load time — same pattern as rdpgfx.js).
const RDP_QUALITY_WORKER_URL = (typeof document !== "undefined" && document.currentScript && document.currentScript.src)
    ? new URL("quality-worker.js", document.currentScript.src).toString()
    : "quality-worker.js";

Client.prototype._startQualityProbe = function () {
    // Needs the gateway session id from the "ready" frame; without it (old gateway) the indicator
    // simply stays hidden — nothing else depends on the quality channel.
    if (!this._gatewaySessionId) return;
    const self = this;
    if (!this._qualityWorker) {
        try {
            this._qualityWorker = new Worker(RDP_QUALITY_WORKER_URL);
        } catch (e) {
            console.warn("quality worker unavailable:", e);
            return;
        }
        this._qualityWorker.onmessage = function (e) { if (self.qualityCb) self.qualityCb(e.data); };
    }
    const url = new URL("/ws/rdp-quality/" + encodeURIComponent(this._gatewaySessionId), window.location.href);
    url.protocol = (window.location.protocol === "https:") ? "wss:" : "ws:";
    // "start" also rebinds after a server redirection, when the reconnected leg got a NEW session id.
    this._qualityWorker.postMessage({ type: "start", url: url.toString() });
};

Client.prototype._stopQualityProbe = function () {
    if (this._qualityWorker) {
        this._qualityWorker.terminate();
        this._qualityWorker = null;
    }
    this._gatewaySessionId = null;
};

// Sizes the canvas backing store to a desktop resolution that fills `wrapEl` in *device* pixels, so
// the remote desktop renders crisp and 1:1 on HiDPI displays (no browser upscaling/blur). The CSS
// size of the canvas is then set to the wrapper's CSS size so it fits the visible area exactly.
// Returns {width, height} in device pixels (the RDP desktop resolution to request).
//
// RDP desktop dimensions must be even (bitmap rows are 16bpp; widths are safest as multiples of 4),
// and are clamped to the [MS-RDPBCGR] valid range (200..8192 px per axis for typical hosts).
Client.prototype.chooseDesktopSize = function (wrapEl, hiDpi) {
    // Match the remote desktop resolution to the console panel size in DEVICE pixels (CSS × dpr), so the
    // host's framebuffer is 1:1 with the physical display — crisp, no browser upscaling. The display DPI
    // is ALSO carried as the RDP DesktopScaleFactor (see _scaleForSession / the CS_CORE handshake) so
    // Windows sizes its UI correctly: e.g. a 200% Retina panel gets a 2560x1606 desktop @ 200% scale.
    //
    // When hiDpi is off (the default — better performance), we ignore devicePixelRatio and request the
    // desktop at the panel's LOGICAL CSS size (scale factor 100). The framebuffer is then upscaled to
    // fit the panel by _fit(), trading crispness on HiDPI panels for streaming ~1/dpr² the pixels.
    // The flag is passed explicitly at connect (from the console popup) and remembered as _hiDpi so
    // later live resizes (requestResize/maybeResize, which call without it) reuse the session's choice.
    if (hiDpi === undefined) hiDpi = !!this._hiDpi;
    this._hiDpi = !!hiDpi;
    const dpr = hiDpi ? this.panelPixelRatio(wrapEl) : 1;
    const cssW = Math.max(1, Math.floor(wrapEl.clientWidth));
    const cssH = Math.max(1, Math.floor(wrapEl.clientHeight));
    let w = Math.round(cssW * dpr);
    let h = Math.round(cssH * dpr);
    w = Math.max(200, Math.min(8192, w - (w % 4)));
    h = Math.max(200, Math.min(8192, h - (h % 2)));
    // Remember the logical size we sized from so the DPI scale can be derived as native/logical (rather
    // than re-reading devicePixelRatio) — the ratio of the resolution we actually sent to the panel's
    // CSS size, which is self-consistent even after clamping/rounding.
    return { width: w, height: h, logicalWidth: cssW, logicalHeight: cssH };
};

// Derive the RDP DesktopScaleFactor from a chosen size: the ratio of the native (device-pixel)
// resolution to the logical (CSS) size, snapped to a legal scale. This keeps the scale consistent with
// the resolution actually applied instead of depending on a separate devicePixelRatio read.
Client.prototype.scaleForSize = function (size) {
    const ratio = (size && size.logicalWidth) ? (size.width / size.logicalWidth) : (window.devicePixelRatio || 1);
    return this.desktopScaleForDpr(ratio);
};

// The live native/logical ratio of the canvas: its backing-store (device-pixel) width divided by its
// on-screen CSS width. This is measured directly from the element after _fit() has laid it out, so it
// reflects the resolution actually in effect — never a hardcoded 2x or a separately-read
// devicePixelRatio (which can momentarily drift during resizes). Used to size the CSS cursor so a
// device-pixel cursor bitmap renders at its correct physical size on HiDPI panels.
Client.prototype.cursorScaleRatio = function () {
    const rect = this.canvas.getBoundingClientRect ? this.canvas.getBoundingClientRect() : null;
    const cssW = rect && rect.width ? rect.width : (parseFloat(this.canvas.style.width) || 0);
    if (this.canvas.width && cssW) return this.canvas.width / cssW;
    // Pre-layout fallback: derive from the captured session scale (native/logical baked at connect).
    if (this._sessionScale && this._sessionScale > 0) return this._sessionScale / 100;
    return 1;
};

// The device-pixel ratio to request the HiDPI desktop resolution at. Derived from the CANVAS's own
// native/logical ratio (cursorScaleRatio) whenever a session already exists — i.e. every live resize
// reuses the resolution the panel is ACTUALLY rendering at, instead of re-reading window.devicePixelRatio
// (which can momentarily read 1 mid-resize and send a spurious scale change that stalls the host). Only
// the very FIRST connect, before any canvas has been sized/laid out, is there no surface to measure —
// and even then the ratio is measured with a CSS resolution probe (probeDevicePixelRatio), not the raw
// devicePixelRatio property. The captured _sessionScale is preferred over the probe on reconnects so a
// reconnect stays on the session's established scale.
Client.prototype.panelPixelRatio = function (wrapEl) {
    // A canvas that a PRIOR connect actually sized and fitted is the ground truth: measure it directly.
    // Gate on _appliedW (set only by applyDesktopSize) — the <canvas> ships with a static placeholder
    // width/height in the markup, so `canvas.width` alone is truthy even on the very first connect and
    // would make us measure the 1:1 placeholder ratio instead of probing the real device-pixel ratio.
    if (this._appliedW && this.canvas.width && this.canvas.height) {
        const measured = this.cursorScaleRatio();
        if (measured > 0) return measured;
    }
    if (this._sessionScale && this._sessionScale > 0) return this._sessionScale / 100;
    // First connect: no canvas to measure yet. Probe the real device-pixel ratio via CSS instead of
    // trusting the window.devicePixelRatio property.
    return this.probeDevicePixelRatio();
};

// Measures the panel's device-pixel ratio with a CSS resolution media query instead of reading the
// window.devicePixelRatio property. `(resolution: <n>dppx)` matches when the display maps CSS pixels to
// device pixels at exactly that ratio, so a binary search over `(min-resolution)` converges on the true
// value — a pure CSS/DOM measurement that stays correct under browser/OS zoom and fractional scales
// where the devicePixelRatio property can be rounded or stale. Falls back to the property only if
// matchMedia is unavailable. Cached per instance: the ratio doesn't change within a session.
Client.prototype.probeDevicePixelRatio = function () {
    if (this._probedDpr) return this._probedDpr;
    const fallback = window.devicePixelRatio || 1;
    if (typeof window.matchMedia !== "function") return (this._probedDpr = fallback);

    // dppx == CSS px per device px inverse; a display at 200% reports resolution 2dppx. Bracket the
    // search around the property's hint but don't depend on its exact value.
    let lo = 0.5, hi = Math.max(2, Math.ceil(fallback) + 1);
    if (!window.matchMedia("(min-resolution: " + lo + "dppx)").matches) {
        return (this._probedDpr = fallback); // sub-0.5 dppx is implausible; trust the property
    }
    // 20 iterations resolves to < 0.00001 dppx — far finer than any scale we snap to.
    for (let i = 0; i < 20; i++) {
        const mid = (lo + hi) / 2;
        if (window.matchMedia("(min-resolution: " + mid + "dppx)").matches) lo = mid; else hi = mid;
    }
    const ratio = (lo + hi) / 2;
    // Guard against a degenerate probe (e.g. headless engines that don't honor resolution queries).
    return (this._probedDpr = (ratio > 0.25 ? ratio : fallback));
};

// Applies a chosen device-pixel size to the canvas backing store, and fits its on-screen size to the
// wrapper via CSS. Call before connecting (so the resolution is baked into the RDP handshake).
Client.prototype.applyDesktopSize = function (wrapEl, size) {
    this._wrapEl = wrapEl; // remembered for re-fitting after live resolution changes
    this._appliedW = size.width;   // last resolution we asked the server for (resize jitter guard)
    this._appliedH = size.height;
    // Capture the session DPI scale ONCE here, derived from this size's native/logical ratio. Everything
    // downstream (the CS_CORE handshake scale, the initial monitor layout, resizes) reuses this single
    // value so the session never re-reads devicePixelRatio mid-stream (which can momentarily read 1
    // during a window resize and send a spurious scale change that stalls the host).
    this._sessionScale = this.scaleForSize(size);
    this.canvas.width = size.width;
    this.canvas.height = size.height;
    this._fit(wrapEl);
};

// Fits the canvas on screen. The backing store is the host's framebuffer size (the device-pixel
// resolution from chooseDesktopSize). We scale that to FIT the wrapper while preserving aspect ratio
// (letterbox) so it always fills the panel regardless of devicePixelRatio — the old "cssSize =
// backingStore / dpr" only happened to fit on a 2x-DPR display and overflowed on 1x.
Client.prototype._fit = function (wrapEl) {
    const el = wrapEl || this._wrapEl;
    const availW = el ? Math.max(1, el.clientWidth) : (this.canvas.width / (window.devicePixelRatio || 1));
    const availH = el ? Math.max(1, el.clientHeight) : (this.canvas.height / (window.devicePixelRatio || 1));
    // Scale the framebuffer to fit inside the available CSS box, preserving aspect ratio.
    const scale = Math.min(availW / this.canvas.width, availH / this.canvas.height);
    this.canvas.style.width = Math.round(this.canvas.width * scale) + "px";
    this.canvas.style.height = Math.round(this.canvas.height * scale) + "px";
};

// creds = {user, password, domain, performanceFlags}
// performanceFlags (optional) is the RDP ExtendedInfoPacket performanceFlags value; omit for the
// default (best visual fidelity — font smoothing + desktop composition).
Client.prototype.connect = function (creds) {
    const self = this;
    this.creds = creds;
    Client_ffT0 = (typeof performance !== "undefined" ? performance.now() : Date.now());
    Client_ffSeen = {};
    FF("connect() called", "canvas " + this.canvas.width + "x" + this.canvas.height);

    // _sessionScale was captured in applyDesktopSize (derived from the chosen native/logical ratio);
    // fall back to a fresh derivation here in case connect() is ever called without it.
    if (!this._sessionScale) this._sessionScale = this.scaleForSize(null);

    const url = new URL(this.websocketURL, window.location.href);
    url.protocol = (window.location.protocol === "https:") ? "wss:" : "ws:";
    url.searchParams.set("width", this.canvas.width);
    url.searchParams.set("height", this.canvas.height);

    this.socket = new WebSocket(url.toString());
    this.socket.binaryType = "arraybuffer";
    this._handshakeDone = false;

    this.socket.onopen = function () {
        FF("ws open");
        // First frame: credentials JSON (text). SKIP this when the gateway already holds the
        // credentials (SSO auto-connect, window.RDP_AUTOCONNECT): the relay then bridges immediately
        // and would mis-read a credentials frame as RDP bytes. The browser still uses creds for the
        // inner RDP auto-logon (Client Info PDU) via _startProtocol.
        if (!window.RDP_AUTOCONNECT) {
            self.socket.send(JSON.stringify({
                user: creds.user,
                password: creds.password,
                domain: creds.domain || "",
            }));
        }
        self._status("connecting", "authenticating…");
    };

    this.socket.onmessage = function (e) {
        if (typeof e.data === "string") {
            // Status control frame ({status: ...}). Quality ping/pong lives on its own worker-owned
            // WebSocket (quality-worker.js), never on this session socket.
            self._onControlFrame(e.data);
            return;
        }
        // Binary: relayed RDP bytes.
        const bytes = (e.data instanceof ArrayBuffer) ? new Uint8Array(e.data) : new Uint8Array(e.data);
        FF_once("first RDP bytes from gateway", bytes.length + "B");
        if (self.proto) self.proto.feed(bytes);
    };

    this.socket.onerror = function (ev) {
        console.error("websocket error:", ev);
        self._status("error", "connection error");
    };

    this.socket.onclose = this.deinitialize;
};

Client.prototype._onControlFrame = function (text) {
    let msg;
    try { msg = JSON.parse(text); } catch (e) { console.warn("bad control frame:", text); return; }

    switch (msg.status) {
        case "connecting":
            this._status("connecting", msg.message || "connecting…");
            break;
        case "ready":
            FF("gateway 'ready' (relay bridging)", "proto=" + msg.selectedProtocol);
            // The gateway includes this tunnel's session id so the quality worker can open its own
            // /ws/rdp-quality/{sessionId} socket once the session goes active (_onActive).
            this._gatewaySessionId = msg.sessionId || null;
            // Which X.224 protocol the gateway negotiated with the host. Usually 2 (HYBRID/NLA); for hosts
            // that reject NLA (xrdp/Linux) the gateway falls back to 1 (SSL) and we must stamp that same
            // value into CS_CORE.serverSelectedProtocol. Default 2 for older gateways that omit the field.
            this._selectedProtocol = (typeof msg.selectedProtocol === "number") ? msg.selectedProtocol : 2;
            this._status("connecting", "negotiating session…");
            this._startProtocol();
            break;
        case "redirect": {
            // RDP Server Redirection: the host (e.g. GNOME Remote Desktop "Remote Login") handed the
            // session off to the real target. The gateway cached the routing token; reconnect the
            // WebSocket so it lands on the redirected session. Tear down the current protocol/socket and
            // start over with the same credentials.
            this._status("connecting", "redirecting…");
            this.proto = null;
            this.connected = false;
            this._handshakeDone = false;
            // CRITICAL: detach the OLD socket's handlers before closing it. Otherwise its (possibly late)
            // onclose=deinitialize fires after we've started the reconnect and tears down the NEW session.
            var old = this.socket;
            if (old) { old.onclose = null; old.onmessage = null; old.onerror = null; old.onopen = null;
                       try { old.close(); } catch (e) { /* ignore */ } }
            this.socket = null;
            // Reconnect on the next tick so the closing socket fully unwinds first.
            var self = this;
            setTimeout(function () { self.connect(self.creds); }, 100);
            break;
        }
        case "error":
            // The gateway reported a hard failure (auth rejected, host unreachable, relay setup failed).
            // This is a genuine error, not a mid-session drop — flag intentional so the follow-on socket
            // close → deinitialize emits "closed" (already handled by the "error" status), not
            // "reconnecting" (which would start a pointless retry loop against a failure that won't heal).
            this._intentionalClose = true;
            this._status("error", msg.message || "connection failed");
            try { this.socket.close(); } catch (e) { /* ignore */ }
            break;
        default:
            console.warn("unknown control status:", msg.status);
    }
};

// Maps a CSS devicePixelRatio to the nearest RDP DesktopScaleFactor (% per [MS-RDPEDISP], 100..500),
// so the remote Windows UI renders at a comfortable size on HiDPI displays instead of tiny-at-100%.
Client.prototype.desktopScaleForDpr = function (dpr) {
    const pct = Math.round((dpr || 1) * 100);
    // Snap to the values Windows commonly uses; clamp to the legal range.
    const allowed = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    let best = allowed[0], bestErr = Infinity;
    for (const a of allowed) { const e = Math.abs(a - pct); if (e < bestErr) { bestErr = e; best = a; } }
    return best;
};

// ---- keyboard layout detection --------------------------------------------------------------------
// The RDP host interprets our scancodes (physical key positions) through the keyboard layout we
// advertise in the handshake, so it must match the user's REAL keyboard or every non-US key is wrong
// (QWERTZ Y/Z swap, dead keys, umlauts). Browsers don't expose the OS layout directly; we combine:
//   1. window.RDP_KEYBOARD_LAYOUT — explicit override (a Windows KLID, e.g. 0x0407), wins outright.
//   2. navigator.languages — locale → KLID table (good proxy: UI language usually matches keyboard).
//   3. navigator.keyboard.getLayoutMap() (Chromium, async) — probes what characters a few physical
//      keys actually produce, correcting the family when UI language and keyboard disagree
//      (e.g. English-UI browser on a German QWERTZ keyboard).

// Windows KLIDs ([MS-LCID] / kbd layout ids) for a browser locale tag. Exact-tag entries first for
// regional keyboards that differ from the language default, then primary-language fallbacks.
const KLID_EXACT = {
    "en-gb": 0x0809, "en-ie": 0x1809, "en-ca": 0x0409, "en-au": 0x0409,
    "de-ch": 0x0807, "de-li": 0x0807,
    "fr-be": 0x080C, "fr-ca": 0x0C0C, "fr-ch": 0x100C,
    "it-ch": 0x0807,               // Swiss keyboards are QWERTZ (Swiss German covers it-CH hardware)
    "nl-be": 0x0813,
    "pt-br": 0x0416,
    "es-mx": 0x080A, "es-419": 0x080A, "es-ar": 0x080A, "es-cl": 0x080A, "es-co": 0x080A,
    "zh-tw": 0x0404, "zh-hk": 0x0404,
};
const KLID_LANG = {
    en: 0x0409, de: 0x0407, fr: 0x040C, it: 0x0410, es: 0x040A, pt: 0x0816, nl: 0x0413,
    sv: 0x041D, nb: 0x0414, nn: 0x0414, no: 0x0414, da: 0x0406, fi: 0x040B, is: 0x040F,
    pl: 0x0415, cs: 0x0405, sk: 0x041B, hu: 0x040E, ro: 0x0418, bg: 0x0402, hr: 0x041A,
    sl: 0x0424, sr: 0x081A, et: 0x0425, lv: 0x0426, lt: 0x0427, el: 0x0408, tr: 0x041F,
    ru: 0x0419, uk: 0x0422, he: 0x040D, ar: 0x0401, th: 0x041E, vi: 0x042A,
    ja: 0x0411, ko: 0x0412, zh: 0x0804,
};
function klidForLocale(tag) {
    const t = (tag || "").toLowerCase();
    return KLID_EXACT[t] || KLID_LANG[t.split("-")[0]] || 0;
}

// Synchronous best guess from the browser's language preferences (first tag that maps wins).
Client.prototype._detectKeyboardLayout = function () {
    if (window.RDP_KEYBOARD_LAYOUT) return window.RDP_KEYBOARD_LAYOUT >>> 0;
    const tags = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language];
    for (const tag of tags) {
        const klid = klidForLocale(tag);
        if (klid) return klid;
    }
    return 0x0409; // US English
};

// Async refinement (Chromium only): the Keyboard API reveals the character each PHYSICAL key
// produces under the OS layout, which identifies the layout family even when the browser UI
// language doesn't match the keyboard. Only overrides when the locale guess disagrees.
Client.prototype._refineKeyboardLayout = function () {
    if (window.RDP_KEYBOARD_LAYOUT) return;
    if (!(navigator.keyboard && navigator.keyboard.getLayoutMap)) return;
    const self = this;
    navigator.keyboard.getLayoutMap().then(function (map) {
        const keyY = map.get("KeyY"), keyQ = map.get("KeyQ"), semi = map.get("Semicolon");
        const current = self.keyboardLayout;
        let refined = 0;
        if (keyY === "z") {
            // QWERTZ family: keep a QWERTZ locale guess (German/Swiss/Czech/Hungarian…), else German.
            const qwertz = [0x0407, 0x0807, 0x0405, 0x041B, 0x040E, 0x0424, 0x041A];
            refined = qwertz.indexOf(current) >= 0 ? current : 0x0407;
        } else if (keyQ === "a") {
            // AZERTY family: keep a French-family guess, else French.
            const azerty = [0x040C, 0x080C];
            refined = azerty.indexOf(current) >= 0 ? current : 0x040C;
        } else if (semi) {
            // QWERTY variants: the Semicolon position carries a distinctive letter on many layouts.
            const bySemi = { "ò": 0x0410, "ñ": 0x040A, "ç": 0x0816, "ø": 0x0414, "æ": 0x0406, "ö": 0x041D };
            const hit = bySemi[semi];
            if (hit === 0x041D && (current === 0x040B || current === 0x041D)) refined = current; // sv/fi share hardware
            else if (hit === 0x0816 && current === 0x0416) refined = current;                    // pt-BR keeps ABNT2
            else if (hit === 0x040A && current === 0x080A) refined = current;                    // Latin American
            else if (hit) refined = hit;
        }
        if (refined && refined !== current) {
            self.keyboardLayout = refined;
            if (window.RDP_LOG >= 1) console.log("rdp: keyboard layout refined to 0x" + refined.toString(16));
        }
    }).catch(function () { /* keep the locale-based guess */ });
};

Client.prototype._startProtocol = function () {
    const self = this;
    const transport = {
        send: function (u8) {
            if (self.socket && self.socket.readyState === WebSocket.OPEN) {
                self.socket.send(u8);
            }
        },
    };
    this.proto = new RdpProtocol(transport, {
        username: this.creds.user,
        password: this.creds.password,
        domain: this.creds.domain || "",
        width: this.canvas.width,
        height: this.canvas.height,
        selectedProtocol: this._selectedProtocol ?? 2, // the protocol the gateway's X.224 negotiation selected (2=HYBRID/NLA, 1=SSL for xrdp)
        // width/height are DEVICE pixels (chooseDesktopSize × dpr) for a crisp 1:1 framebuffer, and the
        // display DPI is carried as the DesktopScaleFactor so the remote Windows UI is sized correctly on
        // HiDPI panels (native res @ 200%, not a tiny 100% desktop). deviceScaleFactor MUST be 100/140/180
        // ([MS-RDPBCGR] 2.2.1.3.2) or the host ignores scaling entirely — keep it 100 and express all DPI
        // via desktopScaleFactor (100..500).
        desktopScaleFactor: this._scaleForSession(),
        deviceScaleFactor: 100,
        keyboardLayout: this.keyboardLayout, // Windows KLID detected from the browser (see _detectKeyboardLayout)
        performanceFlags: this.creds.performanceFlags, // undefined → protocol default (best visuals)
        audio: !!this.audioEnabled,        // request the rdpsnd channel for remote sound
        microphone: !!this.microphoneEnabled, // accept the AUDIO_INPUT DVC for mic redirection
        camera: !!this.cameraEnabled,      // accept the RDPECAM DVCs for camera redirection
        clipboard: !!this.clipboardEnabled, // request the cliprdr channel for clipboard sync
    }, {
        onUpdate: this.onUpdate,
        onActive: function () { self._onActive(); },
        onError: function (m) { console.error("rdp:", m); self._status("error", m); },
        onClose: function (graceful, m) { self._onProtocolClose(graceful, m); },
        onLog: function (m) { if(window.RDP_LOG >= 1) console.log("rdp:", m); },
        onResize: function (w, h) { self._onRemoteResize(w, h); },
        onDisplayControlReady: function () { self._displayControlReady = true; self._applyInitialScale(); },
        onAudio: function (fmt, pcm) { self._playPcm(fmt, pcm); },
        onMicOpen: function (fmt) { self._startMicCapture(fmt); },
        onMicClose: function () { self._stopMicCapture(); },
        onCameraStart: function (mt) { self._startCameraCapture(mt); },
        onCameraStop: function () { self._stopCameraCapture(); },
        onCameraSampleNeeded: function (idx) { self._onCameraSampleNeeded(idx); },
        onClipboardText: function (text) { self._onRemoteClipboardText(text); },
        // RDPEGFX (H.264) rendering — only fires when the GFX path is on (?gfx=1). onGfxPaint blits a
        // region of a decoded surface canvas onto the output canvas; onGfxReset announces a new
        // desktop size from RESET_GRAPHICS.
        onGfxPaint: function (canvas, sx, sy, sw, sh, dx, dy) { self._onGfxPaint(canvas, sx, sy, sw, sh, dx, dy); },
        onGfxReset: function (w, h) { self._onGfxReset(w, h); },
        onGfxDirectFrame: function (frame, surfaceId, map) { self._onGfxDirectFrame(frame, surfaceId, map); },
    });
    this.proto.start();
    // Devtools helper: window.rdpDiagScan() scans every GFX surface for black regions on demand and logs
    // an 8x8 black-cell grid per surface (see RdpGfx.diagScanAll). Works even when RDP_GFX_DIAG is off and
    // regardless of when the black appeared, so a stale black area can still be pinned to a surface.
    if (typeof window !== "undefined") {
        const self2 = this;
        window.rdpDiagScan = function () {
            const gfx = self2.proto && self2.proto.gfx;
            if (!gfx) { console.log("rdp: no GFX session active"); return null; }
            return gfx.diagScanAll();
        };
    }
};

// ---- remote audio playback (Web Audio) -----------------------------------------------------------
// Enable/disable remote sound. Must be set before connect() so the rdpsnd channel is advertised.
Client.prototype.setAudioEnabled = function (on) { this.audioEnabled = !!on; };

// Mute/unmute playback at runtime. The rdpsnd channel and the AudioContext stay fully active; mute
// only zeroes the persistent output gain, so the host keeps streaming, the context never goes idle,
// and unmute takes effect on the next scheduled wave without needing a fresh user gesture.
Client.prototype.setMuted = function (muted) {
    this.muted = !!muted;
    if (this._audioGain) this._audioGain.gain.value = this.muted ? 0 : 1;
};

// Enable camera redirection. Must be set before connect() so the protocol accepts the host's RDPECAM
// dynamic channels (enumerator + per-device). The webcam stream is acquired lazily when the host
// actually starts a stream (_startCameraCapture), which is when the permission prompt appears.
Client.prototype.setCameraEnabled = function (on) { this.cameraEnabled = !!on; };

// Pause/resume sending camera frames at runtime without tearing down the channel: while paused we send
// a blanked (black) frame so the remote app sees a steady feed instead of stalling.
Client.prototype.setCameraPaused = function (paused) { this.cameraPaused = !!paused; };

// Enable microphone redirection. Must be set before connect() so the protocol accepts the host's
// AUDIO_INPUT dynamic channel (a host that opens the DVC but gets no capture PDUs can stall its audio
// set). The actual capture stream is acquired lazily when the host OPENs the channel (_startMicCapture).
Client.prototype.setMicrophoneEnabled = function (on) { this.microphoneEnabled = !!on; };

// Mute/unmute the mic at runtime without tearing down the channel: we simply stop sending captured
// PCM upstream (the host's capture device stays open and just receives silence-by-omission).
Client.prototype.setMicMuted = function (muted) { this.micMuted = !!muted; };

// Create (and resume) the AudioContext. Call from a user gesture (e.g. the connect click) so the
// browser's autoplay policy lets audio start; the first wave arrives well after the gesture ends.
Client.prototype.primeAudio = function () {
    if (!this.audioCtx) {
        const AC = window.AudioContext || window.webkitAudioContext;
        if (!AC) return null;
        this.audioCtx = new AC();
        this._audioTime = 0;
        // Persistent output gain — playback always routes through this. Mute sets gain to 0 instead of
        // dropping waves, so the context keeps an active node graph (browsers can suspend an idle
        // context, which then can't resume outside a user gesture) and unmute is instant.
        this._audioGain = this.audioCtx.createGain();
        this._audioGain.gain.value = this.muted ? 0 : 1;
        this._audioGain.connect(this.audioCtx.destination);
    }
    if (this.audioCtx.state === "suspended") this.audioCtx.resume();
    return this.audioCtx;
};

// Play one decoded PCM wave. fmt = {rate, bits, channels}; pcm = little-endian interleaved samples.
// Schedules buffers back-to-back on a shared timeline so consecutive waves play gaplessly.
Client.prototype._playPcm = function (fmt, pcm) {
    try {
        // Prime/keep the AudioContext alive even while muted: priming must happen so the context the
        // connect gesture unlocked stays usable, otherwise the first unmute happens outside a user
        // gesture and the browser's autoplay policy leaves the context suspended (silent until reconnect).
        // Mute is applied via the persistent gain node (set in setMuted), not by dropping waves here.
        const ctx = this.primeAudio();
        if (!ctx) return;

        const bytesPerSample = (fmt.bits / 8) || 2;
        const channels = fmt.channels || 2;
        const frameBytes = bytesPerSample * channels;
        const frames = Math.floor(pcm.length / frameBytes);
        if (frames <= 0) return;

        const buf = ctx.createBuffer(channels, frames, fmt.rate || 44100);
        const view = new DataView(pcm.buffer, pcm.byteOffset, pcm.byteLength);
        // De-interleave to float [-1, 1]. Only 16-bit and 8-bit PCM are produced by our negotiated
        // formats; 16-bit is the common case.
        for (let ch = 0; ch < channels; ch++) {
            const out = buf.getChannelData(ch);
            for (let i = 0; i < frames; i++) {
                const off = i * frameBytes + ch * bytesPerSample;
                if (bytesPerSample === 2) out[i] = view.getInt16(off, true) / 32768;
                else out[i] = (pcm[off] - 128) / 128; // 8-bit unsigned
            }
        }

        const src = ctx.createBufferSource();
        src.buffer = buf;
        src.connect(this._audioGain || ctx.destination);
        const now = ctx.currentTime;
        // Keep a running playhead so waves queue seamlessly; if we've fallen behind (underrun), jump
        // back to now to avoid an ever-growing latency.
        const start = Math.max(now, this._audioTime || now);
        src.start(start);
        this._audioTime = start + buf.duration;
    } catch (e) {
        console.warn("audio play error:", e);
    }
};

// ---- microphone capture (getUserMedia → PCM upstream) --------------------------------------------
// The host opened the AUDIO_INPUT channel and OPENed the capture device with `fmt` {rate, bits,
// channels}. Acquire the mic (prompts for permission on first use), then continuously convert
// captured audio to the requested PCM format and ship it to the server via proto.sendMicPcm().
//
// Capture runs through an AudioContext whose hardware sample rate is usually NOT the format's rate, so
// we linearly resample to fmt.rate and downmix to fmt.channels before quantizing to 16-bit LE PCM.
Client.prototype._startMicCapture = function (fmt) {
    const self = this;
    this._micFmt = fmt;
    if (this._micStream || this._micStarting) return; // already capturing / acquiring
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        console.warn("mic: getUserMedia unavailable (needs a secure context)");
        return;
    }
    this._micStarting = true;
    navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true }, video: false })
        .then(function (stream) {
            self._micStarting = false;
            if (!self.proto || !self.proto.micFormat()) { // host closed the channel while we prompted
                stream.getTracks().forEach(function (t) { t.stop(); });
                return;
            }
            self._micStream = stream;
            self._buildMicGraph(stream, self._micFmt);
        })
        .catch(function (err) {
            self._micStarting = false;
            console.warn("mic: permission/denied or no device:", err && err.name);
        });
};

// Wire the capture stream through a ScriptProcessor that emits resampled 16-bit PCM each callback.
Client.prototype._buildMicGraph = function (stream, fmt) {
    const self = this;
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    // A dedicated capture context (separate from playback) so its rate/lifecycle is independent.
    this._micCtx = new AC();
    const src = this._micCtx.createMediaStreamSource(stream);
    // 4096-frame buffer ≈ 85ms at 48kHz — low enough latency, large enough to be efficient. We resample
    // from the context's native rate to fmt.rate in the callback.
    const node = this._micCtx.createScriptProcessor(4096, 1, 1);
    this._micNode = node;
    this._micResampleFrac = 0; // fractional read position carried across callbacks for clean resampling

    node.onaudioprocess = function (e) {
        if (!self.proto || !self.proto.micFormat()) return; // channel closed
        let input = e.inputBuffer.getChannelData(0);          // mono capture (Float32, -1..1)
        // Muted → keep the stream flowing but silent. Dropping chunks entirely starves the host's
        // capture pipeline (it can freeze/repeat the last audio); instead resample a zeroed block so we
        // ship correctly-sized SILENT PCM at the same cadence as live audio.
        if (self.micMuted) input = new Float32Array(input.length);
        const inRate = self._micCtx.sampleRate;
        const pcm = self._resampleToPcm(input, inRate, fmt);
        if (pcm && pcm.length) self.proto.sendMicPcm(pcm);
    };
    src.connect(node);
    // ScriptProcessor only fires while connected to the destination; route through a muted gain so we
    // don't echo the mic to the local speakers.
    const sink = this._micCtx.createGain();
    sink.gain.value = 0;
    node.connect(sink);
    sink.connect(this._micCtx.destination);
};

// Linearly resample a mono Float32 block from `inRate` to fmt.rate and quantize to interleaved PCM in
// the format's bit depth (16-bit LE in practice). Mono input is duplicated to fmt.channels. The
// fractional read cursor is carried across calls (this._micResampleFrac) so block boundaries don't click.
Client.prototype._resampleToPcm = function (input, inRate, fmt) {
    const outRate = fmt.rate || inRate;
    const channels = fmt.channels || 1;
    const ratio = inRate / outRate;
    const inLen = input.length;
    // Number of output frames available from this block, continuing from the carried fractional cursor.
    let pos = this._micResampleFrac;
    const outFrames = Math.max(0, Math.floor((inLen - pos) / ratio));
    if (outFrames <= 0) { this._micResampleFrac = pos - inLen; return null; }

    const bytesPerSample = (fmt.bits === 8) ? 1 : 2;
    const out = new Uint8Array(outFrames * channels * bytesPerSample);
    const view = new DataView(out.buffer);
    let o = 0;
    for (let i = 0; i < outFrames; i++) {
        const idx = Math.floor(pos);
        const frac = pos - idx;
        const s0 = input[idx] || 0;
        const s1 = (idx + 1 < inLen) ? input[idx + 1] : s0;
        let sample = s0 + (s1 - s0) * frac; // linear interpolation
        if (sample > 1) sample = 1; else if (sample < -1) sample = -1;
        for (let ch = 0; ch < channels; ch++) {
            if (bytesPerSample === 2) { view.setInt16(o, (sample * 32767) | 0, true); o += 2; }
            else { out[o++] = (sample * 127 + 128) & 0xff; }
        }
        pos += ratio;
    }
    // Carry the leftover fractional position into the next block (subtract the consumed input length).
    this._micResampleFrac = pos - inLen;
    return out;
};

// Stop capturing and release the microphone (host closed the channel, or we disconnected).
Client.prototype._stopMicCapture = function () {
    if (this._micNode) { try { this._micNode.disconnect(); this._micNode.onaudioprocess = null; } catch (e) {} this._micNode = null; }
    if (this._micCtx) { try { this._micCtx.close(); } catch (e) {} this._micCtx = null; }
    if (this._micStream) { this._micStream.getTracks().forEach(function (t) { try { t.stop(); } catch (e) {} }); this._micStream = null; }
    this._micResampleFrac = 0;
};

// ---- camera capture (getUserMedia video → NV12 frames upstream) ----------------------------------
// The host started a camera stream at media type `mt` {width, height, fps}. We set up the capture
// canvas + frame loop IMMEDIATELY (so the remote app always gets a steady feed) and acquire the webcam
// in parallel. Until the webcam is live — or if it's paused, denied, or absent — the loop renders a
// placeholder frame (camera/permission icon + explanatory text) instead of the live video. Each frame
// is converted to NV12 so the host needs no decoder. The RDPECAM device handler pulls samples on demand
// (_onCameraSampleNeeded), answered with the latest frame.
Client.prototype._startCameraCapture = function (mt) {
    const self = this;
    this._camMt = mt;
    // Capture canvas + loop are independent of whether a webcam stream exists, so placeholder frames
    // can be produced from the very first SampleRequest. (Re)create only once.
    if (!this._camCanvas) {
        const canvas = document.createElement("canvas");
        canvas.width = mt.width; canvas.height = mt.height;
        this._camCanvas = canvas;
        this._camCtx = canvas.getContext("2d", { willReadFrequently: true });
        this._camLastFrame = null;
        const fps = Math.max(1, Math.min(30, mt.fps || 15));
        this._camTimer = setInterval(function () { self._captureCameraFrame(); }, Math.round(1000 / fps));
    }

    if (this._camStream || this._camStarting) return; // webcam already acquired / acquiring
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        this._camState = "nodevice";
        console.warn("camera: getUserMedia unavailable (needs a secure context)");
        return;
    }
    this._camStarting = true;
    this._camState = "requesting";
    const want = {
        video: { width: { ideal: mt.width }, height: { ideal: mt.height }, frameRate: { ideal: mt.fps || 30 } },
        audio: false,
    };
    navigator.mediaDevices.getUserMedia(want)
        .then(function (stream) {
            self._camStarting = false;
            if (!self.proto || !self.proto.cameraMediaType()) { // host stopped while we prompted
                stream.getTracks().forEach(function (t) { t.stop(); });
                return;
            }
            self._camStream = stream;
            const video = document.createElement("video");
            video.autoplay = true; video.muted = true; video.playsInline = true;
            video.srcObject = stream;
            video.play().catch(function () { /* autoplay should allow muted */ });
            self._camVideo = video;
            self._camState = "live";
            console.log("camera: capturing →", self._camMt.width + "x" + self._camMt.height + " (NV12)");
        })
        .catch(function (err) {
            self._camStarting = false;
            const name = err && err.name;
            // NotAllowedError = permission denied; NotFoundError/OverconstrainedError = no camera.
            self._camState = (name === "NotFoundError" || name === "OverconstrainedError" || name === "NotReadableError") ? "nodevice" : "denied";
            console.warn("camera: " + self._camState + " (" + name + ")");
        });
};

// Draw the current source (live video, or a placeholder when paused/denied/absent/not-ready) to the
// canvas and convert to NV12. Stored as the latest sample; if the host already asked, answer now.
Client.prototype._captureCameraFrame = function () {
    if (!this._camCtx) return;
    const cv = this._camCanvas;
    const live = this._camVideo && this._camVideo.videoWidth && !this.cameraPaused && this._camState === "live";
    if (live) {
        this._camCtx.drawImage(this._camVideo, 0, 0, cv.width, cv.height);
    } else {
        this._drawCameraPlaceholder(this._camCtx, cv.width, cv.height);
    }
    const img = this._camCtx.getImageData(0, 0, cv.width, cv.height);
    this._camLastFrame = this._rgbaToNv12(img.data, cv.width, cv.height);
    if (this._camSamplePending && this.proto) {
        this._camSamplePending = false;
        this.proto.sendCameraFrame(this._camLastFrame);
    }
};

// Render a placeholder frame explaining why there's no live video: a camera (or permission) glyph and
// a short message, centered on a dark background. The reason is taken from cameraPaused / _camState.
Client.prototype._drawCameraPlaceholder = function (ctx, w, h) {
    let icon = "📷";   // 📷 camera
    let title = "Camera off";
    let subtitle = "";
    if (this.cameraPaused) {
        icon = "🚫"; title = "Camera paused"; subtitle = "Resume from the toolbar to share video.";
    } else if (this._camState === "denied") {
        icon = "🔒"; title = "Camera permission needed"; // 🔒
        subtitle = "Allow camera access in your browser, then reconnect.";
    } else if (this._camState === "nodevice") {
        icon = "📷"; title = "No camera available"; subtitle = "No webcam was found on this device.";
    } else if (this._camState === "requesting") {
        icon = "📷"; title = "Starting camera…"; subtitle = "Waiting for permission.";
    }

    ctx.fillStyle = "#16181d";
    ctx.fillRect(0, 0, w, h);

    const cx = w / 2;
    const cy = h / 2;
    const unit = Math.min(w, h);
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillStyle = "#e6e6e6";
    ctx.font = Math.round(unit * 0.22) + "px sans-serif";
    ctx.fillText(icon, cx, cy - unit * 0.08);
    ctx.fillStyle = "#f0f0f0";
    ctx.font = "600 " + Math.round(unit * 0.07) + "px sans-serif";
    ctx.fillText(title, cx, cy + unit * 0.16);
    if (subtitle) {
        ctx.fillStyle = "#9aa0a6";
        ctx.font = Math.round(unit * 0.045) + "px sans-serif";
        ctx.fillText(subtitle, cx, cy + unit * 0.27);
    }
};

// Convert an RGBA buffer to NV12 (4:2:0): a full-resolution Y plane (w*h bytes) followed by an
// interleaved UV plane (w*h/2 bytes, one U and one V per 2x2 block). BT.601 limited-range coefficients,
// matching what webcams/RDP expect. Width and height should be even (our advertised sizes are).
Client.prototype._rgbaToNv12 = function (rgba, w, h) {
    const ySize = w * h;
    const out = new Uint8Array(ySize + (w * h) / 2);
    // Y plane.
    for (let j = 0, p = 0, q = 0; j < h; j++) {
        for (let i = 0; i < w; i++, p += 4, q++) {
            const r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
            out[q] = (((66 * r + 129 * g + 25 * b + 128) >> 8) + 16) & 0xff;
        }
    }
    // UV plane: sample one chroma pair per 2x2 block (average the 4 RGB samples).
    let uv = ySize;
    for (let j = 0; j < h; j += 2) {
        for (let i = 0; i < w; i += 2) {
            const idx = (j * w + i) * 4;
            const idxR = idx + 4;                  // pixel to the right
            const idxD = idx + w * 4;              // pixel below
            const idxDR = idxD + 4;                // pixel below-right
            const r = (rgba[idx] + rgba[idxR] + rgba[idxD] + rgba[idxDR]) >> 2;
            const g = (rgba[idx + 1] + rgba[idxR + 1] + rgba[idxD + 1] + rgba[idxDR + 1]) >> 2;
            const b = (rgba[idx + 2] + rgba[idxR + 2] + rgba[idxD + 2] + rgba[idxDR + 2]) >> 2;
            out[uv++] = (((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128) & 0xff; // U (Cb)
            out[uv++] = (((112 * r - 94 * g - 18 * b + 128) >> 8) + 128) & 0xff;  // V (Cr)
        }
    }
    return out;
};

// The RDPECAM device handler needs a frame. Answer with the latest captured frame if we have one;
// otherwise mark a pending request that the next captured frame will satisfy.
Client.prototype._onCameraSampleNeeded = function (idx) {
    if (this._camLastFrame && this.proto) {
        const frame = this._camLastFrame;
        this._camLastFrame = null; // one sample per captured frame (avoid resending a stale image)
        this.proto.sendCameraFrame(frame);
    } else {
        this._camSamplePending = true;
    }
};

// Stop capturing and release the webcam (host stopped the stream, or we disconnected).
Client.prototype._stopCameraCapture = function () {
    if (this._camTimer) { clearInterval(this._camTimer); this._camTimer = null; }
    if (this._camVideo) { try { this._camVideo.pause(); this._camVideo.srcObject = null; } catch (e) {} this._camVideo = null; }
    if (this._camStream) { this._camStream.getTracks().forEach(function (t) { try { t.stop(); } catch (e) {} }); this._camStream = null; }
    this._camCanvas = null; this._camCtx = null; this._camLastFrame = null;
    this._camSamplePending = false; this._camStarting = false; this._camState = null;
};

// ---- clipboard sync ------------------------------------------------------------------------------
// Browser autosync against navigator.clipboard proved unfeasible (it needs a secure context, an
// explicit user gesture for every read, and permission prompts that most environments deny). Instead
// the console drives a manual text box (overlay popup): the remote's copied text is surfaced into the
// box via the callback below, and the box's contents are pushed to the remote on demand.

// Enable/disable clipboard redirection. Set before connect() to advertise the cliprdr channel.
Client.prototype.setClipboardEnabled = function (on) { this.clipboardEnabled = !!on; };

// Register a callback that receives text the remote session copied. The console wires this to the
// clipboard overlay's textarea so the user can see/copy what the remote put on the clipboard.
Client.prototype.setRemoteClipboardCallback = function (cb) { this._remoteClipCb = cb; };

// Remote session copied text → surface it to the UI (the clipboard overlay textarea). We remember it
// as the last remote value so a subsequent "send to remote" of the unchanged text is suppressed.
Client.prototype._onRemoteClipboardText = function (text) {
    this._lastRemoteClip = text;
    if (this._remoteClipCb) this._remoteClipCb(text);
};

// Push the given text to the remote session so it can paste it. Called by the clipboard overlay's
// "send to remote" action. No-op when the text is unchanged from what the remote last sent us.
Client.prototype.sendClipboardText = function (text) {
    if (!this.proto || text == null) return;
    if (text === this._lastRemoteClip) return;
    this.proto.sendClipboardText(text);
};

// Resize the canvas backing store to a device-pixel size and re-fit it to the viewport. Setting
// canvas.width/height clears the canvas, so the server's repaint at the new size refills it.
Client.prototype._resizeCanvas = function (w, h) {
    if (this.canvas.width === w && this.canvas.height === h) {
        if (this._wrapEl) this._fit(this._wrapEl);
        return;
    }
    this._appliedW = w;
    this._appliedH = h;
    this.canvas.width = w;
    this.canvas.height = h;
    if (this._wrapEl) this._fit(this._wrapEl);
};

// Called when the remote desktop resolution changes via a Deactivation-Reactivation (hosts that
// reactivate). Idempotent with the immediate resize done in requestResize/maybeResize.
Client.prototype._onRemoteResize = function (w, h) {
    this._resizeCanvas(w, h);
};

// Requests a live resolution change to fill `wrapEl` at the current DPI. Falls back to a no-op (the
// caller may choose to reconnect) if Display Control isn't available on this host.
Client.prototype.requestResize = function (wrapEl) {
    if (!this.proto || !this.proto.canResize()) return false;
    const size = this.chooseDesktopSize(wrapEl);
    const sent = this.proto.sendMonitorLayout(size.width, size.height, this._scaleForSession(), 100);
    // This server applies the new resolution without a Deactivation-Reactivation, so resize the
    // canvas to the requested size now; the server's repaint refills it. (Idempotent if the host
    // does later reactivate via _onRemoteResize.) Use the protocol's clamped dimensions (not the
    // requested ones) so the canvas backing store exactly matches the server's desktop — otherwise
    // mouse coordinate scaling drifts after a resize.
    if (sent) this._resizeCanvas(this.proto.width, this.proto.height);
    return sent;
};

// Conservatively requests a live resolution change only on a GENUINE, settled viewport change. This
// guards against the spurious resize/reflow events that fire right after connect — sending a layout
// mid-stream changes the server's resolution and (on hosts that don't reactivate) stalls output.
// Conditions: Display Control available, session active for >1.5s, and the new size differs from the
// last-applied size by more than a small threshold (so rounding jitter never triggers a layout).
Client.prototype.maybeResize = function (wrapEl) {
    if (!this.proto || !this.proto.canResize()) return false;
    if (!this._activeSince || (performance.now() - this._activeSince) < 1500) return false;

    const size = this.chooseDesktopSize(wrapEl);
    const curW = this._appliedW || this.canvas.width;
    const curH = this._appliedH || this.canvas.height;
    const THRESH = 16; // px — ignore sub-threshold jitter from layout settles
    if (Math.abs(size.width - curW) < THRESH && Math.abs(size.height - curH) < THRESH) return false;

    const sent = this.proto.sendMonitorLayout(size.width, size.height, this._scaleForSession(), 100);
    // Use the protocol's clamped dimensions so the canvas matches the server's desktop exactly (keeps
    // mouse coordinate scaling accurate after a resize).
    if (sent) this._resizeCanvas(this.proto.width, this.proto.height);
    return sent;
};

// Applies the session's HiDPI scale at start. On the GFX path the scale already rides in the CS_CORE
// handshake (desktopScaleFactor optional tail — GNOME RD builds its initial monitor config from it,
// Windows applies it as the connect-time session DPI), so the session STARTS at the right scale. The
// single MONITOR_LAYOUT sent here repeats that same resolution+scale: it's the mstsc-style first
// layout some hosts gate their 2nd RESET_GRAPHICS / free-running GFX stream on (see sendMonitorLayout's
// first-layout exception). Hosts that fully reconfigure on ANY layout (GNOME RD tears down and
// re-negotiates its PipeWire stream per DISPLAYCONTROL_MONITOR_LAYOUT) rebuild an identical desktop —
// and rdpgfx keeps the last frame on screen through that (untouched-surface MAP blits are skipped),
// so there's no black gap while the host renegotiates.
Client.prototype._applyInitialScale = function () {
    if (this._initialScaleApplied) return;
    if (!this.proto || !this.proto.canResize()) return; // DisplayControl/active not ready; retried from _onActive / onDisplayControlReady
    // Branch on gfxEnabled (the session-wide GFX mode), NOT on this.proto.gfx: the RdpGfx instance is
    // only created when the host's GFX DVC create-request arrives, and that RACES onDisplayControlReady.
    // Branching on the instance let the multi-step dummy-resize sequence below run on GFX sessions
    // whenever Display Control came up first — its resolution bounces tore down the GFX surfaces
    // mid-init (black screen) and its layouts got coalesced/dropped by the host (session stuck at 100%).
    if (this.proto.gfxEnabled) {
        this._initialScaleApplied = true;
        this.proto.sendMonitorLayout(this.canvas.width, this.canvas.height, this._scaleForSession(), 100);
        return;
    }
    if (this._sessionScale <= 100) { this._initialScaleApplied = true; return; } // nothing to scale
    this._initialScaleApplied = true;
    // Remember the real target resolution; the dummy-resize sequence bounces off it.
    this._scaleW = this.canvas.width;
    this._scaleH = this.canvas.height;
    // Kick off the scale sequence (see _runScaleStep). Step 0 sends the target scale at the real
    // resolution (a scale-only change), which some hosts intermittently ignore — so the sequence then
    // performs a dummy resize to a different resolution and back, each step gated on the previous
    // Deactivation-Reactivation completing (signalled by _onActive), forcing the scale to take effect.
    this._scaleStep = 0;
    this._runScaleStep();
};

// Drives the initial-scale "dummy resize" as a step machine. Each layout the host accepts triggers a
// Deactivation-Reactivation; we advance to the next step only when that reactivation completes (a fresh
// _onActive) or, for hosts that apply in place without reactivating, after a short timeout fallback.
// This serialization is what makes the scale reliable: two layouts fired in the same tick get coalesced
// or dropped by the host, leaving the session at 100%.
Client.prototype._runScaleStep = function () {
    if (!this.proto || !this.proto.canResize()) return;
    const w = this._scaleW, h = this._scaleH, s = this._sessionScale;
    // Step plan: 0 = target scale @ real res; 1 = nudge res (+2) @ target scale; 2 = back to real res.
    let sent = false;
    if (this._scaleStep === 0) {
        sent = this.proto.sendMonitorLayout(w, h, s, 100);
    } else if (this._scaleStep === 1) {
        sent = this.proto.sendMonitorLayout(w + 2, h + 2, s, 100);
        if (sent) this._resizeCanvas(this.proto.width, this.proto.height);
    } else if (this._scaleStep === 2) {
        sent = this.proto.sendMonitorLayout(w, h, s, 100);
        if (sent) this._resizeCanvas(this.proto.width, this.proto.height);
    } else {
        return; // sequence complete
    }

    // Advance. If the host accepted the layout it will reactivate → _onActive calls _advanceScaleStep.
    // Always arm a timeout fallback too: a no-op send (sent=false) or an in-place apply (no reactivation)
    // won't produce an _onActive, so the timeout keeps the sequence moving / ends it. _advancedFrom
    // dedupes the two triggers (reactivation vs. timeout) racing on the same step.
    this._advancedFrom = this._scaleStep;
    clearTimeout(this._scaleStepTimer);
    this._scaleStepTimer = setTimeout(() => this._advanceScaleStep(), 600);
};

Client.prototype._advanceScaleStep = function () {
    if (this._scaleStep === undefined || this._scaleStep > 2) return;
    if (this._advancedFrom !== this._scaleStep) return; // already advanced past this step
    this._advancedFrom = -1;
    clearTimeout(this._scaleStepTimer);
    this._scaleStep += 1;
    this._runScaleStep();
};

// The fixed DPI scale for this session (captured at connect). Resizes must reuse it so they only ever
// change resolution, never scale — see _applyInitialScale for why re-reading dpr per resize is unsafe.
Client.prototype._scaleForSession = function () {
    if (this._sessionScale) return this._sessionScale;
    return this.desktopScaleForDpr(window.devicePixelRatio || 1);
};

Client.prototype._onActive = function () {
    if (this.connected) {
        // Reached again after a reactivation. If a scale-sequence step is in flight, this reactivation
        // is the host confirming it applied — advance to the next step. Otherwise Display Control may
        // only now be usable, so (re)try the initial scale.
        if (this._initialScaleApplied && this._scaleStep !== undefined && this._scaleStep <= 2) {
            this._advanceScaleStep();
        } else {
            this._applyInitialScale();
        }
        return;
    }
    this.connected = true;
    this._activeSince = performance.now(); // for maybeResize's settle guard
    FF("session ACTIVE (_onActive)");
    this._status("ready", null);
    this._startQualityProbe();
    // Display Control may have signalled ready before the session was ACTIVE; now canResize() is true.
    this._applyInitialScale();

    window.addEventListener("keydown", this.handleKeyDown);
    window.addEventListener("keyup", this.handleKeyUp);
    this.canvas.addEventListener("mousemove", this.handleMouseMove);
    this.canvas.addEventListener("mousedown", this.handleMouseDown);
    this.canvas.addEventListener("mouseup", this.handleMouseUp);
    this.canvas.addEventListener("contextmenu", this.handleMouseUp);
    this.canvas.addEventListener("wheel", this.handleWheel);
};

Client.prototype.deinitialize = function () {
    // During an RDP Server Redirection we intentionally close the socket and immediately reconnect; skip
    // the full teardown (which would flip the UI to "closed" and reset session state) — _onControlFrame's
    // "redirect" handler already reset the protocol and is about to call connect() again.
    if (this._redirecting) return;
    this._stopQualityProbe();
    window.removeEventListener("keydown", this.handleKeyDown);
    window.removeEventListener("keyup", this.handleKeyUp);
    this.canvas.removeEventListener("mousemove", this.handleMouseMove);
    this.canvas.removeEventListener("mousedown", this.handleMouseDown);
    this.canvas.removeEventListener("mouseup", this.handleMouseUp);
    this.canvas.removeEventListener("contextmenu", this.handleMouseUp);
    this.canvas.removeEventListener("wheel", this.handleWheel);

    // Stop any in-flight initial-scale sequence and reset its state so a reconnect on this same Client
    // re-applies the DPI scale from scratch (otherwise _initialScaleApplied stays set and the reconnected
    // session is left at 100%).
    clearTimeout(this._scaleStepTimer);
    this._initialScaleApplied = false;
    this._scaleStep = undefined;
    this._sessionScale = 0;

    this.connected = false;

    Object.entries(this.pointerCache).forEach(([index, style]) => {
        document.getElementsByTagName("head")[0].removeChild(style);
    });
    this.pointerCache = {};
    this._setCursorClass(null);

    // Tear down the audio timeline so a later reconnect starts fresh (the AudioContext is reused).
    this._audioTime = 0;

    // Release the microphone if we were capturing (stops the OS "in use" indicator on disconnect).
    this._stopMicCapture();
    // Release the webcam too (stops the camera light/in-use indicator).
    this._stopCameraCapture();

    // Two ways a session ends:
    //  - Intentional (user Disconnect, or the host ended it gracefully via a logoff/restart/admin PDU):
    //    the session is truly over → emit "closed" and let the UI reset to the login/reconnect overlay.
    //  - Network/protocol drop (WebSocket died mid-session, or a non-graceful protocol close): the
    //    desktop is probably still alive → emit "reconnecting" so the UI keeps the last frame and starts
    //    its own countdown + auto-retry loop. It re-drives connect() on this same Client to reconnect.
    if (this._intentionalClose) {
        // Surface why the session ended (host logoff/disconnect reason) if the protocol gave us one; the
        // UI shows it on the login form. Cleared after so a later manual reconnect/close starts clean.
        this._status("closed", this._closeReason || null);
    } else {
        this._status("reconnecting", this._closeReason || null);
    }
    this._intentionalClose = false;
    this._closeReason = null;
    this._protocolClosed = false;
};

// ---- fastpath update rendering -------------------------------------------------------------------
// A single fastpath PDU may carry multiple updates; walk them all.
Client.prototype.onUpdate = function (arrayBuffer) {
    if (!this.connected) return;
    const r = new BinaryReader(arrayBuffer);
    const total = arrayBuffer.byteLength;

    while (r.offset < total) {
        const header = parseUpdateHeader(r);
        const bodyStart = r.offset;
        if (bodyStart + header.size > total) break; // truncated; shouldn't happen for valid PDUs
        const body = new Uint8Array(arrayBuffer, bodyStart, header.size);

        // Fragmentation ([MS-RDPBCGR] 2.2.9.1.2.1): a large update is split across PDUs as
        // FIRST → NEXT* → LAST; SINGLE is a self-contained update. Reassemble the payload by
        // updateCode before dispatching, otherwise large screen redraws never render.
        let updateCode = header.updateCode;
        let payload;
        if (header.isSingleFragment()) {
            payload = body;
        } else if (header.isFirstFragment()) {
            this._frag = { code: updateCode, parts: [body.slice()], len: body.length };
            r.offset = bodyStart + header.size;
            continue;
        } else { // NEXT or LAST
            if (!this._frag) { r.offset = bodyStart + header.size; continue; }
            this._frag.parts.push(body.slice());
            this._frag.len += body.length;
            if (header.isLastFragment()) {
                payload = new Uint8Array(this._frag.len);
                let off = 0;
                for (const part of this._frag.parts) { payload.set(part, off); off += part.length; }
                updateCode = this._frag.code;
                this._frag = null;
            } else {
                r.offset = bodyStart + header.size;
                continue; // more fragments to come
            }
        }

        if (header.compression === FASTPATH_OUTPUT_COMPRESSION_USED) {
            // Fastpath-level wrapper compression is not supported; skip.
            r.offset = bodyStart + header.size;
            continue;
        }

        try {
            this._dispatchUpdate(updateCode, header, payload);
        } catch (e) {
            console.warn("update render error:", e);
        }
        r.offset = bodyStart + header.size;
    }
};

// Dispatch a fully-reassembled update payload by its update code.
Client.prototype._dispatchUpdate = function (updateCode, header, payload) {
    const r = new BinaryReader(payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.byteLength));
    if (updateCode === FASTPATH_UPDATETYPE_BITMAP) {
        this.handleBitmap(r);
    } else if (updateCode === FASTPATH_UPDATETYPE_PTR_NULL || updateCode === FASTPATH_UPDATETYPE_PTR_DEFAULT
        || updateCode === FASTPATH_UPDATETYPE_PTR_POSITION || updateCode === FASTPATH_UPDATETYPE_PTR_COLOR
        || updateCode === FASTPATH_UPDATETYPE_PTR_CACHED || updateCode === FASTPATH_UPDATETYPE_PTR_NEW
        || updateCode === FASTPATH_UPDATETYPE_LARGE_POINTER) {
        // Rebuild a header carrying this updateCode for the pointer handler's is*() checks.
        const h = new UpdateHeader();
        h.updateCode = updateCode;
        this.handlePointer(h, r);
    }
    // SYNCHRONIZE / ORDERS / SURFCMDS / PALETTE: not rendered in v1.
};

Client.prototype.handleBitmap = function (r) {
    const bitmap = parseBitmapUpdate(r);

    bitmap.rectangles.forEach((bitmapData) => {
        const size = bitmapData.width * bitmapData.height;
        const rowDelta = bitmapData.width * 2;
        const resultSize = size * 2;

        if (!bitmapData.isCompressed()) {
            let rgb = new Uint8ClampedArray(bitmapData.bitmapDataStream);
            let rgba = new Uint8ClampedArray(size * 4);
            flipV(rgb, bitmapData.width, bitmapData.height);
            rgb2rgba(rgb, resultSize, rgba);
            this.ctx.putImageData(new ImageData(rgba, bitmapData.width, bitmapData.height), bitmapData.destLeft, bitmapData.destTop);
            return;
        }

        // Compressed (interleaved RLE) — decompress via the wasm module. Background/FgBg runs on the
        // FIRST scanline read the "previous row" at (pbDest - rowDelta), i.e. `rowDelta` bytes BEFORE
        // the destination. Allocate a one-row zero pad in front and decode into outputPtr = padded +
        // rowDelta so that prior-row read is guaranteed zero (the MS RLE reference's implicit all-zero
        // first previous-line).
        const srcBytes = new Uint8Array(bitmapData.bitmapDataStream);
        const inputPtr = Module._malloc(srcBytes.length);
        const padded = Module._malloc(resultSize + rowDelta);
        const outputPtr = padded + rowDelta;
        new Uint8Array(Module.HEAPU8.buffer, inputPtr, srcBytes.length).set(srcBytes);
        new Uint8Array(Module.HEAPU8.buffer, padded, resultSize + rowDelta).fill(0);

        const ok = Module.ccall("RleDecompress", "number",
            ["number", "number", "number", "number"],
            // srcBytes.length, NOT bitmapData.bitmapLength: when a TS_CD_HEADER is present,
            // bitmapLength includes its 8 bytes and would make the decoder run past the input.
            [inputPtr, srcBytes.length, outputPtr, rowDelta]);
        if (!ok) {
            Module._free(inputPtr);
            Module._free(padded);
            return;
        }

        let rgb = new Uint8ClampedArray(resultSize);
        rgb.set(new Uint8Array(Module.HEAPU8.buffer, outputPtr, resultSize));
        let rgba = new Uint8ClampedArray(size * 4);
        flipV(rgb, bitmapData.width, bitmapData.height);
        rgb2rgba(rgb, resultSize, rgba);
        this.ctx.putImageData(new ImageData(rgba, bitmapData.width, bitmapData.height), bitmapData.destLeft, bitmapData.destTop);

        Module._free(inputPtr);
        Module._free(padded);
    });
};

// ---- RDPEGFX (H.264) rendering -------------------------------------------------------------------
// Blit a region of a decoded GFX surface canvas onto the output canvas. The surface canvas holds the
// host's decoded picture in surface coordinates; (dx,dy) is where that region maps onto the desktop
// (MAP_SURFACE_TO_OUTPUT origin + the region offset). drawImage handles the OffscreenCanvas source.
Client.prototype._onGfxPaint = function (canvas, sx, sy, sw, sh, dx, dy) {
    if (sw <= 0 || sh <= 0) return;
    FF_once("first GFX paint", sw + "x" + sh + " @(" + dx + "," + dy + ")");
    try {
        // The source GFX surface canvas is opaque-backed (see _onCreateSurface's getContext alpha:false),
        // so this source-over drawImage fully replaces the destination rect — no alpha bleed-through of
        // the previous output frame (per [MS-RDPEGFX], surfaces mapped to output are opaque; alpha ignored).
        this.ctx.drawImage(canvas, sx, sy, sw, sh, dx, dy, sw, sh);
    } catch (e) {
        console.warn("gfx paint failed:", e);
    }
};

// DEBUG: draw a decoded VideoFrame STRAIGHT onto the visible output canvas (real DOM canvas), trying
// both drawImage(frame) and a bitmap fallback, and report what the visible canvas holds afterward.
Client.prototype._onGfxDirectFrame = function (frame, surfaceId, map) {
    const self = this;
    const ox = (map && map.originX) || 0, oy = (map && map.originY) || 0;
    FF_once("first GFX direct frame", "surf=" + surfaceId);
    try {
        this.ctx.drawImage(frame, ox, oy);
        if (frame.close) frame.close();
    } catch (e) {
        console.warn("DIRECT drawImage(frame) threw:", e);
        if (frame.close) frame.close();
    }
};

// RESET_GRAPHICS announced a new desktop size. If it differs from the current canvas backing store,
// resize the canvas (and re-fit CSS) so the GFX surfaces map 1:1 to output pixels.
Client.prototype._onGfxReset = function (w, h) {
    if (!w || !h) return;
    if (this.canvas.width !== w || this.canvas.height !== h) {
        // Setting canvas.width/height clears the ENTIRE backing store (HTML spec), even though only the
        // dimensions changed — the previously-composited desktop is now gone from the output canvas.
        // The GFX surfaces (protocol.js's RdpGfx) still hold the real decoded content, so repaint them
        // in full afterward or every static area (anything not immediately re-sent by the host) shows as
        // black until it happens to get its own fresh update.
        this.canvas.width = w;
        this.canvas.height = h;
        if (this._wrapEl) this._fit(this._wrapEl);
        if (this.proto && this.proto.gfx) this.proto.gfx.repaintAll();
    }
};

// Gated pointer-update tracing. Off unless window.RDP_LOG >= 1 (see top of file), so the
// hot path stays quiet in production but pointer-cache issues (e.g. reverting to the OS
// default cursor) can be diagnosed by flipping the flag in devtools.
function PTR_LOG(msg) { if (window.RDP_LOG >= 1) console.log("rdp: pointer " + msg); }

// Select the active RDP cursor by swapping the single pointer-cache-* class on cursorEl.
// Only that class is touched, so any other classes on the element are preserved. Pass null
// to clear the cursor (revert to whatever the element's own CSS specifies).
Client.prototype._setCursorClass = function (cls) {
    const el = this.cursorEl;
    for (const c of Array.from(el.classList)) {
        if (c.indexOf("pointer-cache-") === 0) el.classList.remove(c);
    }
    if (cls) el.classList.add(cls);
};

Client.prototype.handlePointer = function (header, r) {
    if (header.isPTRNull()) { PTR_LOG("PTR_NULL"); this._setCursorClass("pointer-cache-null"); return; }
    if (header.isPTRDefault()) { PTR_LOG("PTR_DEFAULT -> OS default cursor"); this._setCursorClass("pointer-cache-default"); return; }

    // PTR_COLOR and PTR_NEW carry the same cursor bitmap (PTR_COLOR is implicitly 24-bpp);
    // both must be cached so later PTR_CACHED references resolve. Dropping PTR_COLOR left
    // its cache slot empty, so a subsequent PTR_CACHED to that index reverted to the OS
    // default cursor.
    if (header.isPTRColor()) { this._cachePointer(parseColorPointerUpdate(r), "PTR_COLOR"); return; }
    if (header.isPTRNew()) { this._cachePointer(parseNewPointerUpdate(r), "PTR_NEW"); return; }

    if (header.isPTRCached()) {
        const cacheIndex = r.uint16(true);
        if (!this.pointerCache.hasOwnProperty(cacheIndex)) {
            // Referenced a slot we never built (unsupported/failed decode). Keeping the
            // current cursor is less jarring than snapping to the OS default.
            PTR_LOG("PTR_CACHED miss idx=" + cacheIndex + " (keeping current cursor)");
            return;
        }
        PTR_LOG("PTR_CACHED idx=" + cacheIndex);
        this._setCursorClass("pointer-cache-" + cacheIndex);
        return;
    }
    // PTR_POSITION / large pointer: not handled in v1.
    PTR_LOG("unhandled pointer update");
};

// Crop an ImageData to the bounding box of its non-transparent pixels. RDP cursor bitmaps
// are fixed-size frames (32x32/96x96) that are mostly transparent padding; browsers revert
// a custom CSS cursor to the OS default whenever the cursor IMAGE would extend past the
// viewport edge (anti-cursor-spoofing), so the padding created a dead zone of default
// cursor along the bottom/right edges. Cropping to the visible glyph shrinks that zone to
// the glyph itself. Returns {img, x, y} with the crop origin, or null if fully transparent.
function cropImageDataToOpaqueBounds(ctx, img) {
    const w = img.width, h = img.height, d = img.data;
    let minX = w, minY = h, maxX = -1, maxY = -1;
    for (let y = 0; y < h; y++) {
        for (let x = 0; x < w; x++) {
            if (d[(y * w + x) * 4 + 3] !== 0) {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
    }
    if (maxX < 0) return null;
    const cw = maxX - minX + 1, ch = maxY - minY + 1;
    if (cw === w && ch === h) return { img: img, x: 0, y: 0 };
    const out = ctx.createImageData(cw, ch);
    for (let y = 0; y < ch; y++) {
        const src = ((y + minY) * w + minX) * 4;
        out.data.set(d.subarray(src, src + cw * 4), y * cw * 4);
    }
    return { img: out, x: minX, y: minY };
}

// Build a CSS cursor from a decoded pointer bitmap and cache it by index, so PTR_CACHED
// can re-select it later. Shared by PTR_NEW and PTR_COLOR.
Client.prototype._cachePointer = function (u, kind) {
    const full = u.getImageData(this.pointerCacheCanvasCtx);
    if (!full) {
        PTR_LOG(kind + " decode failed bpp=" + u.xorBpp + " " + u.width + "x" + u.height +
            " lenXor=" + u.lengthXorMask + " lenAnd=" + u.lengthAndMask + " (keeping current cursor)");
        return;
    }
    const crop = cropImageDataToOpaqueBounds(this.pointerCacheCanvasCtx, full);
    if (!crop) {
        // Fully transparent bitmap: the host means "hide the pointer".
        PTR_LOG(kind + " idx=" + u.cacheIndex + " fully transparent -> null cursor");
        this._setCursorClass("pointer-cache-null");
        return;
    }
    // Hotspot moves with the crop origin; it may legitimately sit in cropped-away padding,
    // so clamp it into the cropped image (CSS requires the hotspot inside the image).
    const img = crop.img;
    const rawHotX = Math.min(Math.max(u.x - crop.x, 0), img.width - 1);
    const rawHotY = Math.min(Math.max(u.y - crop.y, 0), img.height - 1);

    // In HiDPI mode the desktop is requested at native (device-pixel) resolution, so the host
    // sends cursor bitmaps in DEVICE pixels — e.g. a 32px cursor arrives as 64px on a 200%
    // panel. CSS cursor url()/hotspot are measured in CSS pixels and the browser draws the PNG
    // at its natural pixel size, which would render the cursor at 200% of its intended on-screen
    // size. There is no size parameter on the CSS cursor, so we downscale the actual bitmap
    // (and hotspot) by the canvas's measured native/logical ratio before exporting the PNG.
    const cursorScale = this.cursorScaleRatio() || 1;
    const dispW = Math.max(1, Math.round(img.width / cursorScale));
    const dispH = Math.max(1, Math.round(img.height / cursorScale));
    const hotX = Math.min(Math.round(rawHotX / cursorScale), dispW - 1);
    const hotY = Math.min(Math.round(rawHotY / cursorScale), dispH - 1);

    // Size the cache canvas to the DISPLAYED pointer size (resizing also clears it, so stale
    // pixels never bleed through). At 100% scale this equals the native size (identity draw).
    if (this.pointerCacheCanvas.width !== dispW || this.pointerCacheCanvas.height !== dispH) {
        this.pointerCacheCanvas.width = dispW;
        this.pointerCacheCanvas.height = dispH;
    } else {
        this.pointerCacheCanvasCtx.clearRect(0, 0, dispW, dispH);
    }
    if (dispW === img.width && dispH === img.height) {
        this.pointerCacheCanvasCtx.putImageData(img, 0, 0);
    } else {
        // putImageData ignores canvas scaling, so stage the native bitmap on a scratch canvas
        // and drawImage it down (drawImage honors the smoothing needed for a clean shrink).
        const scratch = this._pointerScaleCanvas || (this._pointerScaleCanvas = document.createElement("canvas"));
        scratch.width = img.width;
        scratch.height = img.height;
        scratch.getContext("2d").putImageData(img, 0, 0);
        this.pointerCacheCanvasCtx.imageSmoothingEnabled = true;
        this.pointerCacheCanvasCtx.drawImage(scratch, 0, 0, dispW, dispH);
    }
    // PNG, not WebP: lossy WebP (which toDataURL produces) drops or flattens the alpha
    // channel in several browsers, which turned anti-aliased pointers (e.g. the text I-beam,
    // whose shape lives entirely in the alpha channel) into an opaque blob. PNG preserves
    // per-pixel alpha losslessly.
    const url = this.pointerCacheCanvas.toDataURL("image/png");

    if (this.pointerCache.hasOwnProperty(u.cacheIndex)) {
        document.getElementsByTagName("head")[0].removeChild(this.pointerCache[u.cacheIndex]);
        delete this.pointerCache[u.cacheIndex];
    }
    const style = document.createElement("style");
    const className = "pointer-cache-" + u.cacheIndex;
    // Target both the classed element and everything inside it, with !important, so
    // interactive elements' own cursor rules (e.g. buttons' cursor:pointer) cannot
    // override the session cursor while it is active.
    style.innerHTML = "." + className + ", ." + className +
        " * {cursor:url(\"" + url + "\") " + hotX + " " + hotY + ", auto !important;}";
    document.getElementsByTagName("head")[0].appendChild(style);
    this.pointerCache[u.cacheIndex] = style;
    this._setCursorClass(className);
    PTR_LOG(kind + " cached idx=" + u.cacheIndex + " bpp=" + u.xorBpp + " " +
        u.width + "x" + u.height + " cropped=" + img.width + "x" + img.height +
        "+" + crop.x + "+" + crop.y + " hot=" + hotX + "," + hotY);
};

// ---- input ---------------------------------------------------------------------------------------
function mouseButtonMap(button) {
    switch (button) { case 0: return 1; case 2: return 2; case 1: return 3; default: return 0; }
}

// Maps a DOM mouse event's CSS coordinates to canvas *backing-store* pixels (= RDP desktop pixels).
// Uses getBoundingClientRect so it stays correct under HiDPI CSS scaling, fullscreen letterboxing,
// and any container offset. Coordinates are clamped to the desktop bounds.
Client.prototype._canvasCoords = function (e) {
    const rect = this.canvas.getBoundingClientRect();
    const scaleX = this.canvas.width / rect.width;
    const scaleY = this.canvas.height / rect.height;
    let x = Math.round((e.clientX - rect.left) * scaleX);
    let y = Math.round((e.clientY - rect.top) * scaleY);
    x = Math.max(0, Math.min(this.canvas.width - 1, x));
    y = Math.max(0, Math.min(this.canvas.height - 1, y));
    return { x: x, y: y };
};

Client.prototype._sendEvent = function (eventArrayBuffer) {
    if (this.proto) this.proto.sendInputEvent(new Uint8Array(eventArrayBuffer));
};

// The keyboard listeners live on window (so the session has focus without clicking the canvas first),
// which means they also fire while the user is typing in an overlay control (login form, the clipboard
// textarea). When such a control is focused, let the keystroke reach it normally instead of forwarding
// it to the remote and swallowing it.
function _typingInOverlay() {
    const el = document.activeElement;
    if (!el) return false;
    const tag = el.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || el.isContentEditable;
}
Client.prototype.handleKeyDown = function (e) {
    if (!this.connected || _typingInOverlay()) return;
    const ev = new KeyboardEventKeyDown(e.code);
    if (ev.keyCode === undefined) { e.preventDefault(); return false; }
    this._sendEvent(ev.serialize());
    e.preventDefault();
    return false;
};
Client.prototype.handleKeyUp = function (e) {
    if (!this.connected || _typingInOverlay()) return;
    const ev = new KeyboardEventKeyUp(e.code);
    if (ev.keyCode === undefined) { e.preventDefault(); return false; }
    this._sendEvent(ev.serialize());
    e.preventDefault();
    return false;
};
Client.prototype.handleMouseMove = function (e) {
    if (!this.connected) return;
    const p = this._canvasCoords(e);
    this._sendEvent(new MouseMoveEvent(p.x, p.y).serialize());
    e.preventDefault();
    return false;
};
Client.prototype.handleMouseDown = function (e) {
    if (!this.connected) return;
    const p = this._canvasCoords(e);
    this._sendEvent(new MouseDownEvent(p.x, p.y, mouseButtonMap(e.button)).serialize());
    e.preventDefault();
    return false;
};
Client.prototype.handleMouseUp = function (e) {
    if (!this.connected) return;
    const p = this._canvasCoords(e);
    this._sendEvent(new MouseUpEvent(p.x, p.y, mouseButtonMap(e.button)).serialize());
    e.preventDefault();
    return false;
};
Client.prototype.handleWheel = function (e) {
    if (!this.connected) return;
    const p = this._canvasCoords(e);
    const isHorizontal = Math.abs(e.deltaX) > Math.abs(e.deltaY);
    const delta = isHorizontal ? e.deltaX : e.deltaY;
    const step = Math.round(Math.abs(delta) * 15 / 8);
    this._sendEvent(new MouseWheelEvent(p.x, p.y, step, delta > 0, isHorizontal).serialize());
    e.preventDefault();
    return false;
};

// The RDP protocol detected the host ending the session (graceful logoff/disconnect or MCS ultimatum)
// BEFORE the host lazily closes its TCP socket. Tear down the client side immediately so the UI
// reflects the disconnect at once instead of waiting (potentially several seconds) for the socket
// close to propagate. For a non-graceful end we surface the error message first; either way we then
// close the WebSocket, whose onclose → deinitialize resets the console to the login form.
Client.prototype._onProtocolClose = function (graceful, message) {
    if (this._protocolClosed) return; // fire once (multiple disconnect PDUs can arrive)
    this._protocolClosed = true;
    // A graceful end (host logoff, restart, admin disconnect) means the session is really over — flag it
    // so deinitialize() emits "closed", not "reconnecting". A non-graceful protocol error is treated as a
    // drop (leave the flag clear) so the UI keeps the frame and retries.
    if (graceful) this._intentionalClose = true;
    // Remember why the session ended so deinitialize() can surface it on the "closed" status (the
    // socket close → deinitialize would otherwise reset the console and wipe any message we set here).
    this._closeReason = message || null;
    // Stop driving the protocol and release input/render before tearing down the socket.
    this.proto = null;
    try { if (this.socket) this.socket.close(1000, "remote session ended"); } catch (e) { /* ignore */ }
};

Client.prototype.disconnect = function () {
    if (!this.socket) return;
    this._intentionalClose = true; // user-initiated: emit "closed", never "reconnecting"
    this.deinitialize();
    try { this.socket.close(1000); } catch (e) { /* ignore */ }
};
