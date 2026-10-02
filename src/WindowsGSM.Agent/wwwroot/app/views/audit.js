// Audit log: every sign-in and every change, who made it and from where. Each machine keeps its own; a hub
// shows any machine's (changes made through the hub read "user (via hub)"). Shown as the Activity tab of Logs.

import { h, icon, clear, fmtDateTime, timeAgo, debounce } from "../dom.js";
import { get } from "../api.js";
import { store } from "../store.js";
import { empty, loading } from "../ui.js";

/** The old /audit address: the Logs page's Activity tab. */
export default async function audit(host, { query }) {
    const { navigate } = await import("../router.js");
    const m = query.get("machine");
    navigate("/logs?tab=activity" + (m ? "&machine=" + encodeURIComponent(m) : ""), { replace: true });
}

/** The audit table with its filters, for one machine. */
export async function auditPanel(host, machine) {
    const user = h("input", { class: "input", placeholder: "Anyone", "aria-label": "Filter by user" });
    const server = h("select", { class: "input", "aria-label": "Filter by server" });
    clear(server).append(h("option", { value: "" }, "All servers"),
        ...store.sortedServers(machine).map(s => h("option", { value: s.id }, `#${s.id} ${s.name}`)));
    const action = h("select", { class: "input", "aria-label": "Filter by action" }, ...[["", "Everything"], ["login", "Sign-ins"], ["start", "Starts"], ["stop", "Stops"], ["restart", "Restarts"], ["update", "Updates"], ["backup", "Backups"], ["restore", "Restores"], ["command", "Console commands"], ["file", "File changes"], ["settings", "Settings"], ["user", "User changes"], ["install", "Installs"]]
        .map(([v, l]) => h("option", { value: v }, l)));
    const limit = h("select", { class: "input", "aria-label": "How many" }, ...[200, 1000, 5000].map(n => h("option", { value: n }, `Last ${n}`)));
    const body = h("div");
    host.append(
        h("section", { class: "panel" },
            h("div", { class: "panel-head wrap" }, h("div", { class: "search" }, icon("user"), user), server, action, limit),
            h("div", { class: "panel-body flush table-wrap audit-body" }, body)));

    async function load() {
        clear(body).append(loading());
        const q = new URLSearchParams({ limit: limit.value });
        if (user.value.trim()) q.set("user", user.value.trim());
        if (server.value) q.set("server", server.value);
        if (action.value) q.set("action", action.value);
        let list;
        try { list = await get(`/machines/${encodeURIComponent(machine)}/audit?` + q); }
        catch (e) { clear(body).append(empty("warn", "Couldn't load the audit log", e.message)); return; }
        clear(body);
        if (!list.length) { body.append(empty("audit", "Nothing matches", "Try a wider filter.")); return; }
        body.append(h("table", { class: "table" },
            h("thead", {}, h("tr", {}, h("th", { text: "When" }), h("th", { text: "Who" }), h("th", { text: "What" }), h("th", { text: "Server" }), h("th", { text: "Details" }), h("th", { text: "From" }))),
            h("tbody", {}, ...list.map(e => h("tr", {},
                h("td", { class: "nowrap small", title: fmtDateTime(e.at), text: timeAgo(e.at) }),
                h("td", {}, h("b", { text: e.user || "—" })),
                h("td", {}, h("span", { class: ["tag", e.ok ? "" : "rose"] }, icon(e.ok ? "check" : "x"), e.action)),
                h("td", { class: "small", text: e.server ? (store.server(machine, e.server)?.name || "#" + e.server) : "—" }),
                h("td", { class: "small muted audit-detail", text: e.detail || "" }),
                h("td", { class: "small faint mono nowrap", text: e.ip || "" }))))));
    }
    const reload = debounce(load, 250);
    [user].forEach(el => el.addEventListener("input", reload));
    [server, action, limit].forEach(el => el.addEventListener("change", load));
    await load();
}
