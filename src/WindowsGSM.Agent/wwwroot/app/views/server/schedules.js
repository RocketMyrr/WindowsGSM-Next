// Scheduled tasks: restarts, backups, updates, start/stop and timed commands. Built with plain-English
// presets (daily at a time, every N hours, weekly) — raw cron is there for anyone who wants it.

import { h, icon, clear, fmtDateTime, timeUntil } from "../../dom.js";
import { get, put, post, srv } from "../../api.js";
import { modal, toast, toastError, empty, field, input, select, busy, confirm, segmented } from "../../ui.js";
import { can } from "../../perms.js";

const ACTIONS = [
    { value: "Restart", label: "Restart the server", icon: "restart" },
    { value: "Backup", label: "Take a backup", icon: "backup" },
    { value: "Update", label: "Update (stop, update, start)", icon: "update" },
    { value: "Start", label: "Start the server", icon: "play" },
    { value: "Stop", label: "Stop the server", icon: "stop" },
    { value: "Command", label: "Send a console command", icon: "console", needsPayload: true },
    { value: "Rcon", label: "Send an RCON command", icon: "send", needsPayload: true },
];
export { ACTIONS as SCHEDULE_ACTIONS };
const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

export default async function schedulesTab(host, { id, machine, key, server, scope }) {
    const body = h("div");
    const editable = can(server(), "Schedules");
    let entries = [];

    host.append(h("section", { class: "panel" },
        h("div", { class: "panel-head" }, h("h2", { text: "Schedules" }), h("span", { class: "sub", text: "Times use this machine's clock." }), h("span", { class: "spacer" }),
            editable ? h("button", { class: "btn primary sm", onclick: () => edit(null) }, icon("plus"), "Add schedule") : null),
        h("div", { class: "panel-body flush" }, body)));

    async function load() {
        // The old app's "restart on schedule" setting is filled in (switched off) on every server — only list it when on.
        entries = (await get(srv(machine, id, "/schedules"))).filter(e => e.source !== "RestartSetting" || e.enabled);
        clear(body);
        if (!entries.length) {
            body.append(empty("calendar", "Nothing scheduled", "Restart every morning, back up every few hours, or announce a restart in chat before it happens.",
                editable ? h("button", { class: "btn primary", onclick: () => edit(null) }, icon("plus"), "Add a schedule") : null));
            return;
        }
        for (const e of entries) {
            const action = ACTIONS.find(a => a.value === e.action) || { label: e.action, icon: "clock" };
            const managed = e.source === "Managed", setting = e.source === "RestartSetting";
            body.append(h("div", { class: ["list-item", !e.enabled && "disabled"] },
                h("span", { class: "sched-icon" }, icon(action.icon)),
                h("div", { class: "grow" },
                    h("b", { text: action.label + (e.payload ? `: ${e.payload}` : "") }),
                    h("div", { class: "small muted" }, describe(e.cron), h("span", { class: "faint mono", text: `   ${e.cron}` }))),
                h("div", { class: "sched-next small" }, e.enabled && e.next ? [h("span", { class: "faint", text: "Next " }), h("span", { title: fmtDateTime(e.next), text: timeUntil(e.next) })] : h("span", { class: "faint", text: "Off" })),
                e.source === "CrontabFile" ? h("span", { class: "tag", title: "From a Crontab .csv file on the server (legacy) — edit that file to change it. These run only while the old \"restart on schedule\" setting is on.", text: "Crontab file" }) : null,
                setting ? h("span", { class: "tag", title: "The old app's \"restart on schedule\" setting. Editing or deleting it here changes that setting.", text: "From the old app" }) : null,
                (managed || setting) && editable ? h("div", { class: "row sched-actions" },
                    h("button", { class: "btn sm", onclick: ev => { ev.stopPropagation(); edit(e); } }, icon("pencil"), "Edit"),
                    h("button", { class: "btn ghost sm danger-text", onclick: ev => { ev.stopPropagation(); remove(e); } }, icon("trash"), "Delete")) : null));
            // The whole row opens the editor too.
            if ((managed || setting) && editable) {
                const row = body.lastChild;
                row.classList.add("clickable");
                row.title = "Edit this schedule";
                row.addEventListener("click", () => edit(e));
            }
        }
    }

    const managedOnly = () => entries.filter(e => e.source === "Managed");
    const toRequest = e => ({ cron: e.cron, action: e.action, payload: e.payload || null, enabled: e.enabled });

    async function saveAll(list) {
        await put(srv(machine, id, "/schedules"), { entries: list.map(toRequest) });
        await load();
    }

    async function remove(e) {
        if (!(await confirm({ title: "Delete this schedule?", message: `${describe(e.cron)} — ${e.action}`, confirmLabel: "Delete", danger: true }))) return;
        try {
            if (e.source === "RestartSetting") { await put(srv(machine, id, "/schedules/restart-setting"), { cron: null, enabled: false }); await load(); }
            else await saveAll(managedOnly().filter(x => x !== e));
            toast("Schedule deleted", { type: "good", timeout: 2500 });
        } catch (ex) { toastError(ex); }
    }

    // ── Warn players before scheduled restarts, updates and stops ──
    const warnBody = h("div", { class: "panel-body stack" });
    host.append(h("section", { class: "panel warn-panel" },
        h("div", { class: "panel-head" }, h("h2", { text: "Warn players first" }), h("span", { class: "sub", text: "Chat messages before scheduled restarts, updates and stops." })),
        warnBody));

    const COMMANDS = [
        ["say {message}", "say — most games (Source, Minecraft, Rust, 7 Days…)"],
        ["broadcast {message}", "broadcast — ARK and others"],
        ["Broadcast {message}", "Broadcast — Palworld (no spaces shown)"],
        ["servermsg \"{message}\"", "servermsg — Project Zomboid"],
    ];

    async function loadWarnings() {
        let w;
        try { w = await get(srv(machine, id, "/restart-warnings")); }
        catch (e) { clear(warnBody).append(h("div", { class: "small faint", text: e.message })); return; }
        const on = h("input", { type: "checkbox", checked: w.enabled, disabled: !editable });
        const leads = new Set(w.leads);
        const chips = h("div", { class: "row wrap" });
        const paintChips = () => clear(chips).append(...w.allowedLeads.map(l => {
            const label = l >= 60 ? `${l / 60} min` : `${l} s`;
            return h("button", { type: "button", class: ["tag chip", leads.has(l) && "accent"], "aria-pressed": String(leads.has(l)), disabled: !editable,
                onclick: () => { if (leads.has(l)) leads.delete(l); else leads.add(l); paintChips(); paintPreview(); } }, label);
        }));
        const known = COMMANDS.some(([c]) => c === w.command);
        const cmdSel = select([...COMMANDS.map(([value, label]) => ({ value, label })), { value: "", label: "Something else…" }], known ? w.command : "");
        const cmd = input({ class: "input mono", value: w.command, placeholder: "say {message}" });
        const cmdField = field("Command", cmd, { hint: "{message} is replaced by the text.", help: "The game's own broadcast command — e.g. say {message} for Source games, broadcast {message} for ARK, global.say {message} for Rust. Check the game's console help if unsure." });
        cmdField.hidden = known;
        cmdSel.addEventListener("change", () => { if (cmdSel.value) cmd.value = cmdSel.value; cmdField.hidden = !!cmdSel.value; paintPreview(); });
        const msg = input({ value: w.message, maxlength: 200 });
        const preview = h("div", { class: "callout info" }, icon("console"), h("span", { class: "mono small" }));
        const paintPreview = () => {
            const first = [...leads].sort((a, b) => b - a)[0] ?? 300;
            const time = first >= 60 ? `${first / 60} minute${first === 60 ? "" : "s"}` : `${first} seconds`;
            preview.lastChild.textContent = (cmd.value || "say {message}").replace("{message}", msg.value.replace("{action}", "restarting").replace("{time}", time));
        };
        [cmd, msg].forEach(el => el.addEventListener("input", paintPreview));
        paintChips();
        paintPreview();
        const saveBtn = h("button", { class: "btn primary sm", disabled: !editable, onclick: () => busy(saveBtn, async () => {
            await put(srv(machine, id, "/restart-warnings"), { enabled: on.checked, leads: [...leads], command: cmd.value.trim(), message: msg.value.trim() });
            toast("Warnings saved", { type: "good", timeout: 2500 });
        }, "Couldn't save the warnings") }, icon("save"), "Save");
        const testBtn = h("button", { class: "btn sm", disabled: !editable, onclick: () => busy(testBtn, async () => {
            await put(srv(machine, id, "/restart-warnings"), { enabled: on.checked, leads: [...leads], command: cmd.value.trim(), message: msg.value.trim() });
            await post(srv(machine, id, "/restart-warnings/test"));
            toast("Test sent", { type: "good", text: "Check the game's chat.", timeout: 3000 });
        }, "Couldn't send the test") }, icon("send"), "Send a test");
        clear(warnBody).append(
            h("label", { class: "row small checkline" }, on, h("b", { text: "Send warnings" }), h("span", { class: "muted", text: "(only while the server is running)" })),
            h("div", { class: "field" }, h("span", { class: "label", text: "When" }), chips),
            h("div", { class: "form-grid" }, field("How to send", cmdSel), field("Message", msg, { hint: "{action} → restarting / updating / shutting down · {time} → 5 minutes" })),
            cmdField,
            preview,
            h("div", { class: "row" }, h("span", { class: "spacer" }), testBtn, saveBtn));
    }

    function edit(existing) {
        scheduleDialog(existing, {
            onSave: async entry => {
                if (existing?.source === "RestartSetting") {
                    // Still a plain restart: keep it as the old setting. Anything else becomes a normal schedule.
                    if (entry.action === "Restart") {
                        await put(srv(machine, id, "/schedules/restart-setting"), { cron: entry.cron, enabled: entry.enabled });
                        await load();
                        toast("Schedule saved", { type: "good", timeout: 2500 });
                        return;
                    }
                    await saveAll([...managedOnly(), entry]);
                    await put(srv(machine, id, "/schedules/restart-setting"), { cron: null, enabled: false });
                    await load();
                    toast("Schedule saved", { type: "good", timeout: 2500 });
                    return;
                }
                const list = managedOnly().filter(x => x !== existing);
                list.push(entry);
                await saveAll(list);
                toast("Schedule saved", { type: "good", timeout: 2500 });
            },
        });
    }

    await Promise.all([load(), loadWarnings()]);
}

