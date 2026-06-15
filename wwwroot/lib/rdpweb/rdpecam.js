// rdpecam.js — client side of the Video Capture (camera) Virtual Channel Extension (MS-RDPECAM).
//
// Two cooperating dynamic virtual channels (both ride drdynvc):
//   1. The CONTROL / enumerator channel "RDCamera_Device_Enumerator" — opened by the host. On open we
//      send SelectVersionRequest; after the host's SelectVersionResponse we advertise our virtual
//      camera with a DeviceAddedNotification carrying a VirtualChannelName. The host then opens…
//   2. A per-device channel named by that VirtualChannelName — on which the host drives the capture
//      negotiation (activate → stream list → media type list → current media type → start streams →
//      sample requests). We answer each and stream JPEG frames as SampleResponses.
//
// Codec choice (deliberately simple, like rdpsnd.js' PCM-only): we advertise a single MJPG media type
// per resolution with the DecodingRequired flag, and send each captured frame as a complete JPEG. A
// real MJPEG USB webcam reports exactly this, and Windows MediaFoundation has a built-in MJPEG decoder
// — so the browser only needs canvas→JPEG (no H.264/WebCodecs encoder or sample muxing). client.js
// supplies frames via the onFrameNeeded/pushFrame bridge.
//
// References: [MS-RDPECAM] 2.2.* and FreeRDP channels/rdpecam/client/{camera_device_enum_main,
// camera_device_main}.c (ground truth for the message ids, the 2-byte header, and PDU byte layouts).

// CAM_MSG_ID ([MS-RDPECAM] 2.2.1.1 / FreeRDP rdpecam.h).
const CAM_MSG_ID_SuccessResponse = 0x01;
const CAM_MSG_ID_ErrorResponse = 0x02;
const CAM_MSG_ID_SelectVersionRequest = 0x03;
const CAM_MSG_ID_SelectVersionResponse = 0x04;
const CAM_MSG_ID_DeviceAddedNotification = 0x05;
const CAM_MSG_ID_DeviceRemovedNotification = 0x06;
const CAM_MSG_ID_ActivateDeviceRequest = 0x07;
const CAM_MSG_ID_DeactivateDeviceRequest = 0x08;
const CAM_MSG_ID_StreamListRequest = 0x09;
const CAM_MSG_ID_StreamListResponse = 0x0A;
const CAM_MSG_ID_MediaTypeListRequest = 0x0B;
const CAM_MSG_ID_MediaTypeListResponse = 0x0C;
const CAM_MSG_ID_CurrentMediaTypeRequest = 0x0D;
const CAM_MSG_ID_CurrentMediaTypeResponse = 0x0E;
const CAM_MSG_ID_StartStreamsRequest = 0x0F;
const CAM_MSG_ID_StopStreamsRequest = 0x10;
const CAM_MSG_ID_SampleRequest = 0x11;
const CAM_MSG_ID_SampleResponse = 0x12;
const CAM_MSG_ID_SampleErrorResponse = 0x13;
const CAM_MSG_ID_PropertyListRequest = 0x14;
const CAM_MSG_ID_PropertyListResponse = 0x15;

// Media formats ([MS-RDPECAM] 2.2.4.4 CAM_MEDIA_FORMAT). We only produce MJPG.
// Media formats ([MS-RDPECAM] 2.2.4.4 CAM_MEDIA_FORMAT).
const CAM_MEDIA_FORMAT_H264 = 0x01;
const CAM_MEDIA_FORMAT_MJPG = 0x02;
const CAM_MEDIA_FORMAT_YUY2 = 0x03;
const CAM_MEDIA_FORMAT_NV12 = 0x04;
const CAM_MEDIA_FORMAT_I420 = 0x05;
const CAM_MEDIA_FORMAT_RGB24 = 0x06;
const CAM_MEDIA_FORMAT_RGB32 = 0x07;
// Media type description flags ([MS-RDPECAM] 2.2.4.5). DecodingRequired = the sample is a compressed
// bitstream the host must decode (H264/MJPG). Raw formats (NV12/I420/RGB) use flags 0.
const CAM_MEDIA_TYPE_DESCRIPTION_FLAG_DecodingRequired = 0x01;
// Stream description ([MS-RDPECAM] 2.2.4.2).
const CAM_STREAM_FRAME_SOURCE_TYPE_Color = 0x0001;
const CAM_STREAM_CATEGORY_Capture = 0x01;

