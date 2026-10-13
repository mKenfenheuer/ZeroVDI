// quality-worker.js — connection-quality sampling off the main thread.
//
// Owns its OWN WebSocket to /ws/rdp-quality/{sessionId} (a WebSocket cannot be shared with the page's
// session socket), so both the ping/speed-test timers and the pong timestamping run on this worker
// thread: a busy main thread (progressive decode, large paints) can no longer inflate the RTT reading
// or starve the probes.
//
// Quality is derived from exactly two active measurements, nothing else:
//   - end-to-end RTT: browserRtt (this worker's own ping/pong) + hostRtt (the relay's sampled
//     gateway→host RTT, carried in each pong)
//   - throughput: a periodic ACTIVE speed test — we ask the gateway for a bounded burst of bytes and
//     time the transfer. An idle RDP session relays almost no bytes on its own, so passively counting
//     session traffic used to report "poor" throughput on a perfectly healthy but quiet connection;
//     actively pulling a burst gives a real reading regardless of desktop activity.
//
// Messages to the page: {rtt, browserRtt, hostRtt, kbps, level}.

var PING_INTERVAL_MS = 3000;
var FIRST_SPEEDTEST_DELAY_MS = 4000; // let the session's initial full-screen paint finish first
var RTT_WINDOW = 5;              // median of the last N pings: one GC pause or retransmit doesn't flip the level
// Speed-test burst sizing: aim for a transfer of ~TARGET_TRANSFER_MS so the timing sits well above
// scheduling noise on fast links without spending seconds of a slow link's capacity on the probe.
var TARGET_TRANSFER_MS = 250;
var BURST_MIN = 64 * 1024, BURST_MAX = 4 * 1024 * 1024, BURST_DEFAULT = 256 * 1024;
var PING_TIMEOUT_MS = 8000;      // stale in-flight pings are dropped so a lost pong doesn't wedge RTT
var SPEEDTEST_INTERVAL_MS = 20000; // active burst cadence — infrequent enough to be negligible traffic
var RECONNECT_MS = 5000;
var MAX_RECONNECTS = 5;          // the session socket restarts us on reconnect; don't hammer a dead session

var ws = null, pingTimer = null, speedTimer = null, url = null;
var seq = 0, sentAt = {};        // seq -> performance.now() at send
var speedtestSeq = null, speedtestStartedAt = 0, firstSpeedTimer = null;
var rttSamples = [];
var burstBytes = BURST_DEFAULT;
var level = null, pendingLevel = null; // hysteresis: a new level must be seen twice in a row
var reconnectsLeft = MAX_RECONNECTS;
var q = { browserRtt: null, hostRtt: null, kbps: null };

onmessage = function (e) {
    var msg = e.data;
    if (msg.type === "start") {         // (re)start against a (possibly new) session's quality socket
        url = msg.url;
        reconnectsLeft = MAX_RECONNECTS;
        // A new session may run over a different path (server redirection): start the readings over.
        rttSamples = []; burstBytes = BURST_DEFAULT; level = null; pendingLevel = null;
        q = { browserRtt: null, hostRtt: null, kbps: null };
        open();
    } else if (msg.type === "stop") {
        url = null;
        close();
    }
};

function open() {
    close();
    if (!url) return;
    try { ws = new WebSocket(url); } catch (e) { scheduleReconnect(); return; }
    ws.binaryType = "arraybuffer";
    ws.onopen = function () {
        reconnectsLeft = MAX_RECONNECTS;
        pingTimer = setInterval(sendPing, PING_INTERVAL_MS);
        speedTimer = setInterval(sendSpeedtest, SPEEDTEST_INTERVAL_MS);
        sendPing();
        firstSpeedTimer = setTimeout(sendSpeedtest, FIRST_SPEEDTEST_DELAY_MS);
    };
    ws.onmessage = function (ev) {
        if (typeof ev.data === "string") onPong(ev.data);
        else onSpeedtestBurst(ev.data);
    };
    ws.onclose = function () { stopTimers(); ws = null; scheduleReconnect(); };
    ws.onerror = function () { /* onclose follows and handles it */ };
}

function close() {
    stopTimers();
    if (ws) {
        ws.onclose = null; ws.onerror = null; ws.onmessage = null; ws.onopen = null;
        try { ws.close(); } catch (e) { /* ignore */ }
        ws = null;
    }
    sentAt = {};
    speedtestSeq = null;
}

function stopTimers() {
    if (pingTimer) { clearInterval(pingTimer); pingTimer = null; }
    if (speedTimer) { clearInterval(speedTimer); speedTimer = null; }
    if (firstSpeedTimer) { clearTimeout(firstSpeedTimer); firstSpeedTimer = null; }
}

function scheduleReconnect() {
    // The gateway closes this socket when the session ends; the page also sends "stop" then, but the
    // close usually races ahead. Bounded retries cover transient drops without polling a dead session.
    if (!url || reconnectsLeft-- <= 0) return;
    setTimeout(function () { if (url && !ws) open(); }, RECONNECT_MS);
}

