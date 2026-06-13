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
        selectedProtocol: 2, // HYBRID (NLA) — matches the gateway's X.224 negotiation
    }, {
        onUpdate: this.onUpdate,
        onActive: function () { self._onActive(); },
        onError: function (m) { console.error("rdp:", m); self._status("error", m); },
        onLog: function (m) { console.log("rdp:", m); },
    });
    this.proto.start();
};

Client.prototype._onActive = function () {
    if (this.connected) return;
    this.connected = true;
    this._status("ready", null);

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

        if (header.isCompressed()) {
            // Fastpath-level compression of the update wrapper is not supported; skip its payload.
            r.skip(header.size);
            continue;
        }

        const bodyStart = r.offset;
        try {
            if (header.isBitmap()) {
                this.handleBitmap(r);
            } else if (header.isPointer()) {
                this.handlePointer(header, r);
            } else if (header.isSynchronize()) {
                // [T128] artifact; ignore.
            } else {
                // Orders / surface commands / palette not implemented in v1.
            }
        } catch (e) {
            console.warn("update render error:", e);
        }
        // Always advance exactly past this update's declared size, regardless of how much each
        // handler consumed (robust against partially-handled update types).
        r.offset = bodyStart + header.size;
    }
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

        // Compressed (interleaved RLE) — decompress via the wasm module.
        const inputPtr = Module._malloc(bitmapData.bitmapLength);
        const outputPtr = Module._malloc(resultSize);
        const inputHeap = new Uint8Array(Module.HEAPU8.buffer, inputPtr, bitmapData.bitmapDataStream.byteLength);
        inputHeap.set(new Uint8Array(bitmapData.bitmapDataStream));

        const ok = Module.ccall("RleDecompress", "number",
            ["number", "number", "number", "number"],
            [inputPtr, bitmapData.bitmapLength, outputPtr, rowDelta]);

        if (!ok) {
            console.warn("bad RLE decompress", bitmapData);
            Module._free(inputPtr);
            Module._free(outputPtr);
            return;
        }

        let rgb = new Uint8ClampedArray(Module.HEAP8.buffer.slice(outputPtr, outputPtr + resultSize));
        let rgba = new Uint8ClampedArray(size * 4);
        flipV(rgb, bitmapData.width, bitmapData.height);
        rgb2rgba(rgb, resultSize, rgba);
        this.ctx.putImageData(new ImageData(rgba, bitmapData.width, bitmapData.height), bitmapData.destLeft, bitmapData.destTop);

        Module._free(inputPtr);
        Module._free(outputPtr);
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
