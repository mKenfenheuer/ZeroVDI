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
    this.connected = false;     // input/render enabled (session active)
    this.pointerCache = {};
    this.proto = null;
    this.statusCb = null;       // optional (status, message) => void for the UI

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

// Sizes the canvas backing store to a desktop resolution that fills `wrapEl` in *device* pixels, so
// the remote desktop renders crisp and 1:1 on HiDPI displays (no browser upscaling/blur). The CSS
// size of the canvas is then set to the wrapper's CSS size so it fits the visible area exactly.
// Returns {width, height} in device pixels (the RDP desktop resolution to request).
//
// RDP desktop dimensions must be even (bitmap rows are 16bpp; widths are safest as multiples of 4),
// and are clamped to the [MS-RDPBCGR] valid range (200..8192 px per axis for typical hosts).
Client.prototype.chooseDesktopSize = function (wrapEl) {
    const dpr = window.devicePixelRatio || 1;
    const cssW = Math.max(1, Math.floor(wrapEl.clientWidth));
    const cssH = Math.max(1, Math.floor(wrapEl.clientHeight));
    let w = Math.round(cssW * dpr);
    let h = Math.round(cssH * dpr);
    w = Math.max(200, Math.min(8192, w - (w % 4)));
    h = Math.max(200, Math.min(8192, h - (h % 2)));
    return { width: w, height: h };
};

// Applies a chosen device-pixel size to the canvas backing store, and fits its on-screen size to the
// wrapper via CSS. Call before connecting (so the resolution is baked into the RDP handshake).
Client.prototype.applyDesktopSize = function (wrapEl, size) {
    this._wrapEl = wrapEl; // remembered for re-fitting after live resolution changes
    this._appliedW = size.width;   // last resolution we asked the server for (resize jitter guard)
    this._appliedH = size.height;
    this.canvas.width = size.width;
    this.canvas.height = size.height;
    this._fit(wrapEl);
};

// Sets the canvas CSS size so the device-pixel backing store maps 1:1 to device pixels on screen
// (i.e. cssSize = backingStore / devicePixelRatio), which fills the wrapper exactly when the desktop
// size was chosen by chooseDesktopSize().
Client.prototype._fit = function (wrapEl) {
    const dpr = window.devicePixelRatio || 1;
    this.canvas.style.width = (this.canvas.width / dpr) + "px";
    this.canvas.style.height = (this.canvas.height / dpr) + "px";
};

