// Schedules across the fleet: what runs next on every server and machine, and adding the same schedule to
// many servers at once ("restart everything at 6am", "back up all Valheim servers every 4 hours").

import { h, icon, clear, append, fmtDateTime, timeUntil, gameTile } from "../dom.js";
import { get, put, srv } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { serverPath } from "../router.js";
import { toast, empty, loading } from "../ui.js";
import { can } from "../perms.js";
import { scheduleDialog, describe, SCHEDULE_ACTIONS } from "./server/schedules.js";

export default async function schedules(host, { scope }) {
    setCrumbs({ label: "Schedules" });
    const editable = () => store.sortedServers().filter(s => store.isOnline(s.machine) && can(s, "Schedules"));
    const addButton = h("button", { class: "btn primary", onclick: () => addToMany() }, icon("plus"), "Add to several servers");
    const upcoming = h("div", { class: "panel-body flush" });
    const byServer = h("div", { class: "panel-body flush" });
    append(host, [
        h("div", { class: "page-head" },
            h("div", {}, h("h1", { text: "Schedules" }), h("p", { text: "Everything that runs on its own, on every server. Times use each machine's clock." })),
            h("div", { class: "actions" }, editable().length ? addButton : null)),
        h("div", { class: "schedules-layout" },
            h("section", { class: "panel" }, h("div", { class: "panel-head" }, h("h3", { text: "Coming up" })), upcoming),
            h("section", { class: "panel" }, h("div", { class: "panel-head" }, h("h3", { text: "By server" })), byServer))]);

    let data = []; // [{ server, entries }]

    async function load() {
        clear(upcoming).append(loading());
        clear(byServer);
        const servers = store.sortedServers().filter(s => store.isOnline(s.machine));
        // A few at a time: on a hub, each is a request to that machine.
        const out = [];
        for (let i = 0; i < servers.length; i += 6) {
            out.push(...await Promise.all(servers.slice(i, i + 6).map(async s => {
                // The old app's restart setting is on every server, switched off — skip it unless it's on.
                try { return { server: s, entries: (await get(srv(s.machine, s.id, "/schedules"))).filter(e => e.source !== "RestartSetting" || e.enabled) }; }
                catch { return { server: s, entries: [], error: true }; }
            })));
        }
        if (!scope.alive) return;
        data = out;
        paint();
    }

    const actionOf = e => SCHEDULE_ACTIONS.find(a => a.value === e.action) || { label: e.action, icon: "clock" };
    const where = s => `${s.name}${store.multiMachine ? " · " + store.machineName(s.machine) : ""}`;

    function paint() {
        // Coming up: the next run of every enabled schedule, soonest first.
        const next = data.flatMap(d => d.entries.filter(e => e.enabled && e.next).map(e => ({ ...e, server: d.server })))
            .sort((a, b) => Date.parse(a.next) - Date.parse(b.next)).slice(0, 25);
        clear(upcoming);
        if (!next.length) {
            upcoming.append(empty("calendar", "Nothing scheduled", "Restart every morning, back up every few hours, or post a message before a restart.",
                editable().length ? h("button", { class: "btn primary", onclick: () => addToMany() }, icon("plus"), "Add a schedule") : null));
        }
        for (const e of next) {
            const a = actionOf(e);
            upcoming.append(h("a", { class: "list-item clickable sched-row", href: serverPath(e.server.machine, e.server.id, "schedules") },
                h("span", { class: "sched-when" }, h("b", { title: fmtDateTime(e.next), text: timeUntil(e.next) }), h("span", { class: "tiny faint", text: new Date(e.next).toLocaleString([], { weekday: "short", hour: "numeric", minute: "2-digit" }) })),
                h("span", { class: "sched-icon" }, icon(a.icon)),
                h("div", { class: "grow sched-main" }, h("b", { class: "truncate", text: a.label + (e.payload ? `: ${e.payload}` : "") }), h("span", { class: "small muted truncate", text: where(e.server) }))));
        }

        clear(byServer);
        const withAny = data.filter(d => d.entries.length);
        if (!withAny.length) { byServer.append(h("div", { class: "small faint pad", text: "No server has a schedule yet." })); }
        for (const d of withAny) {
            byServer.append(h("div", { class: "sched-server" },
                h("a", { class: "sched-server-head", href: serverPath(d.server.machine, d.server.id, "schedules") },
                    gameTile(d.server.game, d.server.iconUrl, "sm", d.server.artUrl), h("b", { class: "grow truncate", text: where(d.server) }), icon("chevronRight")),
                ...d.entries.map(e => h("a", { class: ["sched-line small", !e.enabled && "off"], href: serverPath(d.server.machine, d.server.id, "schedules"), title: "Edit or delete on the server's Schedules tab" },
                    icon(actionOf(e).icon), h("span", { class: "grow truncate", text: `${actionOf(e).label}${e.payload ? ": " + e.payload : ""} — ${describe(e.cron)}` }),
                    e.enabled ? null : h("span", { class: "tag", text: "Off" })))));
        }
        const failed = data.filter(d => d.error).length;
        if (failed) byServer.append(h("div", { class: "small faint pad", text: `Couldn't read ${failed} server${failed === 1 ? "'s" : "s'"} schedules.` }));
    }

    function addToMany() {
        const servers = editable();
        const chosen = new Set();
        const count = h("span", { class: "small muted", text: "None chosen" });
        const list = h("div", { class: "scope-list" });
        const paintCount = () => { count.textContent = chosen.size ? `${chosen.size} server${chosen.size === 1 ? "" : "s"}` : "None chosen"; };
        let group = null;
        for (const s of servers) {
            if (store.multiMachine && s.machine !== group) { group = s.machine; list.append(h("div", { class: "upper sched-group", text: store.machineName(group) })); }
            const k = `${s.machine}/${s.id}`;
            const box = h("input", { type: "checkbox" });
            box.addEventListener("change", () => { if (box.checked) chosen.add(k); else chosen.delete(k); paintCount(); });
            list.append(h("label", { class: "row small checkline" }, box, gameTile(s.game, s.iconUrl, "sm", s.artUrl), h("span", { text: `#${s.id} ${s.name}` })));
        }
        const all = h("button", { type: "button", class: "btn ghost sm", onclick: () => { list.querySelectorAll("input").forEach(b => { b.checked = true; }); servers.forEach(s => chosen.add(`${s.machine}/${s.id}`)); paintCount(); } }, "Select all");
        const before = h("div", { class: "stack tight" }, h("div", { class: "row between" }, h("span", { class: "upper", text: "On these servers" }), h("span", { class: "row" }, count, all)), list);

        scheduleDialog(null, {
            title: "Add a schedule to several servers", before,
            onSave: async entry => {
                if (!chosen.size) throw new Error("Choose at least one server.");
                const targets = servers.filter(s => chosen.has(`${s.machine}/${s.id}`));
                const failed = [];
                await Promise.all(targets.map(async s => {
                    try {
                        const current = (await get(srv(s.machine, s.id, "/schedules"))).filter(e => e.source === "Managed");
                        current.push(entry);
                        await put(srv(s.machine, s.id, "/schedules"), { entries: current.map(e => ({ cron: e.cron, action: e.action, payload: e.payload || null, enabled: e.enabled })) });
                    } catch (e) { failed.push(`${s.name}: ${e.message}`); }
                }));
                if (failed.length === targets.length) throw new Error(failed.join(" · "));
                toast(`Added to ${targets.length - failed.length} server${targets.length - failed.length === 1 ? "" : "s"}`, { type: failed.length ? "warn" : "good", text: failed.join(" · ") });
                load();
            },
        });
    }

    scope.every(60000, () => { if (data.length) paint(); }, { now: false }); // keeps "in 5 min" honest
    await load();
}
