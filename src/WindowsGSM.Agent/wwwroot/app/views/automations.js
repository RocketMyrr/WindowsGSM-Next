// Automations: "if this, then that" for servers — stop an empty server, restart one stuck at full CPU, tell me
// when something crashes (with its last lines), say hello when someone joins. Each machine keeps its own rules.

import { h, icon, clear, append, timeAgo, fmtDateTime, gameTile } from "../dom.js";
import { get, put, del } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { empty, loading, busy, confirm, toast, modal, field, input, select, toggle } from "../ui.js";
import { machinePicker } from "./machines.js";

const TRIGGERS = [
    { value: "empty", label: "Has no players for…", icon: "players", needs: "minutes" },
    { value: "cpu", label: "Uses a lot of CPU for…", icon: "cpu", needs: "both", unit: "%", default: 90 },
    { value: "memory", label: "Uses a lot of memory for…", icon: "memory", needs: "both", unit: "MB", default: 8192 },
    { value: "crashed", label: "Crashes", icon: "warn" },
    { value: "joined", label: "Gets its first player (someone joins an empty server)", icon: "user" },
    { value: "disk", label: "This machine runs low on disk space for…", icon: "disk", needs: "both", unit: "GB free", default: 20, machine: true },
];
const ACTIONS = [
    { value: "notify", label: "Tell me (notifications, and your Discord/webhook channels)", icon: "bell" },
    { value: "stop", label: "Stop the server", icon: "stop" },
    { value: "restart", label: "Restart the server", icon: "restart" },
    { value: "backup", label: "Take a backup", icon: "backup" },
    { value: "command", label: "Send a console command", icon: "console" },
];
const IDEAS = [
    { name: "Stop empty servers", trigger: "empty", minutes: 120, action: "stop" },
    { name: "Restart when stuck at full CPU", trigger: "cpu", minutes: 10, threshold: 95, action: "restart" },
    { name: "Tell me about crashes", trigger: "crashed", action: "notify" },
    { name: "Someone's playing", trigger: "joined", action: "notify", cooldownMinutes: 120 },
    { name: "Warn me about low disk space", trigger: "disk", minutes: 5, threshold: 20, action: "notify", cooldownMinutes: 360 },
];