// creds = {user, password, domain}
Client.prototype.connect = function (creds) {
    const self = this;
    this.creds = creds;

    const url = new URL(this.websocketURL, window.location.href);
    url.protocol = (window.location.protocol === "https:") ? "wss:" : "ws:";
    url.searchParams.set("width", this.canvas.width);
    url.searchParams.set("height", this.canvas.height);

    this.socket = new WebSocket(url.toString());
    this.socket.binaryType = "arraybuffer";
    this._handshakeDone = false;

    this.socket.onopen = function () {
        // First frame: credentials JSON (text).
        self.socket.send(JSON.stringify({
            user: creds.user,
            password: creds.password,
            domain: creds.domain || "",
        }));
        self._status("connecting", "authenticating…");
    };

    this.socket.onmessage = function (e) {
        if (typeof e.data === "string") {
            self._onControlFrame(e.data);
            return;
        }
        // Binary: relayed RDP bytes.
        const bytes = (e.data instanceof ArrayBuffer) ? new Uint8Array(e.data) : new Uint8Array(e.data);
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
            this._status("connecting", "negotiating session…");
            this._startProtocol();
            break;
        case "error":
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

Client.prototype._startProtocol = function () {
    const self = this;
    const transport = {
        send: function (u8) {
            if (self.socket && self.socket.readyState === WebSocket.OPEN) {
                self.socket.send(u8);
            }
        },
    };
    const dpr = window.devicePixelRatio || 1;
    this.proto = new RdpProtocol(transport, {
        username: this.creds.user,
        password: this.creds.password,
        domain: this.creds.domain || "",
        width: this.canvas.width,
        height: this.canvas.height,
        selectedProtocol: 2, // HYBRID (NLA) — matches the gateway's X.224 negotiation
        desktopScaleFactor: this.desktopScaleForDpr(dpr),
        deviceScaleFactor: 100,
    }, {
        onUpdate: this.onUpdate,
        onActive: function () { self._onActive(); },
        onError: function (m) { console.error("rdp:", m); self._status("error", m); },
        onLog: function (m) { console.log("rdp:", m); },
        onResize: function (w, h) { self._onRemoteResize(w, h); },
        onDisplayControlReady: function () { self._displayControlReady = true; self._applyInitialScale(); },
    });
    this.proto.start();
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

// The RDP handshake (CS_CORE) carries no DesktopScaleFactor, so the session always starts at 100%
// scale regardless of the display's DPI — making the remote UI tiny on HiDPI screens. The only way
// to set scale is an MS-RDPEDISP MONITOR_LAYOUT, so once Display Control is ready AND the session is
// active we send one at the SAME resolution but the real DPR scale. This applies correct scaling
// (e.g. 200%) from the start instead of only after a manual resize. Fires once per session.
Client.prototype._applyInitialScale = function () {
    if (this._initialScaleApplied) return;
    if (!this.proto || !this.proto.canResize()) return; // not active yet; retried from _onActive
    const dpr = window.devicePixelRatio || 1;
    // Capture the session scale ONCE. Resizes reuse this instead of re-reading devicePixelRatio, which
    // is unreliable during a window resize (it can momentarily read 1, which would send a 200%→100%
    // scale change mid-session — this host stalls its stream on that, going black after the resize).
    this._sessionScale = this.desktopScaleForDpr(dpr);
    if (this._sessionScale <= 100) { this._initialScaleApplied = true; return; } // nothing to scale
    this._initialScaleApplied = true;
    // Same backing-store resolution, only the DPI scale — no canvas resize/clear needed.
    this.proto.sendMonitorLayout(this.canvas.width, this.canvas.height, this._sessionScale, 100);
};

// The fixed DPI scale for this session (captured at connect). Resizes must reuse it so they only ever
// change resolution, never scale — see _applyInitialScale for why re-reading dpr per resize is unsafe.
Client.prototype._scaleForSession = function () {
    if (this._sessionScale) return this._sessionScale;
    return this.desktopScaleForDpr(window.devicePixelRatio || 1);
};

Client.prototype._onActive = function () {
    if (this.connected) {
        // Reached again after a reactivation; Display Control may only now be usable.
        this._applyInitialScale();
        return;
    }
    this.connected = true;
    this._activeSince = performance.now(); // for maybeResize's settle guard
    this._status("ready", null);
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
    window.removeEventListener("keydown", this.handleKeyDown);
    window.removeEventListener("keyup", this.handleKeyUp);
    this.canvas.removeEventListener("mousemove", this.handleMouseMove);
    this.canvas.removeEventListener("mousedown", this.handleMouseDown);
    this.canvas.removeEventListener("mouseup", this.handleMouseUp);
    this.canvas.removeEventListener("contextmenu", this.handleMouseUp);
    this.canvas.removeEventListener("wheel", this.handleWheel);

    this.connected = false;

    Object.entries(this.pointerCache).forEach(([index, style]) => {
        document.getElementsByTagName("head")[0].removeChild(style);
    });
    this.pointerCache = {};
    this.canvas.classList = [];
    this._status("closed", null);
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
            [inputPtr, bitmapData.bitmapLength, outputPtr, rowDelta]);
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

Client.prototype.handlePointer = function (header, r) {
    if (header.isPTRNull()) { this.canvas.classList = ["pointer-cache-null"]; return; }
    if (header.isPTRDefault()) { this.canvas.classList = ["pointer-cache-default"]; return; }
    if (header.isPTRColor()) { /* color pointer unsupported in v1 */ return; }

    if (header.isPTRNew()) {
        const u = parseNewPointerUpdate(r);
        const img = u.getImageData(this.pointerCacheCanvasCtx);
        if (!img) return;
        this.pointerCacheCanvasCtx.putImageData(img, 0, 0);
        const url = this.pointerCacheCanvas.toDataURL("image/webp", 1);

        if (this.pointerCache.hasOwnProperty(u.cacheIndex)) {
            document.getElementsByTagName("head")[0].removeChild(this.pointerCache[u.cacheIndex]);
            delete this.pointerCache[u.cacheIndex];
        }
        const style = document.createElement("style");
        const className = "pointer-cache-" + u.cacheIndex;
        style.innerHTML = "." + className + " {cursor:url(\"" + url + "\") " + u.x + " " + u.y + ", auto}";
        document.getElementsByTagName("head")[0].appendChild(style);
        this.pointerCache[u.cacheIndex] = style;
        this.canvas.classList = [className];
        return;
    }
    if (header.isPTRCached()) {
        const cacheIndex = r.uint16(true);
        this.canvas.classList = ["pointer-cache-" + cacheIndex];
        return;
    }
    // PTR_POSITION / large pointer: not handled in v1.
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

Client.prototype.handleKeyDown = function (e) {
    if (!this.connected) return;
    const ev = new KeyboardEventKeyDown(e.code);
    if (ev.keyCode === undefined) { e.preventDefault(); return false; }
    this._sendEvent(ev.serialize());
    e.preventDefault();
    return false;
};
Client.prototype.handleKeyUp = function (e) {
    if (!this.connected) return;
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

Client.prototype.disconnect = function () {
    if (!this.socket) return;
    this.deinitialize();
    try { this.socket.close(1000); } catch (e) { /* ignore */ }
};
