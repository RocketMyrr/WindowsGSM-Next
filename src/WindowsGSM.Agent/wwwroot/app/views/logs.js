// Logs: everything a machine writes down, in one place. Activity is the audit log (who did what); the other
// tabs are the log files in the data folder's logs\ — the app's daily log, crashes (the app's and servers'),
// the Discord bot, plugins that didn't compile, and the agent's detailed diagnostics.

import { h, icon, clear, append, fmtBytes, fmtDateTime, timeAgo, debounce } from "../dom.js";
import { get } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { empty, loading, tabs } from "../ui.js";
import { machinePicker } from "./machines.js";
import { auditPanel } from "./audit.js";

const TABS = [
    { id: "activity", label: "Activity", icon: "audit", about: "Who did what, when, and from where — sign-ins and every change." },
    { id: "app", label: "App log", icon: "logs", about: "Everything that happened, day by day: servers starting, stopping, crashing and updating, and the agent itself." },
    { id: "crash", label: "Crashes", icon: "warn", about: "Game servers that stopped unexpectedly (exit code and their last output), and WindowsGSM itself crashing." },
    { id: "discord", label: "Discord bot", icon: "message", about: "Every bot command and button press, who used it, and refusals." },
    { id: "plugins", label: "Plugins", icon: "puzzle", about: "Why a plugin didn't compile or load." },
    { id: "schedules", label: "Scheduled programs", icon: "calendar", about: "Output of programs run from Crontab schedules.", onlyIfAny: true },
    { id: "diagnostics", label: "Diagnostics", icon: "activity", about: "The agent's detailed diagnostics (kept 14 days) — useful when reporting a problem." },
];