const ECAM_PROTO_VERSION = 0x02; // client protocol version (FreeRDP ECAM_PROTO_VERSION)

// The media types we advertise. Each carries an explicit CAM_MEDIA_FORMAT. We advertise a RAW format
// (NV12) so the host needs no decoder — Windows' MediaFoundation source reader rejected MJPG (it
// activates, probes, then DEACTIVATES without StartStreams when the format's decode MFT is absent).
// `fmt` is the wire format; `raw` true means flags=0 (no DecodingRequired). client.js produces the
// chosen format's pixels (see _captureCameraFrame / the format passed to onCameraStart).
const CAM_MEDIA_TYPES = [
    { width: 640, height: 480, fps: 30, fmt: CAM_MEDIA_FORMAT_NV12, raw: true },
    { width: 1280, height: 720, fps: 30, fmt: CAM_MEDIA_FORMAT_NV12, raw: true },
    { width: 320, height: 240, fps: 30, fmt: CAM_MEDIA_FORMAT_NV12, raw: true },
];

// ---- small writer ---------------------------------------------------------------------------------
function CamWriter() { this.b = []; }
CamWriter.prototype.u8 = function (v) { this.b.push(v & 0xff); return this; };
CamWriter.prototype.u16 = function (v) { this.b.push(v & 0xff, (v >> 8) & 0xff); return this; };
CamWriter.prototype.u32 = function (v) { this.b.push(v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >> 24) & 0xff); return this; };
CamWriter.prototype.bytes = function (a) { for (let i = 0; i < a.length; i++) this.b.push(a[i] & 0xff); return this; };
CamWriter.prototype.utf16 = function (s) { for (let i = 0; i < s.length; i++) { const c = s.charCodeAt(i); this.b.push(c & 0xff, (c >> 8) & 0xff); } return this; };
CamWriter.prototype.ascii = function (s) { for (let i = 0; i < s.length; i++) this.b.push(s.charCodeAt(i) & 0xff); return this; };
CamWriter.prototype.arr = function () { return new Uint8Array(this.b); };

// Serialize a 26-byte CAM_MEDIA_TYPE_DESCRIPTION ([MS-RDPECAM] 2.2.4.6): Format(1) + Width(4) +
// Height(4) + FrameRateNumerator(4) + FrameRateDenominator(4) + PixelAspectRatioNumerator(4) +
// PixelAspectRatioDenominator(4) + Flags(1) = 26 bytes (matches FreeRDP ecam_dev_write_media_type).
const CAM_MEDIA_TYPE_SIZE = 26;
function camWriteMediaType(w, mt) {
    w.u8(mt.fmt);                                 // Format
    w.u32(mt.width);                              // Width
    w.u32(mt.height);                             // Height
    w.u32(mt.fps);                                // FrameRateNumerator
    w.u32(1);                                     // FrameRateDenominator
    w.u32(1);                                     // PixelAspectRatioNumerator
    w.u32(1);                                     // PixelAspectRatioDenominator
    w.u8(mt.raw ? 0 : CAM_MEDIA_TYPE_DESCRIPTION_FLAG_DecodingRequired); // Flags
}
function camReadMediaType(view, o) {
    return {
        format: view.getUint8(o),
        width: view.getUint32(o + 1, true),
        height: view.getUint32(o + 5, true),
        fps: view.getUint32(o + 9, true),
        flags: view.getUint8(o + 25),
        _size: CAM_MEDIA_TYPE_SIZE,
    };
}

