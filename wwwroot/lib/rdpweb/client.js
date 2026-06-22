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
    // TEST (2026-06-16): under GFX, force the EXACT resolution the working macOS Remote Desktop app used
    // (2560x1606, its full native screen). Everything else (scale, DisplayControl, Geometry, acks) now
    // matches the macOS app yet the host still does a 2nd RESET_GRAPHICS and stalls; resolution is the
    // last structural difference (we connect at a small 1452x1438 window, the macOS app at 2560x1606 and
    // gets ONE reset + flood). If the host now resets once and streams, sub-native res was the trigger.
    if (typeof rdpGfxMode === "function" && rdpGfxMode() !== "off") {
        return { width: 2560, height: 1606 };
    }
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

// creds = {user, password, domain, performanceFlags}
// performanceFlags (optional) is the RDP ExtendedInfoPacket performanceFlags value; omit for the
// default (best visual fidelity — font smoothing + desktop composition).
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
    this.proto = new RdpProtocol(transport, {
        username: this.creds.user,
        password: this.creds.password,
        domain: this.creds.domain || "",
        width: this.canvas.width,
        height: this.canvas.height,
        selectedProtocol: 2, // HYBRID (NLA) — matches the gateway's X.224 negotiation
        // width/height are ALREADY device pixels (chooseDesktopSize × dpr), so the resolution carries the
        // DPI. Sending desktopScaleFactor=DPI too double-applies it: under GFX the host then churns
        // RESET_GRAPHICS and stalls. Commit to ONE strategy (native-pixel resolution) → desktopScale=100.
        desktopScaleFactor: 100,
        deviceScaleFactor: 100,
        performanceFlags: this.creds.performanceFlags, // undefined → protocol default (best visuals)
        audio: !!this.audioEnabled,        // request the rdpsnd channel for remote sound
        microphone: !!this.microphoneEnabled, // accept the AUDIO_INPUT DVC for mic redirection
        camera: !!this.cameraEnabled,      // accept the RDPECAM DVCs for camera redirection
        clipboard: !!this.clipboardEnabled, // request the cliprdr channel for clipboard sync
    }, {
        onUpdate: this.onUpdate,
        onActive: function () { self._onActive(); },
        onError: function (m) { console.error("rdp:", m); self._status("error", m); },
        onLog: function (m) { console.log("rdp:", m); },
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
};

// ---- remote audio playback (Web Audio) -----------------------------------------------------------
// Enable/disable remote sound. Must be set before connect() so the rdpsnd channel is advertised.
Client.prototype.setAudioEnabled = function (on) { this.audioEnabled = !!on; };

// Mute/unmute playback at runtime (the channel stays open; we just drop or pass the waves).
Client.prototype.setMuted = function (muted) { this.muted = !!muted; };

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
    }
    if (this.audioCtx.state === "suspended") this.audioCtx.resume();
    return this.audioCtx;
};

// Play one decoded PCM wave. fmt = {rate, bits, channels}; pcm = little-endian interleaved samples.
// Schedules buffers back-to-back on a shared timeline so consecutive waves play gaplessly.
Client.prototype._playPcm = function (fmt, pcm) {
    if (this.muted) return;
    try {
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
        src.connect(ctx.destination);
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
    console.log("mic: capturing", self._micCtx.sampleRate + "Hz →", fmt.rate + "Hz/" + fmt.bits + "bit/" + fmt.channels + "ch");
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
// Enable/disable clipboard redirection. Set before connect() to advertise the cliprdr channel.
Client.prototype.setClipboardEnabled = function (on) { this.clipboardEnabled = !!on; };

// Remote session copied text → write it to the browser clipboard (best effort; needs a secure
// context + permission). We suppress our own change echo so it isn't sent straight back.
Client.prototype._onRemoteClipboardText = function (text) {
    this._lastRemoteClip = text;
    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).catch(function () { /* permission/denied — ignore */ });
    }
};