export default async function automations(host, { query, scope }) {
    setCrumbs({ label: "Automations" });
    if (!store.me.canManageUsers) {
        host.append(h("div", { class: "callout warn" }, icon("lock"), h("span", { text: "Only admins and owners can see automations." })));
        return;
    }
    let machine = store.machines.has(query.get("machine")) && store.isOnline(query.get("machine")) ? query.get("machine") : store.localId;
    const base = () => `/machines/${encodeURIComponent(machine)}/automations`;
    const body = h("div", { class: "panel-body flush" });
    const picker = machinePicker(machine, m => { machine = m; history.replaceState({}, "", "/automations?machine=" + encodeURIComponent(m)); load(); }, { onlineOnly: true });
    let rules = [];

    append(host, [
        h("div", { class: "page-head" },
            h("div", {}, h("h1", { text: "Automations" }), h("p", { text: "If this happens to a server, do that — on its own. Each machine keeps its own rules." })),
            h("div", { class: "actions" }, picker, h("button", { class: "btn primary", onclick: () => edit(null) }, icon("plus"), "New automation"))),
        h("section", { class: "panel" }, body)]);

    async function load() {
        clear(body).append(loading());
        try { rules = await get(base()); } catch (e) { clear(body).append(empty("warn", "Couldn't load automations", e.message)); return; }
        clear(body);
        if (!rules.length) {
            body.append(empty("zap", "No automations yet", "Start from an idea, or make your own.",
                ...IDEAS.map(i => h("button", { class: "btn sm", onclick: () => edit({ ...i, servers: [], enabled: true, cooldownMinutes: i.cooldownMinutes ?? 30 }) }, icon("sparkles"), i.name))));
            return;
        }
        for (const r of rules) {
            const on = h("input", { type: "checkbox", checked: r.enabled, "aria-label": `${r.name || "Automation"} on or off` });
            on.addEventListener("change", async () => {
                try { await put(`${base()}/${r.id}`, { ...r, enabled: on.checked }); r.enabled = on.checked; toast(on.checked ? "Automation on" : "Automation off", { type: "good", timeout: 2000 }); }
                catch (e) { on.checked = !on.checked; toast(e.message, { type: "bad" }); }
            });
            const row = h("div", { class: ["list-item automation-row", !r.enabled && "disabled"] },
                h("label", { class: "switch" }, on, h("span", { class: "track" })),
                h("span", { class: "sched-icon" }, icon((TRIGGERS.find(t => t.value === r.trigger) || {}).icon || "zap")),
                h("div", { class: "grow" },
                    h("b", { text: r.name || "Automation" }),
                    h("div", { class: "small muted", text: sentence(r) }),
                    r.lastFired ? h("div", { class: "tiny faint", title: fmtDateTime(r.lastFired), text: `Last ran ${timeAgo(r.lastFired)}: ${r.lastResult || ""}` }) : null),
                h("div", { class: "row sched-actions" },
                    h("button", { class: "btn sm", onclick: ev => { ev.stopPropagation(); edit(r); } }, icon("pencil"), "Edit"),
                    h("button", { class: "btn ghost sm danger-text", onclick: ev => { ev.stopPropagation(); remove(r); } }, icon("trash"), "Delete")));
            body.append(row);
        }
    }

    function serverNames(ids) {
        if (!ids.length) return "any server";
        return ids.map(id => store.server(machine, id)?.name || "#" + id).join(", ");
    }

    function sentence(r) {
        const t = TRIGGERS.find(x => x.value === r.trigger);
        const time = r.minutes % 60 === 0 && r.minutes >= 60 ? `${r.minutes / 60} hour${r.minutes === 60 ? "" : "s"}` : `${r.minutes} minute${r.minutes === 1 ? "" : "s"}`;
        const when = r.trigger === "empty" ? `has had no players for ${time}`
            : r.trigger === "cpu" ? `uses over ${r.threshold}% CPU for ${time}`
            : r.trigger === "memory" ? `uses over ${r.threshold} MB of memory for ${time}`
            : r.trigger === "crashed" ? "crashes" : r.trigger === "joined" ? "gets its first player" : t?.label;
        if (r.trigger === "disk") return `When a drive this machine uses has under ${r.threshold} GB free for ${time}, tell me.`;
        const then = r.action === "notify" ? "tell me" : r.action === "stop" ? "stop it" : r.action === "restart" ? "restart it" : r.action === "backup" ? "back it up" : `send “${r.command}”`;
        return `When ${serverNames(r.servers)} ${when}, ${then}.`;
    }

    function edit(existing) {
        const r = existing ? { ...existing } : { name: "", trigger: "empty", minutes: 120, threshold: 0, action: "notify", command: "", servers: [], cooldownMinutes: 30, enabled: true };
        const name = input({ value: r.name || "", maxlength: 80, placeholder: "e.g. Stop empty servers" });
        const trigger = select(TRIGGERS.map(t => ({ value: t.value, label: t.label })), r.trigger);
        const minutes = select([5, 10, 15, 30, 60, 120, 180, 240, 360, 720, 1440].map(m => ({ value: m, label: m >= 60 ? `${m / 60} hour${m === 60 ? "" : "s"}` : `${m} minutes` })), [5, 10, 15, 30, 60, 120, 180, 240, 360, 720, 1440].includes(r.minutes) ? r.minutes : 60);
        const threshold = input({ type: "number", min: 1, value: r.threshold || "", class: "input num" });
        const action = select(ACTIONS.map(a => ({ value: a.value, label: a.label })), r.action);
        const command = input({ class: "input mono", value: r.command || "", placeholder: "say Server restarting soon!" });
        const cooldown = select([0, 10, 30, 60, 120, 360, 1440].map(m => ({ value: m, label: m === 0 ? "No pause" : m >= 60 ? `${m / 60} hour${m === 60 ? "" : "s"}` : `${m} minutes` })), [0, 10, 30, 60, 120, 360, 1440].includes(r.cooldownMinutes) ? r.cooldownMinutes : 30);
        const servers = store.sortedServers(machine);
        const chosen = new Set(r.servers);
        const anyServer = toggle("Every server on this machine (including new ones)", chosen.size === 0, { help: "On: the rule watches every server, and servers you install later too. Off: pick the servers it applies to." });
        const list = h("div", { class: "scope-list" }, ...servers.map(s => {
            const box = h("input", { type: "checkbox", checked: chosen.has(s.id) });
            box.addEventListener("change", () => { if (box.checked) chosen.add(s.id); else chosen.delete(s.id); refresh(); });
            return h("label", { class: "row small checkline" }, box, gameTile(s.game, s.iconUrl, "sm", s.artUrl), h("span", { text: `#${s.id} ${s.name}` }));
        }));
        const emptyHint = h("div", { class: "tiny faint", text: "Uses the game's player count — servers whose game doesn't report players are never counted as empty (see the server's Players tab)." });
        const serversField = h("div", { class: "field" }, h("span", { class: "label", text: "Which servers" }), anyServer, list);
        const minutesField = field("For", minutes, { help: "How long the condition has to last before the rule acts — so a quiet moment or a short CPU spike doesn't set it off." }),
            thresholdField = field("Above", threshold, { help: "The level that counts as too much (CPU in %, memory in MB, free disk in GB). Look at the server's Overview chart for what's normal, and set this clearly above it." }),
            commandField = field("Command", command, { hint: "Sent to the console (or RCON).", help: "A line for the game, e.g. a save, a broadcast (say Server restarting soon) or a cleanup command. Uses RCON when it's set up." });
        const preview = h("div", { class: "callout info" }, icon("zap"), h("span"));
        const current = () => ({ ...r, name: name.value.trim(), trigger: trigger.value, minutes: Number(minutes.value), threshold: Number(threshold.value) || 0,
            action: action.value, command: command.value.trim() || null, servers: anyServer.input.checked ? [] : [...chosen], cooldownMinutes: Number(cooldown.value) });
        let lastTrigger = trigger.value;
        function refresh() {
            const t = TRIGGERS.find(x => x.value === trigger.value);
            minutesField.hidden = !t.needs;
            emptyHint.hidden = t.value !== "empty";
            thresholdField.hidden = t.needs !== "both";
            thresholdField.querySelector("label").textContent = t.machine ? `Under (${t.unit})` : `Above (${t.unit || ""})`;
            if (t.needs === "both" && (!threshold.value || trigger.value !== lastTrigger)) threshold.value = t.default;
            lastTrigger = trigger.value;
            // Low disk space is about the machine: no server list, and it can only tell you.
            serversField.hidden = !!t.machine;
            if (t.machine) action.value = "notify";
            action.disabled = !!t.machine;
            commandField.hidden = action.value !== "command";
            list.hidden = anyServer.input.checked;
            preview.lastChild.textContent = sentence(current());
        }
        [trigger, action, minutes, cooldown].forEach(el => el.addEventListener("change", refresh));
        [threshold, command, name].forEach(el => el.addEventListener("input", refresh));
        anyServer.input.addEventListener("change", refresh);

        modal({
            title: existing?.id ? "Edit automation" : "New automation", iconName: "zap", wide: true,
            body: h("div", { class: "stack" },
                field("Name", name),
                h("div", { class: "field" }, h("span", { class: "label", text: "When a server" }), trigger),
                h("div", { class: "form-grid two" }, minutesField, thresholdField),
                emptyHint,
                serversField,
                field("Then", action, { help: "Notify only tells you (bell, and your notification channels). Stop / restart / back up act on the server. Command sends a line to the game." }), commandField,
                field("Then wait at least", cooldown, { hint: "before doing it again for the same server.", help: "Stops a rule from firing over and over — e.g. one low-disk warning every 6 hours instead of every minute." }),
                preview),
            footer: close => {
                const save = h("button", { class: "btn primary", onclick: () => busy(save, async () => {
                    const id = existing?.id || Math.random().toString(16).slice(2, 12);
                    const rule = current();
                    if (!rule.servers.length && !anyServer.input.checked) throw new Error("Choose at least one server, or every server.");
                    await put(`${base()}/${id}`, rule);
                    toast("Automation saved", { type: "good", timeout: 2500 });
                    close(true);
                    load();
                }, "Couldn't save the automation") }, icon("save"), "Save");
                return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), save];
            },
        });
        refresh();
    }

    async function remove(r) {
        if (!(await confirm({ title: `Delete “${r.name || "this automation"}”?`, message: sentence(r), confirmLabel: "Delete", danger: true, iconName: "trash" }))) return;
        try { await del(`${base()}/${r.id}`); load(); } catch (e) { toast(e.message, { type: "bad" }); }
    }

    await load();
}