// ==================================================================================================
// Enumerator (control) channel — "RDCamera_Device_Enumerator"
// ==================================================================================================
// send(payload) ships a payload on the enumerator DVC. callbacks: { onLog(msg), onDeviceChannelName(name) }
//   onDeviceChannelName is called with the VirtualChannelName we advertise, so protocol.js knows which
//   future DVC Create to accept and route to a RdpCamDevice.
function RdpCamEnum(send, callbacks) {
    this.send = send;
    this.cb = callbacks || {};
    this.version = ECAM_PROTO_VERSION;
    // A stable virtual-channel name for our single browser camera. The host will open a device DVC by
    // this exact name. Keep it short ASCII (it rides a DVC Create name field).
    this.deviceChannelName = "RDPGWCam0";
    this.deviceFriendlyName = "Browser Camera";
    this._announced = false;
}

RdpCamEnum.prototype._log = function (m) { if (this.cb.onLog) this.cb.onLog(m); };

// Called by protocol.js right after the enumerator DVC Create is accepted (channel OnOpen): kick off
// version negotiation. (FreeRDP sends SelectVersionRequest in ecam_on_open.)
RdpCamEnum.prototype.start = function () {
    this.send(new CamWriter().u8(this.version).u8(CAM_MSG_ID_SelectVersionRequest).arr());
    this._log("enum: SelectVersionRequest v" + this.version);
};

RdpCamEnum.prototype.onData = function (payload) {
    if (!payload || payload.length < 2) return;
    const version = payload[0];
    const msgId = payload[1];
    switch (msgId) {
        case CAM_MSG_ID_SelectVersionResponse: return this._onSelectVersion(version);
        case CAM_MSG_ID_SuccessResponse: return; // ack of our notification
        case CAM_MSG_ID_ErrorResponse: this._log("enum: error response"); return;
        default: this._log("enum: ignoring msgId 0x" + msgId.toString(16));
    }
};

// SelectVersionResponse → adopt the lower version, then advertise our virtual camera so the host opens
// its device channel.
RdpCamEnum.prototype._onSelectVersion = function (serverVersion) {
    if (serverVersion > ECAM_PROTO_VERSION) { this._log("enum: server v" + serverVersion + " unsupported"); return; }
    this.version = serverVersion;
    this._log("enum: version " + serverVersion + " agreed → announcing device");
    this._announceDevice();
};

// DeviceAddedNotification ([MS-RDPECAM] 2.2.2.3): header + DeviceName (UTF-16LE, null-terminated) +
// VirtualChannelName (ASCII, null-terminated). The host then opens a DVC named VirtualChannelName.
RdpCamEnum.prototype._announceDevice = function () {
    if (this._announced) return;
    this._announced = true;
    const w = new CamWriter();
    w.u8(this.version);
    w.u8(CAM_MSG_ID_DeviceAddedNotification);
    w.utf16(this.deviceFriendlyName).u16(0);   // DeviceName + UTF-16 NUL
    w.ascii(this.deviceChannelName).u8(0);     // VirtualChannelName + ASCII NUL
    this.send(w.arr());
    this._log("enum: DeviceAddedNotification name='" + this.deviceChannelName + "'");
    if (this.cb.onDeviceChannelName) this.cb.onDeviceChannelName(this.deviceChannelName);
};

// ==================================================================================================
// Device channel — opened by the host, named by our VirtualChannelName
// ==================================================================================================
// send(payload) ships a payload on this device DVC. callbacks:
//   { onLog(msg), onStart(mediaType), onStop(), onSampleNeeded(streamIndex) }
//   onStart(mediaType) — host started the stream at {width,height,fps}; client.js begins capture.
//   onSampleNeeded(streamIndex) — host requested a frame; client.js should call pushFrame(jpegBytes).
function RdpCamDevice(send, callbacks) {
    this.send = send;
    this.cb = callbacks || {};
    this.version = ECAM_PROTO_VERSION;
    this.currentMediaType = null;  // chosen {width,height,fps} once StartStreams arrives
    this.streamIndex = 0;
    this.streaming = false;
    this._pendingRequests = 0;     // outstanding SampleRequests we owe responses for
    this._lastJpeg = null;         // most recent captured JPEG (Uint8Array), reused if asked again
}

