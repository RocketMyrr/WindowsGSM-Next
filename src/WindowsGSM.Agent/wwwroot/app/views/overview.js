// Overview: machine health at a glance, what needs attention, and every server as a live card. With several
// machines it shows all of them (grouped, each with its own health card) or just one (?machine=id).

import { h, icon, clear, append, gameLabel, gameTile, gameBanner, fmtMb, copyText, debounce, timeAgo } from "../dom.js";
import { store, key } from "../store.js";
import { get, srv } from "../api.js";
import { setCrumbs, openJobs } from "../shell.js";
import { navigate, serverPath } from "../router.js";
import { statusPill, statusOf, meter, sparkline, segmented, showMenu, toast, empty, isRunning, isStopped, isBusy, confirm } from "../ui.js";
import { ACTIONS, runAction, runBulk, allTags, editTags } from "../actions.js";
import { promptText } from "../ui.js";
import { can, canInstall } from "../perms.js";

const PREFS_KEY = "wgsm-overview";
const machineHistory = new Map(); // machine → { cpu: [], ram: [] }; survives navigation within the session
const historyFor = m => { if (!machineHistory.has(m)) machineHistory.set(m, { cpu: [], ram: [] }); return machineHistory.get(m); };

function loadPrefs() {
    try { return { filter: "all", sort: "id", view: "grid", ...JSON.parse(localStorage.getItem(PREFS_KEY) || "{}") }; }
    catch { return { filter: "all", sort: "id", view: "grid" }; }
}
function savePrefs(p) { try { localStorage.setItem(PREFS_KEY, JSON.stringify(p)); } catch { /* private mode */ } }

// Saved views: named combinations of filter, tag, search, sort and layout (this browser only).
const VIEWS_KEY = "wgsm-views";
function loadViews() { try { return JSON.parse(localStorage.getItem(VIEWS_KEY) || "[]"); } catch { return []; } }
function saveViews(v) { try { localStorage.setItem(VIEWS_KEY, JSON.stringify(v)); } catch { /* private mode */ } }