export default async function logs(host, { query, scope }) {
    setCrumbs({ label: "Logs" });
    if (!store.me.canManageUsers) {
        host.append(h("div", { class: "callout warn" }, icon("lock"), h("span", { text: "Only admins and owners can see the logs." })));
        return;
    }
    let machine = store.machines.has(query.get("machine")) && store.isOnline(query.get("machine")) ? query.get("machine") : store.localId;
    let tab = TABS.some(t => t.id === query.get("tab")) ? query.get("tab") : "activity";
    let files = [];
    let view = null; // the open tab's teardown

    const base = () => `/machines/${encodeURIComponent(machine)}/logs`;
    const syncUrl = () => history.replaceState({}, "", `/logs?tab=${tab}` + (machine !== store.localId ? "&machine=" + encodeURIComponent(machine) : ""));
    const picker = machinePicker(machine, m => { machine = m; syncUrl(); reload(); }, { onlineOnly: true });
    const tabBar = h("div");
    const about = h("p", { class: "small muted logs-about" });
    const body = h("div", { class: "logs-body" });

    append(host, [
        h("div", { class: "page-head" },
            h("div", {}, h("h1", { text: "Logs" }), h("p", { text: "What happened on this machine — and what went wrong. Each machine keeps its own." })),
            h("div", { class: "actions" }, picker)),
        tabBar, about, body]);

    function paintTabs() {
        const counts = kind => files.filter(f => f.kind === kind).length;
        const items = TABS.filter(t => !t.onlyIfAny || counts(t.id) || tab === t.id).map(t => ({
            ...t, badge: t.id === "crash" && recentCrashes() ? recentCrashes() : null,
        }));
        clear(tabBar).append(tabs(items, tab, id => { tab = id; syncUrl(); paintTabs(); open(); }));
    }
    // Crashes in the last week get a count on the tab.
    const recentCrashes = () => files.filter(f => f.kind === "crash" && Date.now() - Date.parse(f.modified) < 7 * 864e5).length;

    async function reload() {
        try { files = await get(base()); } catch { files = []; }
        if (!scope.alive) return;
        paintTabs();
        open();
    }

    function open() {
        view?.();
        view = null;
        const t = TABS.find(x => x.id === tab);
        about.textContent = t.about;
        clear(body);
        if (tab === "activity") { auditPanel(body, machine); return; }
        view = filePanel(body, files.filter(f => f.kind === tab), t);
    }

    // ── A list of log files, and the one you picked ──

    function filePanel(host, list, t) {
        if (!list.length) {
            host.append(h("section", { class: "panel" }, empty(t.icon, t.id === "crash" ? "No crashes" : "Nothing here yet",
                t.id === "crash" ? "Nothing has crashed — or the crash logs were cleared." : "This log hasn't been written on this machine yet.")));
            return null;
        }
        // Crash reports lead with the exit code — open those at the top; running logs at their newest line.
        const tail = t.id !== "crash";
        let current = null, timer = null, text = "", follow = tail;
        const side = h("div", { class: "logs-files" });
        const title = h("b", { class: "truncate" });
        const meta = h("span", { class: "small faint truncate" });
        const search = h("input", { class: "input", type: "search", placeholder: "Find in this log", "aria-label": "Find in this log" });
        const problems = h("input", { type: "checkbox" });
        const download = h("a", { class: "btn ghost sm", title: "Download this log" }, icon("download"), "Download");
        const refresh = h("button", { class: "btn ghost sm icon-only", title: "Refresh", "aria-label": "Refresh", onclick: () => show(current) }, icon("refresh"));
        const pre = h("pre", { class: "logs-text", tabindex: "0" });
        const status = h("div", { class: "small faint logs-status" });
        pre.addEventListener("scroll", () => { follow = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 24; });

        host.append(h("div", { class: "logs-layout" },
            h("section", { class: "panel logs-side" }, h("div", { class: "panel-head" }, h("h3", { text: `${list.length} file${list.length === 1 ? "" : "s"}` })), side),
            h("section", { class: "panel logs-viewer" },
                h("div", { class: "panel-head wrap" },
                    h("div", { class: "grow logs-title" }, title, meta),
                    h("div", { class: "search" }, icon("search"), search),
                    h("label", { class: "row small checkline", title: "Only lines with errors, warnings and exceptions" }, problems, "Problems only"),
                    refresh, download),
                pre, status)));

        const serverName = f => f.server ? (store.server(machine, f.server)?.name || `#${f.server}`) : null;
        for (const f of list) {
            const b = h("button", { type: "button", class: "logs-file", onclick: () => { follow = tail; show(f); } },
                h("span", { class: "truncate", text: f.kind === "crash" && f.server ? `${serverName(f)}` : f.label }),
                h("span", { class: "tiny faint truncate", text: [f.kind === "crash" && f.server ? f.label : null, fmtBytes(f.size), timeAgo(f.modified)].filter(Boolean).join(" · ") }));
            b.dataset.path = f.path;
            side.append(b);
        }

        const PROBLEM = /\b(error|exception|fail(ed|ure)?|crash(ed)?|fatal|unhandled|refused|denied)\b|\[(ERROR|NOTICE|WARN)/i;
        function paintText() {
            const q = search.value.trim().toLowerCase();
            let lines = text.split(/\r?\n/);
            if (lines.length && lines[lines.length - 1] === "") lines.pop();
            const total = lines.length;
            if (problems.checked) lines = lines.filter(l => PROBLEM.test(l));
            if (q) lines = lines.filter(l => l.toLowerCase().includes(q));
            const frag = document.createDocumentFragment();
            for (const l of lines) {
                const cls = /\[ERROR\]|exception|fatal|unhandled|crash/i.test(l) ? "bad" : /\[NOTICE\]|\[WARN|warning|failed|skipped/i.test(l) ? "warn" : "";
                frag.append(h("span", { class: ["logs-line", cls], text: l + "\n" }));
            }
            clear(pre).append(frag);
            if (!lines.length) pre.append(h("span", { class: "faint", text: total ? "No lines match." : "(empty)" }));
            status.textContent = (problems.checked || q ? `${lines.length} of ${total} lines` : `${total} lines`) + (current?.truncated ? " · showing the last 1 MB — download for all of it" : "");
            if (follow) pre.scrollTop = pre.scrollHeight;
        }
        const repaint = debounce(paintText, 150);
        search.addEventListener("input", repaint);
        problems.addEventListener("change", paintText);

        async function show(f) {
            if (!f) return;
            side.querySelectorAll(".logs-file").forEach(b => b.setAttribute("aria-current", String(b.dataset.path === f.path)));
            title.textContent = f.kind === "crash" && f.server ? `${serverName(f)} — ${f.label}` : f.label;
            meta.textContent = `logs\\${f.path.replaceAll("/", "\\")} · ${fmtBytes(f.size)} · ${fmtDateTime(f.modified)}`;
            download.href = `/api/v2${base()}/download?path=${encodeURIComponent(f.path)}`;
            download.setAttribute("download", f.path.split("/").pop());
            const first = current?.path !== f.path;
            current = { path: f.path };
            if (first) clear(pre).append(loading());
            try {
                const res = await get(`${base()}/read?path=${encodeURIComponent(f.path)}`);
                if (current.path !== f.path) return;
                current.truncated = res.truncated;
                if (res.text === text && !first) return;
                text = res.text;
                paintText();
                if (first && !tail) pre.scrollTop = 0;
            } catch (e) {
                clear(pre).append(h("span", { class: "rose-text", text: e.message }));
            }
        }

        // The newest file is the one being written — keep it current while it's open.
        show(list[0]);
        // Asks only for the (small) file list, and re-reads the log when it has grown — up to 1 MB each time
        // would add up fast on a phone.
        let lastSize = list[0].size, checking = false;
        timer = setInterval(async () => {
            if (document.hidden || checking || current?.path !== list[0].path) return;
            checking = true;
            try {
                const now = (await get(base())).find(x => x.path === list[0].path);
                if (now && now.size !== lastSize) { lastSize = now.size; list[0] = now; await show(now); }
            } catch { /* agent busy — next time */ }
            finally { checking = false; }
        }, 5000);
        return () => clearInterval(timer);
    }

    scope.add(() => view?.());
    await reload();
}