/**
 * The schedule editor: what to do, and when (plain-English presets or raw cron). before: extra content at the
 * top (the fleet page puts a server picker there). onSave(entry) may throw to keep the dialog open.
 */
export function scheduleDialog(existing, { title, before = null, onSave }) {
    const parsed = existing ? parseCron(existing.cron) : { mode: "daily", time: "06:00", hours: 6, day: 1 };
    let mode = parsed.mode;
    const actionSel = select(ACTIONS.map(a => ({ value: a.value, label: a.label })), existing ? existing.action : "Restart");
    const payload = input({ class: "input mono", value: existing?.payload || "", placeholder: "say Restarting in 5 minutes!" });
    const payloadField = field("Command", payload);
    const time = input({ type: "time", value: parsed.time || "06:00" });
    const hours = select([1, 2, 3, 4, 6, 8, 12].map(n => ({ value: n, label: `Every ${n} hour${n === 1 ? "" : "s"}` })), parsed.hours || 6);
    const day = select(DAYS.map((d, i) => ({ value: i, label: d })), parsed.day ?? 1);
    const cron = input({ class: "input mono", value: existing?.cron || "0 6 * * *", placeholder: "m h dom mon dow" });
    const enabled = h("input", { type: "checkbox", checked: existing ? existing.enabled : true });
    const preview = h("div", { class: "callout info" }, icon("clock"), h("span"));
    const whenHost = h("div", { class: "stack" });

    const modes = segmented([{ value: "daily", label: "Daily" }, { value: "hourly", label: "Every few hours" }, { value: "weekly", label: "Weekly" }, { value: "custom", label: "Custom" }], mode, v => { mode = v; paintWhen(); refresh(); });

    function paintWhen() {
        clear(whenHost).append(
            mode === "daily" ? field("At", time) :
            mode === "hourly" ? field("How often", hours, { hint: "On the hour." }) :
            mode === "weekly" ? h("div", { class: "form-grid two" }, field("Day", day), field("At", time)) :
            field("Cron expression", cron, { help: ["Five fields: minute, hour, day of the month, month, day of the week. * means every; 1-5 a range; */15 every 15.", "Examples: 0 4 * * * (4:00 every day) · 0 */6 * * * (every 6 hours) · 30 4 * * 1-5 (4:30 on weekdays) · 0 3 * * 0 (3:00 on Sundays). Times use this machine's clock."], hint: "minute hour day-of-month month day-of-week — e.g. 30 4 * * 1-5 is 4:30am on weekdays." }));
    }
    function currentCron() {
        const [hh, mm] = (time.value || "06:00").split(":").map(Number);
        if (mode === "daily") return `${mm} ${hh} * * *`;
        if (mode === "hourly") return `0 */${hours.value} * * *`;
        if (mode === "weekly") return `${mm} ${hh} * * ${day.value}`;
        return cron.value.trim();
    }
    function refresh() {
        payloadField.hidden = !ACTIONS.find(a => a.value === actionSel.value)?.needsPayload;
        preview.lastChild.textContent = describe(currentCron()) + (actionSel.value ? ` — ${ACTIONS.find(a => a.value === actionSel.value).label.toLowerCase()}.` : "");
    }
    [actionSel, time, hours, day, cron].forEach(el => el.addEventListener("input", refresh));
    [actionSel, hours, day].forEach(el => el.addEventListener("change", refresh));
    paintWhen();
    refresh();

    modal({
        title: title || (existing ? "Edit schedule" : "New schedule"), iconName: "calendar", wide: true,
        body: h("div", { class: "stack" },
            before,
            field("Do this", actionSel, { help: "Restart warns players first if you set up warnings below. Update stops, updates and starts again (only if there's something new). Command and RCON send a line to the game — e.g. a save or a broadcast." }), payloadField,
            h("div", { class: "field" }, h("span", { class: "label", text: "When" }), modes), whenHost,
            preview,
            h("label", { class: "row small" }, enabled, "Enabled")),
        footer: close => {
            const saveBtn = h("button", { class: "btn primary", onclick: () => busy(saveBtn, async () => {
                const entry = { cron: currentCron(), action: actionSel.value, payload: payload.value.trim(), enabled: enabled.checked, source: "Managed" };
                await onSave(entry);
                close(true);
            }, "Couldn't save the schedule") }, icon("save"), "Save");
            return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), saveBtn];
        },
    });
}

