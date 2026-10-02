// The app's live model of the world: who you are, every machine you can see, their servers, jobs and pending
// questions. Loaded over REST, then kept current from the event stream — views never poll for server state.
//
// Everything server- or job-shaped is keyed "machine/id" (see key()), because two machines can each have a
// server #1. Views subscribe with store.on("servers" | "server:<key>" | "jobs" | "prompts" | "machines" |
// "metrics:<key>" | "players:<key>" | "console:<key>" | "job:<key>" | "log" | "alert" | "me", fn).

import { get, post, srv } from "./api.js";
import * as live from "./live.js";

const HISTORY_POINTS = 720; // 1 hour of 5-second samples
const NOTIFICATIONS = 100;
const listeners = new Map();

/** "m-abc/7" — the identity of a server (or job, or prompt) across machines. */
export const key = (machine, id) => `${machine}/${id}`;

export const store = {
    me: null,
    info: null,
    machine: null,          // the machine serving this UI (MachineDto)
    machines: new Map(),    // id → MachineDto (this one first)
    servers: new Map(),     // key → ServerDto
    jobs: new Map(),        // key → JobDto
    prompts: new Map(),     // key → PromptDto
    history: new Map(),     // server key → [{at, cpu, ram, players}]
    games: new Map(),       // machine → game list
    notifications: [],      // newest first (the last NOTIFICATIONS kept here)
    unread: 0,
    readUpTo: 0,
    loaded: false,

    on(topic, fn) {
        if (!listeners.has(topic)) listeners.set(topic, new Set());
        listeners.get(topic).add(fn);
        return () => listeners.get(topic)?.delete(fn);
    },

    emit(topic, payload) {
        for (const fn of listeners.get(topic) || []) {
            try { fn(payload); } catch (e) { console.error("store listener failed", topic, e); }
        }
    },

    get localId() { return this.machine?.id; },
    get multiMachine() { return this.machines.size > 1; },
    server(machine, id) { return this.servers.get(key(machine, id)) || null; },
    machineOf(id) { return this.machines.get(id) || null; },
    machineName(id) { return this.machines.get(id)?.name || id; },
    isOnline(id) { return this.machines.get(id)?.online !== false; },

    /** This machine's servers first, then each other machine's (by name), each by number. */
    sortedServers(machine = null) {
        const order = [...this.machines.keys()];
        return [...this.servers.values()]
            .filter(s => !machine || s.machine === machine)
            .sort((a, b) => order.indexOf(a.machine) - order.indexOf(b.machine) || Number(a.id) - Number(b.id));
    },
    runningJobs() { return [...this.jobs.values()].filter(j => j.status === "Running"); },
    jobsNewestFirst() { return [...this.jobs.values()].sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt)); },

    /** Loads everything after sign-in and starts the live connection. */
    async load() {
        const [me, machines] = await Promise.all([get("/auth/me"), get("/machines")]);
        this.me = me;
        this.setMachines(machines);
        await Promise.all([...machines.map(m => this.loadMachine(m.id).catch(() => { })), this.loadNotifications().catch(() => { })]);
        this.loaded = true;
        wireLive();
        live.start();
        for (const t of ["machines", "servers", "jobs", "prompts", "me", "notifications"]) this.emit(t);
    },

    unload() {
        live.stop();
        unwire();
        this.me = null;
        this.machine = null;
        for (const m of [this.machines, this.servers, this.jobs, this.prompts, this.history, this.games]) m.clear();
        this.notifications = [];
        this.unread = 0;
        this.loaded = false;
    },

    setMachines(list) {
        this.machines = new Map(list.map(m => [m.id, m]));
        this.machine = list.find(m => m.isLocal) || list[0] || null;
    },

    /** (Re)loads one machine's servers — the last known ones while it's offline — and its jobs and questions. */
    async loadMachine(machine) {
        const online = this.isOnline(machine);
        const [servers, jobs, prompts] = await Promise.all([
            get(`/machines/${encodeURIComponent(machine)}/servers`),
            online ? get(`/machines/${encodeURIComponent(machine)}/jobs?limit=60`).catch(() => []) : [],
            online ? get(`/machines/${encodeURIComponent(machine)}/prompts`).catch(() => []) : [],
        ]);
        replaceFor(this.servers, machine, servers, s => key(machine, s.id), s => ({ ...s, machine }));
        replaceFor(this.jobs, machine, jobs, j => key(machine, j.id), j => ({ ...j, machine }));
        replaceFor(this.prompts, machine, prompts, p => key(machine, p.id), p => ({ ...p, machine }));
        this.emit("servers");
        this.emit("jobs");
        this.emit("prompts");
        for (const s of servers) this.emit("server:" + key(machine, s.id), this.server(machine, s.id));
    },

    async refreshMachines() {
        this.setMachines(await get("/machines"));
        this.emit("machines");
        this.emit("machine", this.machine);
        return this.machines;
    },

    /** Kept for views that only care about the local machine's metrics. */
    async refreshMachine() { await this.refreshMachines(); return this.machine; },

    async refreshServer(machine, id) {
        try {
            const s = await get(srv(machine, id));
            this.servers.set(key(machine, id), { ...s, machine });
            this.emit("server:" + key(machine, id), this.server(machine, id));
        } catch (e) {
            if (e.status === 404) { this.servers.delete(key(machine, id)); this.emit("server:" + key(machine, id), null); }
            else if (e.status !== 503) throw e;
        }
        this.emit("servers");
    },

    async loadGames(machine = this.localId, force = false) {
        if (!this.games.has(machine) || force) this.games.set(machine, await get(`/machines/${encodeURIComponent(machine)}/games`));
        return this.games.get(machine);
    },

    /** Pushes a job the UI just started, so it shows before the first event arrives. */
    trackJob(job, machine = job?.machine) {
        if (!job) return;
        job = { ...job, machine: machine || this.localId };
        const k = key(job.machine, job.id);
        // The live stream often delivers the job's progress before the HTTP response that started it
        // arrives — never let that older snapshot overwrite newer live state.
        if (!this.jobs.has(k)) this.jobs.set(k, job);
        this.emit("jobs");
        this.emit("job:" + k, this.jobs.get(k));
    },

    async loadNotifications() {
        const page = await get(`/notifications?limit=${NOTIFICATIONS}`);
        this.notifications = page.items;
        this.unread = page.unread;
        this.readUpTo = page.readUpTo;
        this.emit("notifications");
    },

    /** Marks everything up to the newest notification as read (for this person, on every device). */
    async markNotificationsRead() {
        const newest = this.notifications[0]?.id || 0;
        if (!newest || newest <= this.readUpTo) { this.unread = 0; this.emit("notifications"); return; }
        this.readUpTo = newest;
        this.unread = 0;
        this.emit("notifications");
        try { const res = await post("/notifications/read", { upTo: newest }); this.unread = res.unread; this.emit("notifications"); } catch { /* next load corrects it */ }
    },

    historyOf(serverKey) { return this.history.get(serverKey) || []; },

    seedHistory(serverKey, samples) {
        const mapped = samples.map(s => ({ at: Date.parse(s.at), cpu: s.cpuPercent, ram: s.memoryMb, players: s.players }));
        const existing = this.historyOf(serverKey);
        const newest = mapped.length ? mapped[mapped.length - 1].at : 0;
        this.history.set(serverKey, mapped.concat(existing.filter(p => p.at > newest)).slice(-HISTORY_POINTS));
    },
};