function sendPing() {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    var now = performance.now();
    for (var s in sentAt) { if (now - sentAt[s] > PING_TIMEOUT_MS) delete sentAt[s]; }
    seq++;
    sentAt[seq] = now;
    try { ws.send(JSON.stringify({ type: "ping", t: now, seq: seq })); } catch (e) { /* ignore */ }
}

function sendSpeedtest() {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    // Previous burst still in flight (slow link) — don't stack. Give up on it after a full interval: the
    // gateway drops requests that come too close together, and a lost one must not stop testing forever.
    if (speedtestSeq != null && performance.now() - speedtestStartedAt < SPEEDTEST_INTERVAL_MS) return;
    seq++;
    speedtestSeq = seq;
    speedtestStartedAt = performance.now();
    try { ws.send(JSON.stringify({ type: "speedtest", t: speedtestStartedAt, seq: speedtestSeq, bytes: burstBytes })); }
    catch (e) { speedtestSeq = null; }
}

function onPong(text) {
    var msg;
    try { msg = JSON.parse(text); } catch (e) { return; }
    if (msg.type !== "pong" || typeof msg.t !== "number") return;
    var now = performance.now();
    if (msg.seq != null) delete sentAt[msg.seq];

    rttSamples.push(now - msg.t);
    if (rttSamples.length > RTT_WINDOW) rttSamples.shift();
    q.browserRtt = median(rttSamples);
    q.hostRtt = (typeof msg.hostRtt === "number") ? msg.hostRtt : null;
    publish();
}

function onSpeedtestBurst(buf) {
    if (speedtestSeq == null) return; // stale/unexpected burst (e.g. after a reconnect); ignore
    // The gateway prefixes the burst with the request's seq: a late burst from an abandoned test must
    // not be timed against the current one.
    if (buf.byteLength < 8 || new DataView(buf).getUint32(0, true) !== (speedtestSeq >>> 0)) return;
    var elapsedMs = performance.now() - speedtestStartedAt;
    var bytes = buf.byteLength - 8; // first 8 bytes are the server's seq prefix, not payload
    speedtestSeq = null;
    if (!(elapsedMs > 0 && bytes > 0)) return;
    // The timer spans request → last byte, i.e. one round trip PLUS the transfer. Counting the round
    // trip as transfer time made a fast-but-distant link read far too slow (256 KB at 100 Mbit/s over a
    // 50 ms RTT measured ~29 Mbit/s). Subtract the smallest recent RTT (the latency floor), but never
    // credit the transfer with less than a fifth of the elapsed time so a stale RTT can't explode it.
    var latency = rttSamples.length ? Math.min.apply(null, rttSamples) : 0;
    var transferMs = Math.max(elapsedMs - latency, elapsedMs * 0.2);
    var sample = (bytes * 8) / transferMs; // bits per ms == kbit/s
    // Light smoothing: one burst that collided with a heavy screen update shouldn't swing the level.
    q.kbps = (q.kbps == null) ? sample : q.kbps * 0.4 + sample * 0.6;
    // Size the next burst for ~TARGET_TRANSFER_MS at the measured rate.
    var next = Math.round(sample * 1000 / 8 * TARGET_TRANSFER_MS / 1000);
    burstBytes = Math.max(BURST_MIN, Math.min(BURST_MAX, next));
    publish();
}

function median(a) {
    var s = a.slice().sort(function (x, y) { return x - y; });
    var m = s.length >> 1;
    return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
}

// Thresholds are RTT/throughput heuristics for an interactive desktop session (RDP GFX), not raw link
// speed: >150ms RTT or <256kbps is where cursor lag / progressive-tile catch-up becomes visible. The
// rtt judged here is the full web→gateway→host round trip; kbps is the active speed-test result, so
// idle desktop traffic never factors into the verdict.
function classify(rtt, kbps) {
    if (rtt == null && kbps == null) return null;
    if ((rtt != null && rtt > 300) || (kbps != null && kbps < 256)) return "poor";
    if ((rtt != null && rtt > 120) || (kbps != null && kbps < 1024)) return "fair";
    return "good";
}

function publish() {
    var rtt = (q.browserRtt != null) ? q.browserRtt + (q.hostRtt || 0) : q.hostRtt;
    var next = classify(rtt, q.kbps);
    // Hysteresis: adopt a different level only once two consecutive samples agree, so a reading that
    // sits right at a threshold doesn't make the indicator flicker. The first verdict is taken directly.
    if (level == null || next === level) { level = next; pendingLevel = null; }
    else if (pendingLevel === next) { level = next; pendingLevel = null; }
    else pendingLevel = next;
    postMessage({
        rtt: rtt,
        browserRtt: q.browserRtt,
        hostRtt: q.hostRtt,
        kbps: q.kbps,
        level: level,
    });
}