function parseCron(expr) {
    const p = (expr || "").trim().split(/\s+/);
    if (p.length === 5) {
        const [m, hr, dom, mon, dow] = p;
        const t = /^\d+$/.test(m) && /^\d+$/.test(hr) ? `${hr.padStart(2, "0")}:${m.padStart(2, "0")}` : null;
        if (t && dom === "*" && mon === "*" && dow === "*") return { mode: "daily", time: t };
        if (t && dom === "*" && mon === "*" && /^[0-6]$/.test(dow)) return { mode: "weekly", time: t, day: Number(dow) };
        const every = /^\*\/(\d+)$/.exec(hr);
        if (m === "0" && every && dom === "*" && mon === "*" && dow === "*") return { mode: "hourly", hours: Number(every[1]) };
    }
    return { mode: "custom" };
}

/** "0 6 * * *" → "Every day at 6:00 AM" (falls back to the expression). */
export function describe(expr) {
    const c = parseCron(expr);
    const fmt = t => { const [hh, mm] = t.split(":").map(Number); return new Date(2000, 0, 1, hh, mm).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }); };
    if (c.mode === "daily") return `Every day at ${fmt(c.time)}`;
    if (c.mode === "weekly") return `Every ${DAYS[c.day]} at ${fmt(c.time)}`;
    if (c.mode === "hourly") return c.hours === 1 ? "Every hour, on the hour" : `Every ${c.hours} hours, on the hour`;
    return `Custom: ${expr}`;
}
