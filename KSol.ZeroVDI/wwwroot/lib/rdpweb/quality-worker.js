// quality-worker.js — connection-quality sampling off the main thread.
//
// Owns its OWN WebSocket to /ws/rdp-quality/{sessionId} (a WebSocket cannot be shared with the page's
// session socket), so both the ping timer and the pong timestamping run on this worker thread: a busy
// main thread (progressive decode, large paints) can no longer inflate the RTT reading or starve the
// probe interval.
//
// Each pong from the gateway carries:
//   t       — our own send timestamp echoed back  → browserRtt = now - t   (web ↔ gateway leg)
//   hostRtt — the relay's sampled gateway→host RTT in ms (null while unknown)  (gateway ↔ host leg)
//   bytes   — total bytes the relay has sent to the browser → throughput from the delta between pongs
//
// The published rtt is the END-TO-END estimate browserRtt + hostRtt; the split is included so the UI
// can show which leg is slow. Messages to the page: {rtt, browserRtt, hostRtt, kbps, level}.

var PING_INTERVAL_MS = 3000;
var PING_TIMEOUT_MS = 8000;   // stale in-flight pings are dropped so a lost pong doesn't wedge RTT
var RECONNECT_MS = 5000;
var MAX_RECONNECTS = 5;       // the session socket restarts us on reconnect; don't hammer a dead session

var ws = null, timer = null, url = null;
var seq = 0, sentAt = {};     // seq -> performance.now() at send
var reconnectsLeft = MAX_RECONNECTS;
var q = { browserRtt: null, hostRtt: null, kbps: null, lastBytes: null, lastBytesAt: 0 };

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
    q.lastBytes = null;                 // byte counter is per-session; don't compute a delta across sessions
    try { ws = new WebSocket(url); } catch (e) { scheduleReconnect(); return; }
    ws.onopen = function () {
        reconnectsLeft = MAX_RECONNECTS;
        timer = setInterval(tick, PING_INTERVAL_MS);
        tick();
    };
    ws.onmessage = function (ev) { if (typeof ev.data === "string") onPong(ev.data); };
    ws.onclose = function () { stopTimer(); ws = null; scheduleReconnect(); };
    ws.onerror = function () { /* onclose follows and handles it */ };
}

function close() {
    stopTimer();
    if (ws) {
        ws.onclose = null; ws.onerror = null; ws.onmessage = null; ws.onopen = null;
        try { ws.close(); } catch (e) { /* ignore */ }
        ws = null;
    }
    sentAt = {};
}

function stopTimer() { if (timer) { clearInterval(timer); timer = null; } }

function scheduleReconnect() {
    // The gateway closes this socket when the session ends; the page also sends "stop" then, but the
    // close usually races ahead. Bounded retries cover transient drops without polling a dead session.
    if (!url || reconnectsLeft-- <= 0) return;
    setTimeout(function () { if (url && !ws) open(); }, RECONNECT_MS);
}

function tick() {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    var now = performance.now();
    for (var s in sentAt) { if (now - sentAt[s] > PING_TIMEOUT_MS) delete sentAt[s]; }
    seq++;
    sentAt[seq] = now;
    try { ws.send(JSON.stringify({ type: "ping", t: now, seq: seq })); } catch (e) { /* ignore */ }
}

function onPong(text) {
    var msg;
    try { msg = JSON.parse(text); } catch (e) { return; }
    if (msg.type !== "pong" || typeof msg.t !== "number") return;
    var now = performance.now();
    if (msg.seq != null) delete sentAt[msg.seq];

    q.browserRtt = now - msg.t;
    q.hostRtt = (typeof msg.hostRtt === "number") ? msg.hostRtt : null;

    // Throughput: gateway-side relayed-byte counter delta over the inter-pong interval. Measured at the
    // gateway rather than in the page's onmessage so the reading costs the main thread nothing; WS
    // backpressure means the gateway's send rate tracks what the browser actually receives.
    if (typeof msg.bytes === "number") {
        if (q.lastBytes != null && now > q.lastBytesAt) {
            var elapsedS = (now - q.lastBytesAt) / 1000;
            q.kbps = ((msg.bytes - q.lastBytes) * 8 / 1000) / elapsedS;
        }
        q.lastBytes = msg.bytes;
        q.lastBytesAt = now;
    }
    publish();
}

// Thresholds are RTT/throughput heuristics for an interactive desktop session (RDP GFX), not raw link
// speed: >150ms RTT or <256kbps is where cursor lag / progressive-tile catch-up becomes visible. The
// rtt judged here is the full web→gateway→host round trip.
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