RdpCamDevice.prototype._log = function (m) { if (this.cb.onLog) this.cb.onLog(m); };

RdpCamDevice.prototype.onData = function (payload) {
    if (!payload || payload.length < 2) return;
    const version = payload[0];
    const msgId = payload[1];
    this.version = version || this.version;
    const body = payload.subarray(2);
    switch (msgId) {
        case CAM_MSG_ID_ActivateDeviceRequest: return this._sendGeneric(CAM_MSG_ID_SuccessResponse);
        case CAM_MSG_ID_DeactivateDeviceRequest: this._stop(); return this._sendGeneric(CAM_MSG_ID_SuccessResponse);
        case CAM_MSG_ID_StreamListRequest: return this._onStreamListRequest();
        case CAM_MSG_ID_MediaTypeListRequest: return this._onMediaTypeListRequest(body);
        case CAM_MSG_ID_CurrentMediaTypeRequest: return this._onCurrentMediaTypeRequest(body);
        case CAM_MSG_ID_PropertyListRequest: return this._sendGeneric(CAM_MSG_ID_PropertyListResponse);
        case CAM_MSG_ID_StartStreamsRequest: return this._onStartStreams(body);
        case CAM_MSG_ID_StopStreamsRequest: this._stop(); return this._sendGeneric(CAM_MSG_ID_SuccessResponse);
        case CAM_MSG_ID_SampleRequest: return this._onSampleRequest(body);
        default: this._log("dev: ignoring msgId 0x" + msgId.toString(16)); return this._sendError(0x0000000A /* OperationNotSupported */);
    }
};

// A bare header message (SuccessResponse, PropertyListResponse, …).
RdpCamDevice.prototype._sendGeneric = function (msgId) {
    this.send(new CamWriter().u8(this.version).u8(msgId).arr());
};
RdpCamDevice.prototype._sendError = function (code) {
    this.send(new CamWriter().u8(this.version).u8(CAM_MSG_ID_ErrorResponse).u32(code).arr());
};

// StreamListResponse ([MS-RDPECAM] 2.2.3.3): a single Color/Capture stream description.
RdpCamDevice.prototype._onStreamListRequest = function () {
    const w = new CamWriter();
    w.u8(this.version).u8(CAM_MSG_ID_StreamListResponse);
    w.u16(CAM_STREAM_FRAME_SOURCE_TYPE_Color); // FrameSourceTypes
    w.u8(CAM_STREAM_CATEGORY_Capture);         // StreamCategory
    w.u8(1);                                   // Selected
    w.u8(0);                                   // CanBeShared
    this.send(w.arr());
    this._log("dev: StreamListResponse");
};

// MediaTypeListRequest (+streamIndex) → MediaTypeListResponse ([MS-RDPECAM] 2.2.3.5): our MJPG media
// types. We remember the first as the default current media type.
RdpCamDevice.prototype._onMediaTypeListRequest = function (body) {
    this.streamIndex = body.length >= 1 ? body[0] : 0;
    const w = new CamWriter();
    w.u8(this.version).u8(CAM_MSG_ID_MediaTypeListResponse);
    for (const mt of CAM_MEDIA_TYPES) camWriteMediaType(w, mt);
    this.send(w.arr());
    if (!this.currentMediaType) this.currentMediaType = CAM_MEDIA_TYPES[0];
    this._log("dev: MediaTypeListResponse (" + CAM_MEDIA_TYPES.length + " types)");
};