export default async function overview(host, { scope, query }) {
    const prefs = loadPrefs();
    let tag = query.get("tag") || null;
    let views = loadViews();
    // One machine in focus (?machine=id), or all of them.
    let focus = store.multiMachine && store.machines.has(query.get("machine")) ? query.get("machine") : null;
    const single = () => !store.multiMachine || focus != null;
    const focusMachine = () => focus ? store.machineOf(focus) : store.machine;
    const inScope = x => !focus || x.machine === focus;
    const scoped = () => [...store.servers.values()].filter(inScope);
    const paintCrumbs = () => focus
        ? setCrumbs({ label: "Overview", href: "/" }, { label: store.machineName(focus) })
        : setCrumbs({ label: "Overview" });
    paintCrumbs();
    const selected = new Set();
    let search = "";

    const hour = new Date().getHours();
    const greeting = hour < 5 ? "Up late" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";

    // ── Header ──
    const subtitle = h("p");
    const head = h("div", { class: "page-head" },
        h("div", {},
            h("h1", { text: `${greeting}, ${store.me.username}` }),
            subtitle),
        h("div", { class: "actions" },
            h("a", { class: "btn", href: "/health", hidden: !store.me.canManageUsers }, icon("checkCircle"), "Health checks")));

    const welcome = query.get("welcome") ? welcomeCard() : null;

    // ── KPIs ──
    const kpis = h("div", { class: "kpis" });
    const machineStrip = h("section", { class: "machine-strip", "aria-label": "Machines" });

    // ── Attention ──
    const attention = h("section", { class: "attention", "aria-label": "Needs attention" });

    // ── Servers toolbar ──
    const counts = () => {
        const all = scoped();
        return { all: all.length, running: all.filter(isRunning).length, stopped: all.filter(isStopped).length, busy: all.filter(isBusy).length };
    };
    const filter = segmented([
        { value: "all", label: "All" },
        { value: "running", label: "Running" },
        { value: "stopped", label: "Stopped" },
        { value: "busy", label: "Busy" },
    ].map(o => ({ ...o, count: 0 })), prefs.filter, v => { prefs.filter = v; savePrefs(prefs); paintServers(); });
    const searchBox = h("input", { class: "input", type: "search", placeholder: "Search servers, games, ports…", "aria-label": "Search servers" });
    searchBox.addEventListener("input", debounce(() => { search = searchBox.value.trim().toLowerCase(); paintServers(); }, 120));
    const sortSel = h("select", { class: "input", "aria-label": "Sort servers" },
        ...[["id", "Sort: number"], ["name", "Sort: name"], ["status", "Sort: status"], ["players", "Sort: players"], ["cpu", "Sort: CPU"]]
            .map(([v, l]) => h("option", { value: v, selected: prefs.sort === v }, l)));
    sortSel.addEventListener("change", () => { prefs.sort = sortSel.value; savePrefs(prefs); paintServers(); });
    const viewToggle = segmented([{ value: "grid", label: "", icon: "grid" }, { value: "list", label: "", icon: "logs" }], prefs.view, v => { prefs.view = v; savePrefs(prefs); paintServers(); });
    viewToggle.querySelectorAll("button")[0].setAttribute("aria-label", "Cards");
    viewToggle.querySelectorAll("button")[1].setAttribute("aria-label", "List");

    const bulkBar = h("div", { class: "bulk-bar", hidden: true });
    const filterRow = h("div", { class: "filter-row", hidden: true });
    const grid = h("div", { class: "server-grid" });

    append(host, [
        head,
        welcome,
        kpis,
        machineStrip,
        attention,
        h("section", { class: "servers-section", "aria-label": "Servers" },
            h("div", { class: "toolbar" },
                h("h2", { text: "Servers" }),
                filter,
                h("div", { class: "search grow" }, icon("search"), searchBox),
                sortSel,
                viewToggle,
                h("button", { class: "btn icon-only", title: "Actions for every server shown", "aria-label": "Actions for every server shown", "aria-haspopup": "menu",
                    onclick: e => fleetMenu(e.currentTarget, scoped(), focus ? `on ${store.machineName(focus)}` : store.multiMachine ? "on every machine" : "") }, icon("more"))),
            filterRow,
            bulkBar,
            grid)]);

    // ── Painting ──

    function paintHead() {
        const n = scoped().length;
        const servers = `${n} server${n === 1 ? "" : "s"}`;
        if (single()) { subtitle.textContent = `${focusMachine()?.name || "This machine"} · ${servers}`; return; }
        const offline = [...store.machines.values()].filter(m => m.online === false).length;
        subtitle.textContent = `${store.machines.size} machines${offline ? ` (${offline} offline)` : ""} · ${servers}`;
    }

    // The most players online at once today (each machine's own peak, added up across machines).
    let peakToday = null;
    async function loadPeak() {
        const machines = [...store.machines.values()].filter(m => store.isOnline(m.id));
        const peaks = await Promise.all(machines.map(m => get(`/machines/${encodeURIComponent(m.id)}/summary`).then(r => r.peakToday).catch(() => 0)));
        peakToday = peaks.reduce((a, b) => a + b, 0);
        if (scope.alive) paintKpis();
    }
    scope.every(5 * 60000, loadPeak);

    function paintKpis() {
        const all = scoped();
        const running = all.filter(isRunning);
        const players = running.reduce((n, s) => n + (s.players || 0), 0);
        const capacity = running.reduce((n, s) => n + (s.maxPlayers || 0), 0);
        const serversKpi = kpi("servers", "Servers online", h("span", {}, h("b", { text: running.length }), h("small", { text: ` / ${all.length}` })),
            all.length ? `${all.filter(isBusy).length} busy · ${all.filter(isStopped).length} stopped` : "No servers yet", "accent", running.length / Math.max(1, all.length) * 100);
        const peakText = peakToday != null ? `peak today ${Math.max(peakToday, players)}` : null;
        const playersKpi = kpi("players", "Players online", h("span", {}, h("b", { text: players }), capacity ? h("small", { text: ` / ${capacity}` }) : null),
            [running.length ? `across ${running.length} running server${running.length === 1 ? "" : "s"}` : "No servers running", peakText].filter(Boolean).join(" · "), "cyan", capacity ? players / capacity * 100 : 0);
        kpis.classList.toggle("three", !single());
        if (!single()) {
            // Several machines: totals here, each machine's health on its card below.
            const machines = [...store.machines.values()];
            const online = machines.filter(m => m.online !== false).length;
            const allOn = online === machines.length;
            clear(kpis).append(serversKpi, playersKpi,
                kpi("machine", "Machines online", h("span", {}, h("b", { text: online }), h("small", { text: ` / ${machines.length}` })),
                    allOn ? "All connected" : `${machines.length - online} offline`, allOn ? "violet" : "rose", online / machines.length * 100));
            return;
        }
        const fm = focusMachine();
        const m = fm?.online === false ? null : fm?.metrics;
        const hist = historyFor(fm?.id);
        clear(kpis).append(
            serversKpi,
            playersKpi,
            kpiSpark("cpu", "CPU", m ? `${Math.round(m.cpuPercent)}%` : "—", m ? `${m.cores} cores${m.cpuName ? " · " + shortCpu(m.cpuName) : ""}` : fm?.online === false ? "Offline" : "Not available", hist.cpu, "cpu", 100),
            kpiSpark("memory", "Memory", m ? `${Math.round(m.ramPercent)}%` : "—", m ? `of ${m.ramTotalGb} GB` : "Not available", hist.ram, "ram", 100),
            kpi("disk", "Disk", h("span", {}, h("b", { text: m ? `${Math.round(m.diskPercent)}%` : "—" })),
                m ? `used of ${m.diskTotalGb} GB` : "Not available", m && m.diskPercent >= 90 ? "rose" : "violet", m ? m.diskPercent : 0));
    }

    function paintMachines() {
        clear(machineStrip);
        machineStrip.hidden = !store.multiMachine;
        if (machineStrip.hidden) return;
        for (const m of store.machines.values()) {
            const on = m.online !== false;
            const mm = on ? m.metrics : null;
            const servers = [...store.servers.values()].filter(s => s.machine === m.id);
            const running = servers.filter(isRunning).length;
            const hist = historyFor(m.id);
            const active = focus === m.id;
            machineStrip.append(h("button", { class: ["machine-card", !on && "offline", active && "active"], "aria-pressed": String(active),
                title: active ? "Show every machine" : `Show only ${m.name}`, onclick: () => setFocus(active ? null : m.id) },
                h("div", { class: "machine-card-head" },
                    h("span", { class: "machine-icon" }, icon("machine")),
                    h("div", { class: "grow truncate" },
                        h("b", { class: "truncate", text: m.name }),
                        h("div", { class: "tiny muted truncate" }, h("span", { class: ["dot", on ? "st-running" : "st-bad"] }),
                            on ? ` ${running} of ${servers.length} running${m.isLocal ? " · this machine" : ""}` : ` Offline${m.lastSeen ? " · seen " + timeAgo(m.lastSeen) : ""}`)),
                    on && hist.cpu.length > 1 ? h("div", { class: "machine-card-spark" }, sparkline(hist.cpu, "cpu", 100)) : null),
                mm ? h("div", { class: "machine-card-meters" },
                    meter("CPU", mm.cpuPercent, `${Math.round(mm.cpuPercent)}%`, "cpu"),
                    meter("Memory", mm.ramPercent, `${Math.round(mm.ramPercent)}%`, "ram"),
                    meter("Disk", mm.diskPercent, `${Math.round(mm.diskPercent)}%`, "disk"))
                    : h("div", { class: "small faint machine-card-off", text: on ? "No readings yet." : "Showing its last known servers. Actions come back when it reconnects." })));
        }
    }

    function setFocus(machine) {
        focus = machine;
        history.replaceState({}, "", machine ? "/?machine=" + encodeURIComponent(machine) : "/");
        selected.clear();
        paintCrumbs();
        repaintAll();
    }

    function paintAttention() {
        const items = [];
        for (const m of store.machines.values()) {
            if (m.online !== false || (focus && focus !== m.id)) continue;
            items.push({ tone: "rose", icon: "machine", title: `${m.name} is offline`,
                text: `${m.lastSeen ? "Last seen " + timeAgo(m.lastSeen) + ". " : ""}Its servers show their last known state until it reconnects.`,
                action: store.me.canManageUsers ? ["Machines", () => navigate("/machines")] : null });
        }
        for (const p of [...store.prompts.values()].filter(inScope)) {
            items.push({ tone: "violet", icon: "help", title: "A job is waiting for your answer", text: p.title, action: ["Answer", () => openJobs()] });
        }
        const updates = scoped().filter(s => s.updateAvailable);
        if (updates.length) {
            const names = updates.slice(0, 3).map(s => s.name).join(", ") + (updates.length > 3 ? ` and ${updates.length - 3} more` : "");
            items.push({ tone: "violet", icon: "update", title: `${updates.length === 1 ? "An update is" : updates.length + " updates are"} waiting`, text: `${names}. Updating stops the server while it downloads.`,
                action: updates.length === 1 ? ["Open", () => navigate(serverPath(updates[0].machine, updates[0].id))] : null });
        }
        for (const s of scoped()) {
            if (s.crashLoopSuspended) items.push({ tone: "rose", icon: "warn", title: `${s.name} kept crashing`, text: "Auto-restart is paused until someone starts it again. Check its console and logs.", action: ["Open", () => navigate(serverPath(s.machine, s.id, "logs"))] });
        }
        const hourAgo = Date.now() - 3600_000;
        const failed = [...store.jobs.values()].filter(j => inScope(j) && j.status === "Failed" && Date.parse(j.endedAt || j.startedAt) > hourAgo);
        for (const j of failed.slice(0, 3)) items.push({ tone: "amber", icon: "xCircle", title: j.title + " failed", text: `${j.error || "See the activity panel."} · ${timeAgo(j.endedAt)}`, action: ["Details", () => openJobs()] });
        for (const mc of store.machines.values()) {
            const m = mc.online === false || (focus && focus !== mc.id) ? null : mc.metrics;
            if (m && m.diskPercent >= 90) items.push({ tone: "rose", icon: "disk", title: store.multiMachine ? `Disk nearly full on ${mc.name}` : "Disk nearly full", text: `${Math.round(m.diskPercent)}% used. Updates and backups may fail — clear space or move backups.`, action: null });
        }

        clear(attention);
        attention.hidden = items.length === 0;
        if (!items.length) return;
        attention.append(h("div", { class: "attention-head" }, icon("warn"), h("h2", { text: "Needs attention" }), h("span", { class: "count-badge", text: items.length })));
        attention.append(h("div", { class: "attention-list" }, ...items.map(i => h("div", { class: ["attention-item", i.tone] },
            h("span", { class: "attention-icon" }, icon(i.icon)),
            h("div", { class: "grow" }, h("b", { text: i.title }), h("div", { class: "small muted", text: i.text })),
            i.action ? h("button", { class: "btn sm", onclick: i.action[1] }, i.action[0]) : null))));
    }

    function visibleServers() {
        let list = scoped();
        if (prefs.filter === "running") list = list.filter(isRunning);
        if (prefs.filter === "stopped") list = list.filter(isStopped);
        if (prefs.filter === "busy") list = list.filter(isBusy);
        if (tag) list = list.filter(s => (s.tags || []).some(t => t.toLowerCase() === tag.toLowerCase()));
        if (search) list = list.filter(s => [s.name, s.game, s.id, s.port, s.ip, s.state, store.multiMachine ? store.machineName(s.machine) : null, ...(s.tags || [])].some(v => String(v || "").toLowerCase().includes(search)));
        const statusRank = s => (isRunning(s) ? 0 : isBusy(s) ? 1 : 2);
        const sorters = {
            id: (a, b) => Number(a.id) - Number(b.id),
            name: (a, b) => a.name.localeCompare(b.name),
            status: (a, b) => statusRank(a) - statusRank(b) || Number(a.id) - Number(b.id),
            players: (a, b) => (b.players || 0) - (a.players || 0),
            cpu: (a, b) => (b.cpuPercent || 0) - (a.cpuPercent || 0),
        };
        // Grouped by machine (this one first), sorted within each.
        const order = [...store.machines.keys()];
        const sort = sorters[prefs.sort] || sorters.id;
        return list.sort((a, b) => order.indexOf(a.machine) - order.indexOf(b.machine) || sort(a, b));
    }

    // ── Tags and saved views ──

    const currentView = () => ({ filter: prefs.filter, sort: prefs.sort, view: prefs.view, search: searchBox.value.trim(), tag, machine: focus });
    const sameView = (a, b) => ["filter", "sort", "view", "search", "tag", "machine"].every(k => (a[k] || null) === (b[k] || null));

    function applyView(v) {
        prefs.filter = v.filter || "all"; prefs.sort = v.sort || "id"; prefs.view = v.view || "grid";
        savePrefs(prefs);
        filter.set(prefs.filter);
        sortSel.value = prefs.sort;
        viewToggle.set(prefs.view);
        searchBox.value = v.search || "";
        search = searchBox.value.toLowerCase();
        tag = v.tag || null;
        if ((v.machine || null) !== focus && (!v.machine || store.machines.has(v.machine))) setFocus(v.machine || null);
        else repaintAll();
    }

    async function saveView() {
        const name = await promptText({ title: "Save this view", message: "Keeps the current filter, tag, search, sort and layout under a name (in this browser).", label: "Name", placeholder: "e.g. EU servers", confirmLabel: "Save", iconName: "save",
            validate: v => !v.trim() ? "Give it a name." : v.trim().length > 30 ? "Up to 30 characters." : null });
        if (!name) return;
        views = views.filter(v => v.name.toLowerCase() !== name.trim().toLowerCase()).concat({ name: name.trim(), ...currentView() });
        saveViews(views);
        filterRowKey = null;
        paintFilterRow();
    }

    let filterRowKey = null;
    function paintFilterRow() {
        const tags = allTags(scoped());
        if (tag && !tags.some(t => t.toLowerCase() === tag.toLowerCase())) tags.unshift(tag);
        const now = currentView();
        // Live metrics repaint the grid every second; this row only changes when tags, views or the filter do.
        const rowKey = JSON.stringify([tags, views, now]);
        if (rowKey === filterRowKey) return;
        filterRowKey = rowKey;
        clear(filterRow);
        filterRow.hidden = !tags.length && !views.length;
        if (filterRow.hidden) return;
        if (views.length) {
            filterRow.append(h("span", { class: "filter-label", text: "Views" }),
                ...views.map(v => h("span", { class: ["view-chip", sameView(v, now) && "active"] },
                    h("button", { type: "button", onclick: () => applyView(v), "aria-pressed": String(sameView(v, now)) }, icon("filter"), v.name),
                    h("button", { type: "button", class: "chip-x", "aria-label": `Forget view ${v.name}`, onclick: () => { views = views.filter(x => x !== v); saveViews(views); filterRowKey = null; paintFilterRow(); } }, icon("x")))));
        }
        if (tags.length) {
            filterRow.append(h("span", { class: "filter-label", text: "Tags" }),
                ...tags.map(t => {
                    const on = tag && tag.toLowerCase() === t.toLowerCase();
                    return h("button", { type: "button", class: ["tag chip", on && "accent"], "aria-pressed": String(!!on), onclick: () => { tag = on ? null : t; paintServers(); } }, t);
                }));
        }
        const dirty = !views.some(v => sameView(v, now)) && (tag || now.search || now.filter !== "all" || focus);
        append(filterRow, [h("span", { class: "spacer" }),
            dirty ? h("button", { type: "button", class: "btn ghost sm", onclick: saveView }, icon("save"), "Save view") : null]);
    }

    function paintServers() {
        paintFilterRow();
        const c = counts();
        for (const k of Object.keys(c)) filter.setCount(k, c[k]);
        grid.className = "server-grid " + prefs.view;
        const list = visibleServers();
        clear(grid);
        if (scoped().length === 0) {
            grid.append(empty("servers", focus ? `No servers on ${store.machineName(focus)} yet` : "No servers yet", "Install your first game server — pick a game, give it a name, and WindowsGSM downloads it and picks free ports for you.",
                canInstall(focus) ? h("a", { class: "btn primary", href: "/install" + (focus ? "?machine=" + encodeURIComponent(focus) : "") }, icon("plus"), "Install a server") : null));
        } else if (list.length === 0) {
            grid.append(empty("search", "No matching servers", "Try a different search or filter.", h("button", { class: "btn", onclick: () => { searchBox.value = ""; search = ""; filter.set("all"); prefs.filter = "all"; paintServers(); } }, "Clear filters")));
        } else {
            let group = null;
            for (const s of list) {
                // Several machines on screen: a heading wherever the machine changes.
                if (!single() && s.machine !== group) {
                    group = s.machine;
                    const m = store.machineOf(group);
                    grid.append(h("div", { class: "machine-group" }, icon("machine"), h("b", { text: m?.name || group }),
                        m?.online === false ? h("span", { class: "tag rose", text: "Offline" }) : null,
                        h("span", { class: "rule" }),
                        m?.online === false ? null : h("button", { class: "btn ghost sm icon-only", title: `Actions for every server on ${m?.name || group}`, "aria-label": `Actions for every server on ${m?.name || group}`, "aria-haspopup": "menu",
                            onclick: ev => { const mid = ev.currentTarget.dataset.machine; fleetMenu(ev.currentTarget, scoped().filter(x => x.machine === mid), `on ${store.machineName(mid)}`); }, dataset: { machine: group } }, icon("more"))));
                }
                grid.append(prefs.view === "list" ? serverRow(s) : serverCard(s));
            }
        }
        paintBulk();
    }

    /** Start / stop / restart / update / back up many servers at once (only where it applies and is allowed). */
    function fleetMenu(anchor, servers, where) {
        const eligible = a => servers.filter(s => can(s, ACTIONS[a].cap) && ACTIONS[a].when(s));
        const item = (a, label, danger = false) => {
            const list = eligible(a);
            return { label: `${label} (${list.length})`, icon: ACTIONS[a].icon, danger, disabled: list.length === 0, onClick: () => fleet(a, list, where) };
        };
        showMenu(anchor, [
            { heading: where ? `Every server ${where}` : "Every server" },
            item("start", "Start the stopped ones"),
            item("restart", "Restart the running ones"),
            item("stop", "Stop the running ones", true),
            "-",
            item("update", "Update the stopped ones"),
            item("backup", "Back up all"),
            "-",
            { label: "Select them all", icon: "check", onClick: () => { for (const s of servers) selected.add(key(s.machine, s.id)); paintServers(); } },
        ]);
    }

    async function fleet(action, list, where) {
        const def = ACTIONS[action];
        const n = list.length;
        const plural = `${n} server${n === 1 ? "" : "s"}`;
        if (action !== "start" && action !== "backup") {
            const names = list.slice(0, 6).map(s => s.name).join(", ") + (n > 6 ? ` and ${n - 6} more` : "");
            const warning = action === "stop" ? "Players on them are disconnected." : action === "restart" ? "Players are disconnected while they restart." : "Each is updated in turn; they stay stopped afterwards.";
            if (!(await confirm({ title: `${def.label} ${plural}${where ? " " + where : ""}?`, message: `${names}. ${warning}`, confirmLabel: `${def.label} ${plural}`, danger: action === "stop" }))) return;
        }
        await runBulk(list, action);
    }

    function toggleSelect(id, on) {
        if (on) selected.add(id); else selected.delete(id);
        paintBulk();
    }

    function paintBulk() {
        for (const k of [...selected]) if (!store.servers.has(k)) selected.delete(k);
        clear(bulkBar);
        bulkBar.hidden = selected.size === 0;
        if (!selected.size) return;
        const chosen = [...selected].map(k => store.servers.get(k));
        bulkBar.append(
            h("b", { text: `${selected.size} selected` }),
            ...["start", "stop", "restart", "update", "backup"].map(a => h("button", { class: "btn sm", onclick: async () => { await runBulk(chosen, a); } }, icon(ACTIONS[a].icon), ACTIONS[a].label)),
            h("span", { class: "spacer" }),
            h("button", { class: "btn ghost sm", onclick: () => { selected.clear(); paintServers(); } }, "Clear"));
    }

    function serverCard(s) {
        const { cls } = statusOf(s);
        const k = key(s.machine, s.id);
        const history = store.historyOf(k);
        const box = h("input", { type: "checkbox", class: "select-box", "aria-label": `Select ${s.name}`, checked: selected.has(k) });
        box.addEventListener("click", e => e.stopPropagation());
        box.addEventListener("change", () => toggleSelect(k, box.checked));
        const running = isRunning(s);
        const address = [s.ip, s.port].filter(Boolean).join(":");

        const card = h("article", { class: ["server-card", cls, selected.has(k) && "selected", !store.isOnline(s.machine) && "stale"], tabindex: "0", "aria-label": `${s.name}, ${statusOf(s).label}` },
            gameBanner(s.bannerUrl, "server-card-banner"),
            h("div", { class: "server-card-glow" }),
            h("header", { class: "server-card-head" },
                gameTile(s.game, s.iconUrl, "", s.artUrl),
                h("div", { class: "grow" },
                    h("h3", { class: "truncate", text: s.name }),
                    h("div", { class: "small muted truncate", text: `#${s.id} · ${gameLabel(s.game)}` }),
                    s.tags?.length ? h("div", { class: "card-tags" }, ...s.tags.slice(0, 3).map(t => h("span", { class: "tag chip sm", text: t })), s.tags.length > 3 ? h("span", { class: "tiny faint", text: `+${s.tags.length - 3}` }) : null) : null),
                box),
            h("div", { class: "row between" },
                h("span", { class: "row" }, statusPill(s), s.updateAvailable ? h("span", { class: "tag accent", title: "A game update is waiting" }, icon("update"), "Update") : null),
                address ? h("button", { class: "address", title: "Copy address", onclick: async e => { e.stopPropagation(); if (await copyText(address)) toast("Address copied", { type: "good", text: address, timeout: 2500 }); } }, icon("copy"), address) : null),
            running ? h("div", { class: "server-card-stats" },
                h("div", { class: "stat-players" },
                    h("span", { class: "upper", text: "Players" }),
                    h("b", { class: "num", text: s.players != null ? `${s.players}${s.maxPlayers ? " / " + s.maxPlayers : ""}` : "—" })),
                h("div", { class: "stat-spark" }, h("span", { class: "upper", text: `CPU ${s.cpuPercent != null ? Math.round(s.cpuPercent) + "%" : "—"}` }), sparkline(history.map(p => p.cpu), "cpu", 100)),
                h("div", { class: "stat-spark" }, h("span", { class: "upper", text: `RAM ${fmtMb(s.memoryMb)}` }), sparkline(history.map(p => p.ram), "ram")))
                : h("div", { class: "server-card-idle small faint", text: s.busyWith ? s.busyWith + "…" : isStopped(s) ? (s.autoStart ? "Starts automatically with the agent" : "Ready to start") : statusOf(s).label + "…" }),
            h("footer", { class: "server-card-actions" }, ...quickActions(s)));

        card.addEventListener("click", e => { if (!e.target.closest("button, a, input")) navigate(serverPath(s.machine, s.id)); });
        card.addEventListener("keydown", e => { if (e.key === "Enter" && e.target === card) navigate(serverPath(s.machine, s.id)); });
        return card;
    }

    function serverRow(s) {
        const { cls } = statusOf(s);
        const k = key(s.machine, s.id);
        const box = h("input", { type: "checkbox", class: "select-box", "aria-label": `Select ${s.name}`, checked: selected.has(k) });
        box.addEventListener("change", () => toggleSelect(k, box.checked));
        const row = h("div", { class: ["server-row", cls, !store.isOnline(s.machine) && "stale"] },
            box,
            gameTile(s.game, s.iconUrl, "sm", s.artUrl),
            h("a", { class: "grow truncate server-row-name", href: serverPath(s.machine, s.id) }, h("b", { text: s.name }), h("span", { class: "small faint", text: ` #${s.id} · ${gameLabel(s.game)}` })),
            statusPill(s),
            h("span", { class: "num small muted row-stat", text: isRunning(s) && s.players != null ? `${s.players}${s.maxPlayers ? "/" + s.maxPlayers : ""} players` : "" }),
            h("span", { class: "num small muted row-stat", text: isRunning(s) && s.cpuPercent != null ? `${Math.round(s.cpuPercent)}% · ${fmtMb(s.memoryMb)}` : "" }),
            h("div", { class: "row" }, ...quickActions(s, true)));
        return row;
    }

    function quickActions(s, compact = false) {
        const out = [];
        const primary = isRunning(s) && can(s, "Stop") ? "stop" : isStopped(s) && can(s, "Start") ? "start" : null;
        if (primary) {
            out.push(h("button", { class: ["btn sm", primary === "start" ? "primary" : ""], onclick: async e => { e.stopPropagation(); e.currentTarget.classList.add("busy"); try { await runAction(s, primary); } catch { /* toasted */ } } },
                icon(ACTIONS[primary].icon), compact ? null : ACTIONS[primary].label));
        } else if (isBusy(s)) {
            out.push(h("span", { class: "row small muted" }, h("span", { class: "spinner" }), compact ? null : (s.busyWith || statusOf(s).label)));
        }
        if (isRunning(s) && can(s, "Restart") && !compact) {
            out.push(h("button", { class: "btn sm ghost", title: "Restart", "aria-label": `Restart ${s.name}`, onclick: e => { e.stopPropagation(); runAction(s, "restart").catch(() => { }); } }, icon("restart")));
        }
        if (can(s, "Console") && !compact) {
            out.push(h("a", { class: "btn sm ghost", href: serverPath(s.machine, s.id, "console"), title: "Console", "aria-label": `Console for ${s.name}` }, icon("console")));
        }
        out.push(h("span", { class: "spacer" }));
        out.push(h("button", { class: "btn sm ghost icon-only", "aria-label": `More actions for ${s.name}`, "aria-haspopup": "menu", onclick: e => {
            e.stopPropagation();
            showMenu(e.currentTarget, [
                { label: "Open", icon: "chevronRight", onClick: () => navigate(serverPath(s.machine, s.id)) },
                { label: "Tags…", icon: "filter", hidden: !can(s, "EditConfig"), onClick: () => editTags(s) },
                "-",
                ...["start", "stop", "restart", "update", "validate", "backup", "kill"].map(a => ({
                    label: ACTIONS[a].label, icon: ACTIONS[a].icon, danger: ACTIONS[a].danger,
                    hidden: !can(s, ACTIONS[a].cap), disabled: !ACTIONS[a].when(s),
                    onClick: () => runAction(s, a).catch(() => { }),
                })),
            ]);
        } }, icon("more")));
        return out;
    }

    // ── Live updates ──
    function repaintAll() { paintHead(); paintKpis(); paintMachines(); paintAttention(); paintServers(); }
    const repaint = debounce(repaintAll, 60);
    scope.add(store.on("servers", repaint));
    scope.add(store.on("machines", repaint));
    scope.add(store.on("jobs", debounce(paintAttention, 200)));
    scope.add(store.on("prompts", paintAttention));
    // Metrics arrive every few seconds per server: repaint cards, but not more than ~once a second.
    let metricsPending = false;
    scope.add(store.on("metrics", () => {
        if (metricsPending) return;
        metricsPending = true;
        setTimeout(() => { metricsPending = false; if (scope.alive && !grid.contains(document.activeElement)) { paintKpis(); paintServers(); } }, 1000);
    }));
    scope.every(5000, async () => {
        const machines = await store.refreshMachines();
        for (const m of machines.values()) {
            if (m.online === false || !m.metrics) continue;
            const hist = historyFor(m.id);
            hist.cpu.push(m.metrics.cpuPercent);
            hist.ram.push(m.metrics.ramPercent);
            for (const k of ["cpu", "ram"]) if (hist[k].length > 60) hist[k].shift();
        }
        if (focus && !machines.has(focus)) { setFocus(null); return; }
        paintHead();
        paintKpis();
        paintMachines();
        paintAttention();
    });

    repaintAll();

    // Fill the card sparklines with the last hour straight away instead of waiting for new samples.
    const running = [...store.servers.values()].filter(s => isRunning(s) && store.isOnline(s.machine) && store.historyOf(key(s.machine, s.id)).length < 2);
    if (running.length) {
        await Promise.all(running.map(s => get(srv(s.machine, s.id, "/metrics")).then(m => store.seedHistory(key(s.machine, s.id), m)).catch(() => { })));
        if (scope.alive) paintServers();
    }
}