// Offer local browser clipboard text to the remote session (so it can paste). Call from a user
// gesture (clipboard read requires one). No-op if the text is what the remote just sent us.
Client.prototype.pushLocalClipboard = function () {
    const self = this;
    if (!this.proto || !navigator.clipboard || !navigator.clipboard.readText) return;
    navigator.clipboard.readText().then(function (text) {
        if (text == null || text === self._lastRemoteClip) return;
        self.proto.sendClipboardText(text);
    }).catch(function () { /* permission/denied — ignore */ });
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
    // When GFX rendering is active, do NOT run the multi-step dummy-resize sequence: each dummy resize
    // triggers a Deactivation-Reactivation that resizes (and thus CLEARS) the output canvas and tears
    // down the GFX surface — wiping the just-decoded H.264/ClearCodec frame and going black (the host
    // doesn't resend a keyframe for a no-op resolution change, so the cleared canvas stays cleared).
    // Instead send ONE scale-only monitor layout at the target DPI: the single reactivation it causes
    // makes the host re-send a keyframe at the new scale, which repaints correctly.
    if (this.proto && this.proto.gfx) {
        // Under GFX the host stalls after the init burst with THREE RESET_GRAPHICS; the working macOS
        // Remote Desktop app gets ONE then floods. A MITM diff showed the cause: we apply DPI TWICE —
        // chooseDesktopSize already multiplies the CSS size by devicePixelRatio (so canvas.width is the
        // native device-pixel resolution), and then we ALSO sent a monitor layout at 200% desktopScale.
        // The host churns RESETs trying to reconcile a device-pixel surface that also asks for 2x scale.
        // The macOS app commits to ONE strategy (native-pixel resolution). So send a single layout at the
        // CURRENT (device-pixel) resolution with desktopScale=100 — no competing scale, no resolution
        // change (no surface teardown). This is what settles the host into free-run.
        if (!this.proto.canResize()) return; // DisplayControl/active not ready yet; retried from _onActive
        this._initialScaleApplied = true;
        this._sessionScale = 100; // resolution already carries DPI (device pixels); do NOT double-scale
        this.proto.sendMonitorLayout(this.canvas.width, this.canvas.height, 100, 100);
        return;
    }
    if (!this.proto || !this.proto.canResize()) return; // not active yet; retried from _onActive
    const dpr = window.devicePixelRatio || 1;
    // Capture the session scale ONCE. Resizes reuse this instead of re-reading devicePixelRatio, which
    // is unreliable during a window resize (it can momentarily read 1, which would send a 200%→100%
    // scale change mid-session — this host stalls its stream on that, going black after the resize).
    this._sessionScale = this.desktopScaleForDpr(dpr);
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
    this.canvas.classList = [];

    // Tear down the audio timeline so a later reconnect starts fresh (the AudioContext is reused).
    this._audioTime = 0;

    // Release the microphone if we were capturing (stops the OS "in use" indicator on disconnect).
    this._stopMicCapture();
    // Release the webcam too (stops the camera light/in-use indicator).
    this._stopCameraCapture();

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

// ---- RDPEGFX (H.264) rendering -------------------------------------------------------------------
// Blit a region of a decoded GFX surface canvas onto the output canvas. The surface canvas holds the
// host's decoded picture in surface coordinates; (dx,dy) is where that region maps onto the desktop
// (MAP_SURFACE_TO_OUTPUT origin + the region offset). drawImage handles the OffscreenCanvas source.
Client.prototype._onGfxPaint = function (canvas, sx, sy, sw, sh, dx, dy) {
    if (sw <= 0 || sh <= 0) return;
    try {
        this.ctx.drawImage(canvas, sx, sy, sw, sh, dx, dy, sw, sh);
        // Log the first few paints, then every 30th, so we can SEE whether painting keeps going (stream
        // alive) vs. genuinely stops — without per-frame spam.
        const pc = (this._gfxPaintCount = (this._gfxPaintCount || 0) + 1);
        if (pc <= 8 || pc % 30 === 0) {
            let sample = "?";
            try {
                const px = this.ctx.getImageData(Math.floor(this.canvas.width / 2), Math.floor(this.canvas.height / 2), 1, 1).data;
                sample = "centerPx=rgba(" + px[0] + "," + px[1] + "," + px[2] + "," + px[3] + ")";
            } catch (e) { sample = "centerPx=?(" + (e && e.message) + ")"; }
            console.log("rdp: gfx paint #" + this._gfxPaintCount + " -> output " + this.canvas.width + "x" + this.canvas.height +
                " css=" + this.canvas.style.width + "x" + this.canvas.style.height +
                " src=" + sx + "," + sy + " " + sw + "x" + sh + " dst=" + dx + "," + dy + " " + sample);
        }
    } catch (e) {
        console.warn("gfx paint failed:", e);
    }
};

// DEBUG: draw a decoded VideoFrame STRAIGHT onto the visible output canvas (real DOM canvas), trying
// both drawImage(frame) and a bitmap fallback, and report what the visible canvas holds afterward.
Client.prototype._onGfxDirectFrame = function (frame, surfaceId, map) {
    const self = this;
    const ox = (map && map.originX) || 0, oy = (map && map.originY) || 0;
    try {
        this.ctx.drawImage(frame, ox, oy);
        const px = this.ctx.getImageData(Math.floor(this.canvas.width / 2), Math.floor(this.canvas.height / 2), 1, 1).data;
        if (!this._directDbg) { this._directDbg = 1; console.log("rdp: DIRECT drawImage(frame) center=rgba(" + px[0] + "," + px[1] + "," + px[2] + "," + px[3] + ")"); }
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
        this.canvas.width = w;
        this.canvas.height = h;
        if (this._wrapEl) this._fit(this._wrapEl);
    }
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