// CurrentMediaTypeRequest → CurrentMediaTypeResponse ([MS-RDPECAM] 2.2.3.7): the active media type.
RdpCamDevice.prototype._onCurrentMediaTypeRequest = function (body) {
    const mt = this.currentMediaType || CAM_MEDIA_TYPES[0];
    const w = new CamWriter();
    w.u8(this.version).u8(CAM_MSG_ID_CurrentMediaTypeResponse);
    camWriteMediaType(w, mt);
    this.send(w.arr());
    this._log("dev: CurrentMediaTypeResponse " + mt.width + "x" + mt.height);
};

// StartStreamsRequest ([MS-RDPECAM] 2.2.3.8): streamIndex + a 30-byte media type. Begin capture at the
// chosen resolution, ack SuccessResponse, and tell client.js to start the camera.
RdpCamDevice.prototype._onStartStreams = function (body) {
    if (body.length < 1 + CAM_MEDIA_TYPE_SIZE) { this._sendError(0x00000002 /* InvalidMessage */); return; }
    const view = new DataView(body.buffer, body.byteOffset, body.byteLength);
    this.streamIndex = view.getUint8(0);
    const mt = camReadMediaType(view, 1);
    this.currentMediaType = { width: mt.width, height: mt.height, fps: mt.fps || 30, fmt: mt.format };
    this.streaming = true;
    this._pendingRequests = 0;
    this._lastFrame = null;
    this._sendGeneric(CAM_MSG_ID_SuccessResponse);
    this._log("dev: StartStreams " + mt.width + "x" + mt.height + "@" + this.currentMediaType.fps + " fmt=" + mt.format);
    if (this.cb.onStart) this.cb.onStart(this.currentMediaType);
};

RdpCamDevice.prototype._stop = function () {
    if (!this.streaming) return;
    this.streaming = false;
    this._pendingRequests = 0;
    this._lastFrame = null;
    if (this.cb.onStop) this.cb.onStop();
    this._log("dev: stream stopped");
};

// SampleRequest (+streamIndex): the host wants a frame. The host opens several device channels for the
// same camera and may pull samples on a channel that didn't itself receive StartStreams — so we DON'T
// reject on a non-streaming channel (that caused an ERROR-and-retry loop). We mark this channel as a
// sample sink, record the request, and ask client.js for a frame (which starts/continues capture).
RdpCamDevice.prototype._onSampleRequest = function (body) {
    const streamIndex = body.length >= 1 ? body[0] : 0;
    if (!this.streaming) {
        // Adopt the current media type so capture has dimensions, and accept (no error → no loop).
        this.streaming = true;
        if (!this.currentMediaType) this.currentMediaType = CAM_MEDIA_TYPES[0];
    }
    this._pendingRequests++;
    this._flushIfPossible(streamIndex);
    if (this.cb.onSampleNeeded) this.cb.onSampleNeeded(streamIndex);
};

// client.js delivers a freshly captured sample (raw pixel bytes in the negotiated format); satisfy any
// outstanding SampleRequests.
RdpCamDevice.prototype.pushFrame = function (frameBytes) {
    if (!frameBytes || !frameBytes.length) return;
    this._lastFrame = frameBytes;
    this._flushIfPossible(this.streamIndex);
};

// Send a SampleResponse ([MS-RDPECAM] 2.2.3.10) for each pending request we can satisfy: header +
// streamIndex(1) + the sample bytes (raw pixels in the negotiated format).
RdpCamDevice.prototype._flushIfPossible = function (streamIndex) {
    while (this.streaming && this._pendingRequests > 0 && this._lastFrame) {
        const w = new CamWriter();
        w.u8(this.version).u8(CAM_MSG_ID_SampleResponse).u8(streamIndex & 0xff);
        w.bytes(this._lastFrame);
        this.send(w.arr());
        this._pendingRequests--;
        // Consume the frame: a request maps to one captured frame. The next request waits for the next
        // pushFrame so we don't re-send a stale image as a new frame.
        this._lastFrame = null;
    }
};

RdpCamDevice.prototype.isStreaming = function () { return this.streaming; };