function kpi(iconName, label, value, sub, tone, percent) {
    const bar = h("span");
    bar.style.setProperty("width", Math.max(0, Math.min(100, percent || 0)) + "%");
    return h("div", { class: ["kpi", tone] },
        h("div", { class: "kpi-top" }, h("span", { class: "kpi-icon" }, icon(iconName)), h("span", { class: "upper", text: label })),
        h("div", { class: "kpi-value num" }, value),
        h("div", { class: "kpi-sub small muted", text: sub }),
        h("div", { class: "kpi-bar" }, bar));
}

function kpiSpark(iconName, label, value, sub, history, kind, max) {
    return h("div", { class: ["kpi", kind === "cpu" ? "accent" : "violet"] },
        h("div", { class: "kpi-top" }, h("span", { class: "kpi-icon" }, icon(iconName)), h("span", { class: "upper", text: label })),
        h("div", { class: "kpi-value num" }, h("b", { text: value })),
        h("div", { class: "kpi-sub small muted truncate", text: sub }),
        h("div", { class: "kpi-spark" }, sparkline(history, kind, max)));
}

function shortCpu(name) {
    return name.replace(/\(R\)|\(TM\)|CPU|Processor|\d+-Core/gi, "").replace(/@.*$/, "").replace(/\s+/g, " ").trim();
}

