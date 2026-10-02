// The live connection: one WebSocket to /api/v2/events. Keeps the last sequence number so a reconnect
// resumes exactly where it left off; the server answers "reset" when too much was missed, and listeners
// then refetch. Reconnects with backoff, and immediately when the tab becomes visible again.

const handlers = new Map(); // type → Set<fn(event)>
const stateListeners = new Set();
let socket = null;
let topics = new Set(["servers", "jobs", "metrics", "alerts"]);
let lastSeq = null;
let state = "offline"; // connecting | live | offline
let retry = 0;
let retryTimer = null;
let stopped = true;

export function on(type, fn) {
    if (!handlers.has(type)) handlers.set(type, new Set());
    handlers.get(type).add(fn);
    return () => handlers.get(type).delete(fn);
}

export function onState(fn) { stateListeners.add(fn); fn(state); return () => stateListeners.delete(fn); }
export function connectionState() { return state; }

function setState(s) {
    if (s === state) return;
    state = s;
    for (const fn of stateListeners) fn(s);
}

function emit(type, event) {
    for (const fn of handlers.get(type) || []) {
        try { fn(event); } catch (e) { console.error("live handler failed", type, e); }
    }
    for (const fn of handlers.get("*") || []) {
        try { fn(event); } catch (e) { console.error("live handler failed", e); }
    }
}

function send(obj) {
    if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(obj));
}

function subscribe(since) {
    send({ type: "subscribe", topics: [...topics], since: since ?? undefined });
}

/** Adds topics (e.g. "console:local/7") for as long as the returned function isn't called. */
export function watch(...extra) {
    const added = extra.filter(t => !topics.has(t));
    added.forEach(t => topics.add(t));
    if (added.length) subscribe();
    return () => {
        added.forEach(t => topics.delete(t));
        if (added.length) subscribe();
    };
}

export function start() {
    stopped = false;
    connect();
}

export function stop() {
    stopped = true;
    clearTimeout(retryTimer);
    if (socket) { socket.onclose = null; socket.close(); socket = null; }
    lastSeq = null;
    setState("offline");
}

function connect() {
    if (stopped || socket) return;
    setState("connecting");
    const url = (location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/api/v2/events";
    let ws;
    try { ws = new WebSocket(url); } catch { scheduleRetry(); return; }
    socket = ws;
    ws.onopen = () => { retry = 0; };
    ws.onmessage = e => {
        let msg;
        try { msg = JSON.parse(e.data); } catch { return; }
        if (msg.type === "hello") {
            // First connection: from now. Reconnect: from the last event seen. Either way `since` also covers
            // anything that happened between "hello" and our subscribe arriving.
            const first = lastSeq == null;
            lastSeq = first ? msg.data.seq : lastSeq;
            subscribe(lastSeq);
            setState("live");
            emit(first ? "connected" : "reconnected", msg);
            return;
        }
        if (msg.type === "reset") { lastSeq = msg.data.seq; emit("reset", msg); return; }
        if (msg.seq) lastSeq = Math.max(lastSeq ?? 0, msg.seq);
        emit(msg.type, msg);
    };
    ws.onclose = e => {
        socket = null;
        if (stopped) return;
        setState("offline");
        if (e.code === 4401) { emit("signedOut", {}); return; } // session revoked — don't retry
        scheduleRetry();
    };
    ws.onerror = () => { /* onclose follows */ };
}

function scheduleRetry() {
    clearTimeout(retryTimer);
    const delay = Math.min(15000, 500 * Math.pow(1.8, retry++)) + Math.random() * 400;
    retryTimer = setTimeout(connect, delay);
}

document.addEventListener("visibilitychange", () => {
    if (!document.hidden && !stopped && !socket) { retry = 0; clearTimeout(retryTimer); connect(); }
});
window.addEventListener("online", () => { if (!stopped && !socket) { retry = 0; connect(); } });
