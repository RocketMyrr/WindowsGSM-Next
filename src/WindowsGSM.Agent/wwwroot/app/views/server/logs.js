// WindowsGSM's own log for this server (starts, stops, crashes, updates…), live.

import { h, icon, clear, debounce } from "../../dom.js";
import { get, srv } from "../../api.js";
import * as live from "../../live.js";
import { store } from "../../store.js";
import { segmented, empty } from "../../ui.js";
import { logLine } from "./overview.js";

export default async function logsTab(host, { id, machine, key, scope }) {
    const list = h("div", { class: "log-list" });
    let level = "all";
    let search = "";
    const box = h("input", { class: "input", type: "search", placeholder: "Search the log…", "aria-label": "Search the log" });
    const levels = segmented([{ value: "all", label: "All" }, { value: "warn", label: "Warnings" }, { value: "bad", label: "Errors" }], level, v => { level = v; applyFilter(); });
    const count = h("select", { class: "input", "aria-label": "How many lines" }, ...[200, 500, 2000].map(n => h("option", { value: n }, `Last ${n}`)));
    count.addEventListener("change", load);
    box.addEventListener("input", debounce(() => { search = box.value.trim().toLowerCase(); applyFilter(); }, 120));

    host.append(h("section", { class: "panel" },
        h("div", { class: "panel-head" }, h("div", { class: "search grow" }, icon("search"), box), levels, count,
            h("button", { class: "btn ghost sm", onclick: load }, icon("refresh"), "Refresh")),
        h("div", { class: "panel-body flush" }, list)));

    function show(el) {
        const lvl = el.classList.contains("bad") ? "bad" : el.classList.contains("warn") ? "warn" : "";
        const levelOk = level === "all" || (level === "warn" && (lvl === "warn" || lvl === "bad")) || (level === "bad" && lvl === "bad");
        el.hidden = !levelOk || (!!search && !el.textContent.toLowerCase().includes(search));
    }
    function applyFilter() { for (const el of list.children) if (el.classList.contains("log-line")) show(el); }

    async function load() {
        const res = await get(srv(machine, id, `/logs?count=${count.value}`));
        clear(list);
        if (!res.lines.length) { list.append(empty("logs", "Nothing logged yet", "Starts, stops, crashes, updates and backups for this server show up here.")); return; }
        for (const line of res.lines.slice().reverse()) { const el = logLine(line); show(el); list.append(el); }
    }

    // New lines arrive live at the top.
    scope.add(live.watch("logs"));
    scope.add(store.on("log", e => {
        if (e.server !== id || e.machine !== machine) return;
        list.querySelector(".empty")?.remove();
        const d = new Date(e.at);
        const pad = n => String(n).padStart(2, "0");
        const el = logLine(`[${pad(d.getMonth() + 1)}/${pad(d.getDate())}/${d.getFullYear()}-${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}][#${id}] ${e.data.message}`);
        el.classList.add("fresh");
        show(el);
        list.prepend(el);
    }));
    await load();
}