function welcomeCard() {
    const card = h("section", { class: "welcome card" },
        h("div", { class: "welcome-art" }, icon("sparkles")),
        h("div", { class: "grow" },
            h("h2", { text: "You're all set up" }),
            h("p", { class: "muted", text: "A few good next steps:" }),
            h("div", { class: "welcome-steps" },
                h("a", { class: "welcome-step", href: "/install" }, icon("plus"), h("span", {}, h("b", { text: "Install a server" }), h("small", { text: "Pick a game — ports are chosen for you." }))),
                h("a", { class: "welcome-step", href: "/settings" }, icon("rocket"), h("span", {}, h("b", { text: "Start with Windows" }), h("small", { text: "Keep servers running after a reboot." }))),
                h("a", { class: "welcome-step", href: "/users" }, icon("users"), h("span", {}, h("b", { text: "Invite your crew" }), h("small", { text: "Give friends access to just their servers." }))),
                h("a", { class: "welcome-step", href: "/account" }, icon("shield"), h("span", {}, h("b", { text: "Turn on 2FA" }), h("small", { text: "Protect the owner account." }))))),
        h("button", { class: "btn ghost sm icon-only", "aria-label": "Dismiss", onclick: () => { card.remove(); history.replaceState({}, "", "/"); } }, icon("x")));
    return card;
}