function replaceFor(map, machine, items, keyOf, shape) {
    for (const k of [...map.keys()]) if (k.startsWith(machine + "/")) map.delete(k);
    for (const item of items) map.set(keyOf(item), shape(item));
}

// ─────────────────────────── Live wiring ───────────────────────────

const refreshTimers = new Map();
function refreshSoon(machine, id) {
    const k = key(machine, id);
    clearTimeout(refreshTimers.get(k));
    refreshTimers.set(k, setTimeout(() => { refreshTimers.delete(k); store.refreshServer(machine, id).catch(() => { }); }, 120));
}

let unsubs = [];
function unwire() { unsubs.forEach(u => u()); unsubs = []; }

function wireLive() {
    unwire();
    unsubs = [
        live.on("serverState", e => {
            const s = store.server(e.machine, e.server);
            if (s) { s.state = e.data.to; store.emit("server:" + key(e.machine, e.server), s); store.emit("servers"); }
            refreshSoon(e.machine, e.server);
        }),
        live.on("serverList", e => {
            if (e.data.removed) {
                store.servers.delete(key(e.machine, e.server));
                store.emit("servers");
                store.emit("server:" + key(e.machine, e.server), null);
            } else refreshSoon(e.machine, e.server);
        }),
        live.on("serverConfig", e => refreshSoon(e.machine, e.server)),
        live.on("metrics", e => {
            const d = e.data;
            const k = key(e.machine, e.server);
            const s = store.servers.get(k);
            if (s) {
                s.cpuPercent = d.cpuPercent;
                s.memoryMb = d.memoryMb;
                if (d.players != null) s.players = d.players;
                if (d.maxPlayers != null) s.maxPlayers = d.maxPlayers;
            }
            const list = store.history.get(k) || [];
            list.push({ at: Date.parse(d.at), cpu: d.cpuPercent, ram: d.memoryMb, players: d.players });
            if (list.length > HISTORY_POINTS) list.splice(0, list.length - HISTORY_POINTS);
            store.history.set(k, list);
            store.emit("metrics:" + k, d);
            store.emit("metrics", e);
        }),
        live.on("players", e => store.emit("players:" + key(e.machine, e.server), e.data)),
        live.on("job", e => {
            const job = { ...e.data, machine: e.machine };
            const k = key(e.machine, job.id);
            const before = store.jobs.get(k);
            store.jobs.set(k, job);
            store.emit("jobs");
            store.emit("job:" + k, job);
            if (before && before.status === "Running" && job.status !== "Running") {
                store.emit("jobFinished", job);
                if (job.serverId) refreshSoon(e.machine, job.serverId);
            }
        }),
        live.on("prompt", e => {
            const p = { ...e.data, machine: e.machine };
            store.prompts.set(key(e.machine, p.id), p);
            store.emit("prompts");
            store.emit("promptRaised", p);
        }),
        live.on("promptResolved", e => { store.prompts.delete(key(e.machine, e.data.promptId)); store.emit("prompts"); }),
        live.on("alert", e => store.emit("alert", e)),
        live.on("console", e => store.emit("console:" + key(e.machine, e.server), e.data.line)),
        live.on("log", e => store.emit("log", e)),
        live.on("notification", e => {
            const n = e.data;
            if (store.notifications.some(x => x.id === n.id)) return;
            store.notifications.unshift(n);
            if (store.notifications.length > NOTIFICATIONS) store.notifications.length = NOTIFICATIONS;
            if (n.id > store.readUpTo) store.unread++;
            store.emit("notifications");
            store.emit("notificationAdded", n);
        }),
        // A machine connected or went away: refresh the list and what it's running.
        live.on("machine", e => {
            store.refreshMachines().then(() => store.loadMachine(e.machine)).catch(() => { });
        }),
        live.on("reset", () => {
            store.refreshMachines().then(ms => Promise.all([...ms.keys()].map(m => store.loadMachine(m)))).catch(() => { });
            store.loadNotifications().catch(() => { });
            store.emit("reset");
        }),
    ];
}
