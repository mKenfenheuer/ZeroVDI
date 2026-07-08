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
var PING_TIMEOUT_MS = 8000;      // stale in-flight pings are dropped so a lost pong doesn't wedge RTT
var SPEEDTEST_INTERVAL_MS = 20000; // active burst cadence — infrequent enough to be negligible traffic
var RECONNECT_MS = 5000;
var MAX_RECONNECTS = 5;          // the session socket restarts us on reconnect; don't hammer a dead session

var ws = null, pingTimer = null, speedTimer = null, url = null;
var seq = 0, sentAt = {};        // seq -> performance.now() at send
var speedtestSeq = null, speedtestStartedAt = 0;
var reconnectsLeft = MAX_RECONNECTS;
var q = { browserRtt: null, hostRtt: null, kbps: null };

onmessage = function (e) {
    var msg = e.data;
    if (msg.type === "start") {         // (re)start against a (possibly new) session's quality socket
        url = msg.url;
        reconnectsLeft = MAX_RECONNECTS;
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
        sendSpeedtest();
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
    if (speedtestSeq != null) return; // previous burst still in flight (slow/dead link) — don't stack
    seq++;
    speedtestSeq = seq;
    speedtestStartedAt = performance.now();
    try { ws.send(JSON.stringify({ type: "speedtest", t: speedtestStartedAt, seq: speedtestSeq })); }
    catch (e) { speedtestSeq = null; }
}

function onPong(text) {
    var msg;
    try { msg = JSON.parse(text); } catch (e) { return; }
    if (msg.type !== "pong" || typeof msg.t !== "number") return;
    var now = performance.now();
    if (msg.seq != null) delete sentAt[msg.seq];

    q.browserRtt = now - msg.t;
    q.hostRtt = (typeof msg.hostRtt === "number") ? msg.hostRtt : null;
    publish();
}

function onSpeedtestBurst(buf) {
    if (speedtestSeq == null) return; // stale/unexpected burst (e.g. after a reconnect); ignore
    var elapsedS = (performance.now() - speedtestStartedAt) / 1000;
    var bytes = buf.byteLength - 8; // first 8 bytes are the server's seq prefix, not payload
    speedtestSeq = null;
    if (elapsedS > 0 && bytes > 0) {
        q.kbps = (bytes * 8 / 1000) / elapsedS;
        publish();
    }
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
    postMessage({
        rtt: rtt,
        browserRtt: q.browserRtt,
        hostRtt: q.hostRtt,
        kbps: q.kbps,
        level: classify(rtt, q.kbps),
    });
}
