// Server overview tab: CPU/RAM/players chart (live for the last hour, from the machine's history for longer
// ranges), key facts, recent activity and a quick health check.

import { h, icon, clear, append, fmtMb, fmtBytes, fmtDateTime, gameLabel } from "../../dom.js";
import { moveFilesDialog, openFolderButton } from "../../places.js";
import { get, post, srv } from "../../api.js";
import { store } from "../../store.js";
import { metricsChart, isRunning, segmented, modal, loading, busy, toast } from "../../ui.js";
import { can, isAdmin } from "../../perms.js";
import { serverPath } from "../../router.js";

const RANGES = [{ value: "1h", label: "1h" }, { value: "24h", label: "24h" }, { value: "7d", label: "7d" }, { value: "30d", label: "30d" }];
let lastRange = "1h"; // remembered while you move between servers

export default async function overviewTab(host, { id, machine, key, server, scope }) {
    const chartBox = h("div", { class: "chart-box" });
    let range = lastRange;
    let longPoints = null; // for ranges beyond the live hour
    const rangeBar = segmented(RANGES, range, v => { range = lastRange = v; loadRange(); });
    rangeBar.classList.add("sm");
    const legend = h("div", { class: "chart-legend" });
    const facts = h("dl", { class: "facts" });
    const activity = h("div", { class: "activity-list" });
    const checks = h("div", { class: "checks-mini" });
    const firewall = h("div");
    const gamePerf = h("section", { class: "panel span-2", hidden: true });

    host.append(firewall, h("div", { class: "overview-grid" },
        h("section", { class: "panel span-2" },
            h("div", { class: "panel-head wrap" }, h("h3", { text: "Performance" }), legend, h("span", { class: "spacer" }), rangeBar),
            h("div", { class: "panel-body" }, chartBox)),
        gamePerf,
        h("section", { class: "panel" },
            h("div", { class: "panel-head" }, h("h3", { text: "Details" })),
            h("div", { class: "panel-body" }, facts)),
        h("section", { class: "panel" },
            h("div", { class: "panel-head" }, h("h3", { text: "Recent activity" }), h("span", { class: "spacer" }), h("a", { class: "btn ghost sm", href: serverPath(machine, id, "logs") }, "All logs", icon("chevronRight"))),
            h("div", { class: "panel-body flush" }, activity)),
        can(server(), "EditConfig") ? h("section", { class: "panel span-2" },
            h("div", { class: "panel-head wrap" }, h("h3", { text: "Health check" }), h("span", { class: "sub", text: "Is everything in place to run?" }), h("span", { class: "spacer" }),
                h("button", { class: "btn sm", onclick: () => reachDialog() }, icon("globe"), "Can players reach it?")),
            h("div", { class: "panel-body" }, checks)) : null));

    try { store.seedHistory(key, await get(srv(machine, id, "/metrics"))); } catch { /* live samples fill in */ }

    function paintChart() {
        const s = server();
        const live = range === "1h";
        const points = live ? store.historyOf(key) : longPoints || [];
        const withPlayers = points.some(p => p.players != null);
        clear(chartBox).append(metricsChart(points, { players: withPlayers, empty: live ? "Collecting samples…" : !longPoints ? "Loading…" : longPoints.length ? "Recording started — more shows up every few minutes" : "No history for this period yet" }));
        if (live) {
            const last = points[points.length - 1];
            append(clear(legend), [
                h("span", { class: "legend cpu" }, h("i"), `CPU ${last && isRunning(s) ? Math.round(last.cpu) + "%" : "—"}`),
                h("span", { class: "legend ram" }, h("i"), `RAM ${last && isRunning(s) ? fmtMb(last.ram) : "—"}`),
                withPlayers ? h("span", { class: "legend players" }, h("i"), `Players ${last && isRunning(s) && last.players != null ? last.players : "—"}`) : null]);
        } else {
            // Longer ranges: the averages and the busiest moment.
            const avg = k => { const v = points.map(p => p[k]).filter(n => n != null); return v.length ? v.reduce((a, b) => a + b, 0) / v.length : null; };
            const peak = Math.max(0, ...points.map(p => p.players || 0));
            const cpu = avg("cpu"), ram = avg("ram");
            append(clear(legend), [
                h("span", { class: "legend cpu" }, h("i"), `CPU avg ${cpu != null ? Math.round(cpu) + "%" : "—"}`),
                h("span", { class: "legend ram" }, h("i"), `RAM avg ${ram != null ? fmtMb(ram) : "—"}`),
                withPlayers ? h("span", { class: "legend players" }, h("i"), `Peak ${peak} players`) : null]);
        }
    }

    async function loadRange() {
        loadGamePerf();
        if (range === "1h") { paintChart(); return; }
        longPoints = null;
        paintChart();
        const asked = range;
        try {
            const list = await get(srv(machine, id, `/history?range=${asked}`));
            if (asked !== range || !scope.alive) return;
            longPoints = list.map(p => ({ at: Date.parse(p.at), cpu: p.cpu, ram: p.ram, players: p.players, maxPlayers: p.maxPlayers }));
        } catch { longPoints = []; }
        paintChart();
    }

    // In-game performance: server FPS (Rust, Source games) or TPS (Minecraft), asked over RCON every 5 minutes.
    async function loadGamePerf() {
        let p;
        try { p = await get(srv(machine, id, `/performance?range=${range === "30d" ? "30d" : range}`)); } catch { gamePerf.hidden = true; return; }
        if (!p.supported) { gamePerf.hidden = true; return; }
        gamePerf.hidden = false;
        const label = p.kind === "TPS" ? "Ticks per second (TPS)" : "Server FPS";
        const latest = p.latest && isRunning(server()) ? p.latest.value : null;
        const tone = v => v == null ? "" : p.target ? (v >= p.target * 0.9 ? "green-text" : v >= p.target * 0.6 ? "amber-text" : "rose-text") : "";
        const points = p.points.map(x => ({ at: Date.parse(x.at), v: x.avg, min: x.min }));
        const body = !p.rcon
            ? h("div", { class: "callout info" }, icon("info"), h("span", { text: `Set up RCON (Settings → Console & RCON) and WindowsGSM asks the game for its ${p.kind} every 5 minutes (\"${p.command}\") — lag shows here even when CPU looks fine.` }))
            : points.length < 2
                ? h("div", { class: "small muted pad", text: isRunning(server()) ? `Asking the game for its ${p.kind} every 5 minutes — the chart fills in shortly. If it stays empty, the game didn't answer "${p.command}" over RCON.` : "Start the server to measure it." })
                : perfChart(points, p.target);
        const avg = points.length ? points.reduce((a, b) => a + b.v, 0) / points.length : null;
        const low = points.length ? Math.min(...points.map(x => x.min)) : null;
        append(clear(gamePerf), [
            h("div", { class: "panel-head wrap" }, h("h3", { text: "Game performance" }), h("span", { class: "sub", text: label }), h("span", { class: "spacer" }),
                latest != null ? h("span", { class: "perf-now" }, h("b", { class: tone(latest), text: String(latest) }), h("span", { class: "muted", text: ` ${p.kind} now` })) : null,
                avg != null ? h("span", { class: "small muted", text: `avg ${avg.toFixed(1)} · lowest ${low}` }) : null),
            h("div", { class: "panel-body" }, body)]);
    }

    /** A plain line chart: average per point, a faint band down to the lowest, and the target (20 TPS) dashed. */
    function perfChart(points, target) {
        const NS = "http://www.w3.org/2000/svg";
        const el = (tag, attrs) => { const e = document.createElementNS(NS, tag); for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v); return e; };
        const W = 720, H = 120, padL = 34, padR = 10, padT = 8, padB = 16;
        const max = Math.max(target || 0, ...points.map(x => x.v)) * 1.1 || 1;
        const t0 = points[0].at, span = Math.max(1, points[points.length - 1].at - t0);
        const x = at => padL + ((at - t0) / span) * (W - padL - padR);
        const y = v => padT + (1 - v / max) * (H - padT - padB);
        const root = el("svg", { class: "chart perf-chart", viewBox: `0 0 ${W} ${H}`, role: "img", "aria-label": "Game performance over time" });
        for (const frac of [0, 0.5, 1]) {
            root.append(el("line", { class: "grid-line", x1: padL, x2: W - padR, y1: y(max * frac), y2: y(max * frac) }));
            const t = el("text", { class: "axis-label", x: padL - 6, y: y(max * frac) + 3, "text-anchor": "end" });
            t.textContent = Math.round(max * frac);
            root.append(t);
        }
        if (target) root.append(el("line", { class: "perf-target", x1: padL, x2: W - padR, y1: y(target), y2: y(target) }));
        const band = points.map(q => `${x(q.at)},${y(q.v)}`).concat(points.slice().reverse().map(q => `${x(q.at)},${y(q.min)}`)).join(" ");
        root.append(el("polygon", { class: "perf-band", points: band }));
        root.append(el("polyline", { class: "perf-line", points: points.map(q => `${x(q.at)},${y(q.v)}`).join(" ") }));
        return root;
    }

    // Share of time it was running, from this machine's history.
    let uptime = null;
    const pct = v => v == null ? null : (v >= 99.95 ? "100%" : `${v.toFixed(1)}%`);
    function uptimeText() {
        if (!uptime || uptime.day == null) return h("span", { class: "muted", text: uptime ? "Measuring…" : "—" });
        const parts = [["24 h", uptime.day], ["7 days", uptime.week], ["30 days", uptime.month]].filter(([, v]) => v != null);
        const tone = v => v >= 99 ? "green-text" : v >= 90 ? "" : "rose-text";
        // When WindowsGSM itself was off for a while, say so: the server may well have been fine.
        const agent = uptime.agentWeek ?? uptime.agentDay;
        return h("span", { class: "uptime", title: uptime.since ? `Counted since ${fmtDateTime(uptime.since)}.` : "" },
            ...parts.flatMap(([label, v], i) => [i ? h("span", { class: "faint", text: " · " }) : null, h("b", { class: tone(v), text: pct(v) }), h("span", { class: "muted", text: ` ${label}` })]).filter(Boolean),
            agent != null && agent < 99.5 ? h("div", { class: "tiny faint", text: `WindowsGSM itself was running ${pct(agent)} of the last ${uptime.agentWeek != null ? "7 days" : "24 h"} — time it was off counts as down.` }) : null);
    }
    // Where the game files are (another drive?), for Details.
    let filesAt = null;
    async function loadFilesAt() {
        try { filesAt = await get(srv(machine, id, "/files-location")); paintFacts(); } catch { /* older agent */ }
    }
    function filesText() {
        if (!filesAt) return "—";
        if (filesAt.problem) return h("span", { class: "rose-text" }, icon("warn"), " ", filesAt.problem);
        const open = filesAt.canOpen ? openFolderButton(machine, id) : null;
        const move = isAdmin() ? h("button", { class: "btn ghost sm", title: "Move the game files to another drive", onclick: () => moveFilesDialog(server()) }, icon("disk"), "Move…") : null;
        return h("span", { class: "files-at" },
            h("span", { class: "mono small", title: filesAt.path, text: filesAt.path }),
            h("span", { class: "faint small", text: `${filesAt.elsewhere ? "on another drive · " : ""}${filesAt.free != null ? `${filesAt.drive} has ${fmtBytes(filesAt.free)} free` : ""}` }),
            open, move);
    }

    async function loadUptime() {
        try { uptime = await get(srv(machine, id, "/uptime")); paintFacts(); } catch { /* history off or offline */ }
    }

    function paintFacts() {
        const s = server();
        if (!s) return;
        const row = (k, v) => [h("dt", { text: k }), h("dd", {}, v ?? "—")];
        clear(facts).append(
            ...row("Game", gameLabel(s.game)),
            ...row("Address", [s.ip, s.port].filter(Boolean).join(":") || "—"),
            ...row("Query port", s.queryPort || "—"),
            ...row("Max players", s.maxPlayers ?? "—"),
            ...row("Started", isRunning(s) && s.startedAt ? fmtDateTime(s.startedAt) : "Not running"),
            ...row("Uptime", uptimeText()),
            ...row("Game files", filesText()),
            ...row("Auto-start", onOff(s.autoStart, "with the agent")),
            ...row("Auto-restart", onOff(s.autoRestart, "after a crash")),
            ...row("Auto-update", onOff(s.autoUpdate, "checks every 30 min")));
    }

    async function paintActivity() {
        try {
            const log = await get(srv(machine, id, "/logs?count=8"));
            clear(activity);
            if (!log.lines.length) { activity.append(h("div", { class: "small faint pad", text: "Nothing logged yet today." })); return; }
            for (const line of log.lines.slice().reverse()) activity.append(logLine(line));
        } catch { /* keep what we have */ }
    }

    /** Port, address, firewall, router and Steam's server list, in plain words. */
    function reachDialog() {
        const body = h("div", { class: "stack" }, loading("Checking… (this asks the internet, so it takes a few seconds)"));
        const ICON = { pass: "checkCircle", warn: "warn", fail: "xCircle", info: "info" };
        async function run() {
            clear(body).append(loading("Checking… (this asks the internet, so it takes a few seconds)"));
            try {
                const r = await get(srv(machine, id, "/reachability"));
                append(clear(body), [...r.checks.map(c => h("div", { class: ["check", c.status === "pass" ? "pass" : c.status === "fail" ? "fail" : c.status === "warn" ? "warning" : "info"] },
                    icon(ICON[c.status] || "info"), h("div", { class: "grow" }, h("b", { text: c.name }), h("div", { class: "small muted", text: c.message })))),
                    r.publicIp ? h("p", { class: "tiny faint", text: `Checked from this machine. Public IP ${r.publicIp}${r.localIp ? ", local " + r.localIp : ""}.` }) : null]);
            } catch (e) { clear(body).append(h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message }))); }
        }
        // Router port forwarding (UPnP): admins can switch it on; everyone sees whether it worked.
        const forwarding = h("div", { class: "upnp-box" });
        async function paintForwarding(st) {
            try { st = st || await get(srv(machine, id, "/port-forwarding")); } catch { clear(forwarding); return; }
            const on = h("input", { type: "checkbox", checked: st.enabled, disabled: !isAdmin() });
            on.addEventListener("change", async () => {
                on.disabled = true;
                note.textContent = on.checked ? "Asking the router… (a few seconds)" : "Removing…";
                try {
                    const res = await post(srv(machine, id, "/port-forwarding"), { enabled: on.checked });
                    paintForwarding(res);
                    if (on.checked && !res.problem) toast("Ports forwarded on the router", { type: "good" });
                    run();
                } catch (e) { on.checked = !on.checked; on.disabled = false; note.textContent = e.message; }
            });
            const note = h("div", { class: "small muted" });
            if (!st.enabled) note.textContent = "Asks your router (UPnP) to forward this server's game and query ports, UDP and TCP, to this machine — again each time it starts. RCON is never forwarded.";
            else if (!st.at) note.textContent = "On — the ports are forwarded when the server next starts.";
            else if (st.problem) note.textContent = st.problem;
            else note.textContent = `Forwarded ${[...new Set(st.entries.map(e => e.port))].join(", ")} to ${st.localIp} on ${st.router}${st.externalIp ? ` (public IP ${st.externalIp})` : ""}.`;
            clear(forwarding).append(h("label", { class: "row upnp-toggle" }, on, h("b", { text: "Forward ports automatically" }),
                st.enabled && st.at ? h("span", { class: ["tag", st.problem ? "amber" : "green"] }, st.problem ? "didn't work" : "forwarded") : null), note,
                !isAdmin() ? h("div", { class: "tiny faint", text: "Admins and owners can change this." }) : null);
        }
        paintForwarding();

        modal({
            title: "Can players reach it?", subtitle: server()?.name, iconName: "globe", wide: true, body: h("div", { class: "stack" }, body, forwarding),
            footer: close => {
                const again = h("button", { class: "btn", onclick: () => busy(again, run) }, icon("refresh"), "Check again");
                return [again, h("button", { class: "btn primary", onclick: () => close(true) }, "Done")];
            },
        });
        run();
    }

    /** No Windows Firewall rule (or a blocking one): players elsewhere can't connect — say so, with the fix. */
    async function paintFirewall() {
        let r;
        try { r = await get(srv(machine, id, "/firewall")); } catch { clear(firewall); return; }
        const f = r.status;
        if (f.state !== "missing" && f.state !== "blocked") { clear(firewall); return; }
        const fix = isAdmin() ? h("button", { class: "btn primary sm", onclick: () => busy(fix, async () => {
            await post(srv(machine, id, "/firewall"));
            toast("Allowed through Windows Firewall", { type: "good", text: f.program ? f.program.split("\\").pop() : "" });
            paintFirewall();
            paintChecks();
        }, "Couldn't change Windows Firewall") }, icon("shield"), "Allow through firewall") : null;
        clear(firewall).append(h("div", { class: "callout warn firewall-callout" }, icon("shield"),
            h("div", { class: "grow" },
                h("b", { text: f.state === "blocked" ? "Windows Firewall is blocking this server" : "Players on other computers probably can't connect" }),
                // The heading already says what it means for players; the detail says why.
                h("div", { class: "small", text: f.state === "blocked" ? f.message : `Windows Firewall has no rule for ${(f.program || "").split("\\").pop()} yet.` }),
                isAdmin() && !r.elevated ? h("div", { class: "tiny muted", text: `Windows asks for administrator approval on ${store.machineName(machine)}'s screen — someone has to be there to click Yes.` }) : null),
            fix));
    }

    async function paintChecks() {
        if (!checks.isConnected) return;
        try {
            const list = await get(srv(machine, id, "/readiness"));
            clear(checks).append(...list.map(c => h("div", { class: ["check", c.status.toLowerCase()] },
                icon(c.status === "Pass" ? "checkCircle" : c.status === "Fail" ? "xCircle" : c.status === "Warning" ? "warn" : "info"),
                h("div", { class: "grow" }, h("b", { text: c.name }), h("div", { class: "tiny muted", text: c.message })))));
        } catch (e) { clear(checks).append(h("div", { class: "small faint", text: e.message })); }
    }

    scope.add(store.on("metrics:" + key, () => { if (range === "1h") paintChart(); }));
    scope.every(60000, () => { if (range !== "1h") loadRange(); }, { now: false });
    scope.add(store.on("server:" + key, () => { paintFacts(); paintChart(); }));
    scope.add(store.on("jobFinished", j => { if (j.serverId === id) { paintActivity(); paintChecks(); } }));
    scope.every(15000, paintActivity);
    loadRange();
    paintFacts();
    scope.every(5 * 60000, loadUptime);
    loadFilesAt();
    scope.add(store.on("jobFinished", j => { if (j.serverId === id && j.kind === "move-files") loadFilesAt(); }));
    scope.every(60000, loadGamePerf, { now: false });
    paintChecks();
    paintFirewall();
}

function onOff(on, detail) {
    return h("span", { class: on ? "on" : "off" }, on ? "On" : "Off", on && detail ? h("span", { class: "faint", text: " · " + detail }) : null);
}

/** "[09/29/2026-17:12:03][#1] Server: Started" → time + message, coloured by level. */
export function logLine(line) {
    const m = /^\[(\d\d)\/(\d\d)\/(\d{4})-(\d\d:\d\d:\d\d)\]\[[^\]]*\]\s?(.*)$/.exec(line);
    const time = m ? m[4] : "";
    const text = m ? m[5] : line;
    const level = /\[ERROR\]|fail|crash/i.test(text) ? "bad" : /\[NOTICE\]|warn/i.test(text) ? "warn" : /started|succe|complete|done/i.test(text) ? "good" : "";
    return h("div", { class: ["log-line", level] }, h("span", { class: "log-time mono", text: time }), h("span", { class: "log-text", text }));
}
