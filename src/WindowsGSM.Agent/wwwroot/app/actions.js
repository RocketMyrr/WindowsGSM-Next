// Server actions shared by the overview and the server page: run the API call, track the job it starts,
// confirm the dangerous ones.

import { get, post, put, del, srv } from "./api.js";
import { h, icon } from "./dom.js";
import { store } from "./store.js";
import { confirm, toastError, toast, modal, busy, field, input, select, toggle } from "./ui.js";
import { can, isAdmin } from "./perms.js";

export const ACTIONS = {
    start: { label: "Start", icon: "play", cap: "Start", when: s => s.state === "Stopped" },
    stop: { label: "Stop", icon: "stop", cap: "Stop", when: s => s.state === "Running" },
    restart: { label: "Restart", icon: "restart", cap: "Restart", when: s => s.state === "Running" },
    kill: { label: "Force stop", icon: "kill", cap: "Kill", when: s => s.state !== "Stopped", danger: true,
        confirm: s => ({ title: `Force stop ${s.name}?`, message: "Ends the server process immediately, without a clean shutdown. Unsaved game progress may be lost. Use it when a server is stuck.", confirmLabel: "Force stop", danger: true }) },
    update: { label: "Update", icon: "update", cap: "Update", when: s => s.state === "Stopped" },
    validate: { label: "Verify files", icon: "validate", cap: "Update", when: s => s.state === "Stopped",
        confirm: s => ({ title: `Verify ${s.name}'s files?`, message: "Checks every game file against Steam and re-downloads anything missing or damaged. This can take a while.", confirmLabel: "Verify files" }) },
    backup: { label: "Back up now", icon: "backup", cap: "Backup", when: () => true, path: "/backups" },
};

/** Runs an action on one server. Returns the job (or null). */
export async function runAction(server, action, { quiet = false } = {}) {
    const def = ACTIONS[action];
    if (def.confirm && !quiet && !(await confirm(def.confirm(server)))) return null;
    try {
        const res = await post(srv(server.machine, server.id, def.path || "/" + action), {});
        if (res && res.job) store.trackJob(res.job, server.machine);
        return res && res.job;
    } catch (e) {
        if (!quiet) toastError(e, `Couldn't ${def.label.toLowerCase()} ${server.name}`);
        throw e;
    }
}

/** Runs an action on several servers; skips the ones where it doesn't apply or isn't allowed. */
export async function runBulk(servers, action) {
    const def = ACTIONS[action];
    const eligible = servers.filter(s => can(s, def.cap) && def.when(s));
    if (eligible.length === 0) { toast(`Nothing to ${def.label.toLowerCase()}`, { type: "info", text: "None of the selected servers can do that right now." }); return; }
    let ok = 0;
    const failed = [];
    await Promise.all(eligible.map(async s => {
        try { await runAction(s, action, { quiet: true }); ok++; }
        catch (e) { failed.push(`${s.name}: ${e.message}`); }
    }));
    const skipped = servers.length - eligible.length;
    if (failed.length) toast(`${def.label}: ${ok} started, ${failed.length} failed`, { type: "bad", text: failed.join(" · ") });
    else toast(`${def.label} started on ${ok} server${ok === 1 ? "" : "s"}`, { type: "good", text: skipped ? `${skipped} skipped (not applicable or not allowed).` : "" });
}

export async function deleteServer(server) {
    const ok = await confirm({
        title: `Delete ${server.name}?`,
        message: "Removes the server's files and settings from this machine. Its backups are kept, so it can be restored later. This can't be undone.",
        confirmLabel: "Delete server", danger: true, iconName: "trash",
    });
    if (!ok) return null;
    try {
        const res = await del(srv(server.machine, server.id));
        if (res && res.job) store.trackJob(res.job, server.machine);
        return res && res.job;
    } catch (e) { toastError(e, "Couldn't delete the server"); return null; }
}

/** A copy of a stopped server on the same machine — files and settings, next free ports, auto-start off. */
export function cloneServer(server) {
    const name = input({ value: `${server.name} (copy)`, maxlength: 100 });
    modal({
        title: `Copy ${server.name}`, iconName: "copy",
        subtitle: "A new server with the same files and settings — handy for a test server.",
        body: h("div", { class: "stack" },
            field("Name of the copy", name),
            h("div", { class: "callout info" }, icon("info"), h("span", { text: "It gets the next free ports and auto-start off. If the game keeps its port in its own config file too (Minecraft's server.properties, for example), change it there as well." }))),
        footer: close => {
            const go = h("button", { class: "btn primary", onclick: () => busy(go, async () => {
                const res = await post(srv(server.machine, server.id, "/clone"), { name: name.value.trim() });
                store.trackJob(res.job, server.machine);
                toast("Copying", { type: "info", text: "Big servers take a while — it runs in the background.", timeout: 4000 });
                close(true);
            }, "Couldn't copy the server") }, icon("copy"), "Copy");
            return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), go];
        },
    });
}

/** Moves (or copies) a stopped server to another machine on the hub. */
export function moveServer(server) {
    const targets = [...store.machines.values()].filter(m => m.id !== server.machine && store.isOnline(m.id));
    if (!targets.length) { toast("No other machine is online", { type: "warn" }); return; }
    const target = select(targets.map(m => ({ value: m.id, label: m.name })), targets[0].id);
    const name = input({ value: server.name, maxlength: 100 });
    const remove = toggle("Delete it from this machine afterwards", true, { hint: "Off: a copy is made and the original stays." });
    modal({
        title: `Move ${server.name}`, iconName: "machine",
        subtitle: `From ${store.machineName(server.machine)} to another machine.`,
        body: h("div", { class: "stack" },
            field("To", target), field("Name there", name), remove,
            h("div", { class: "callout info" }, icon("info"), h("span", { text: "The server is packed, sent through the hub and unpacked as a new server on the next free ports, with auto-start off. The game's plugin must be installed there too. Big servers take a while — and the original can't start until it's done." }))),
        footer: close => {
            const go = h("button", { class: "btn primary", onclick: () => busy(go, async () => {
                const res = await post("/move", { sourceMachine: server.machine, serverId: server.id, targetMachine: target.value, name: name.value.trim(), deleteOriginal: remove.input.checked });
                store.trackJob(res.job, store.localId);
                toast("Moving", { type: "info", text: "Follow it in Activity.", timeout: 4000 });
                close(true);
            }, "Couldn't start the move") }, icon("machine"), "Move");
            return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), go];
        },
    });
}

/**
 * Roll back a game update: pick an earlier build DepotDownloader installed (it keeps every build's file list), and it
 * downloads exactly that version again. Updates then stay on hold so auto-update doesn't undo it.
 */
export async function rollbackServer(server) {
    const list = h("div", { class: "build-list" }, h("div", { class: "row" }, h("span", { class: "spinner" }), h("span", { class: "muted", text: "Reading the build history…" })));
    let picked = null;
    let goButton = null;
    const holdBox = h("div");
    modal({
        title: `Roll back ${server.name}`, iconName: "restore",
        subtitle: "Go back to an earlier version of the game after a bad update.",
        body: h("div", { class: "stack" }, list, holdBox,
            h("div", { class: "callout info" }, icon("info"), h("span", { text: "Downloads that version's files from Steam again with DepotDownloader — your worlds, saves and configs aren't touched. Updates go on hold afterwards; update by hand (or resume them) when the game is fixed." }))),
        footer: close => {
            goButton = h("button", { class: "btn primary", disabled: true, onclick: () => busy(goButton, async () => {
                const res = await post(srv(server.machine, server.id, "/rollback"), { key: picked.key });
                if (res && res.job) store.trackJob(res.job, server.machine);
                toast("Rolling back", { type: "info", text: "Follow it in Activity.", timeout: 4000 });
                close(true);
            }, "Couldn't roll back") }, icon("restore"), "Roll back");
            return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), goButton];
        },
    });
    let data;
    try { data = await get(srv(server.machine, server.id, "/builds")); }
    catch (e) { list.replaceChildren(h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message || "Couldn't read the build history." }))); return; }

    if (data.held) {
        const resume = h("button", { class: "btn sm", onclick: () => busy(resume, async () => {
            await post(srv(server.machine, server.id, "/update-hold"), { held: false });
            holdBox.replaceChildren(h("div", { class: "callout good" }, icon("check"), h("span", { text: "Updates resumed — auto-update and update on start work again." })));
        }, "Couldn't resume updates") }, icon("play"), "Resume updates");
        holdBox.replaceChildren(h("div", { class: "callout warn" }, icon("clock"), h("span", { text: "Updates are on hold for this server (it was rolled back)." }), resume));
    }
    const builds = data.builds || [];
    if (builds.filter(b => !b.current).length === 0) {
        list.replaceChildren(h("div", { class: "callout info" }, icon("info"), h("span", { text: builds.length
            ? "There's no earlier build yet. Once this server has been updated with DepotDownloader, the build before the update shows up here."
            : "No build history yet. It starts with the next update made with DepotDownloader (servers set to \"Use SteamCMD instead\" in Settings have none)." })));
        return;
    }
    list.replaceChildren(...builds.map(b => {
        const when = new Date(b.installedAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
        const radio = h("input", { type: "radio", name: "rollback-build", disabled: b.current, onchange: () => { picked = b; goButton.disabled = false; } });
        return h("label", { class: ["build-row", b.current ? "current" : ""] }, radio,
            h("div", { class: "grow" },
                h("strong", { text: b.buildId ? `Build ${b.buildId}` : "Build (number unknown)" }),
                h("div", { class: "small muted", text: `${b.current ? "Installed now" : "Installed"} · ${when} · ${b.depots} depot${b.depots === 1 ? "" : "s"}` })),
            b.current ? h("span", { class: "tag green" }, "current") : null);
    }));
}

/** Saves this server's settings (and game config files) as a template for new servers of the same game. */
export function saveTemplate(server) {
    const name = input({ value: `${server.name} setup`, maxlength: 60 });
    const description = input({ maxlength: 300, placeholder: "e.g. PvE, 50 slots, our mod list" });
    const files = toggle("Include the game's config files", true, { hint: "server.cfg, server.properties, Game.ini… — ports in them are changed to each new server's own." });
    modal({
        title: `Save ${server.name} as a template`, iconName: "save",
        subtitle: "Start new servers of this game with the same setup.",
        body: h("div", { class: "stack" }, field("Template name", name), field("Description", description), files,
            h("div", { class: "callout info" }, icon("info"), h("span", { text: "Not copied from Settings: the name, address, ports and passwords (RCON, Steam branch, Discord webhook). Config files are copied whole — a password inside one (e.g. in server.cfg) comes along. Worlds and saves aren't part of a template — use Copy server for that." }))),
        footer: close => {
            const go = h("button", { class: "btn primary", onclick: () => busy(go, async () => {
                const t = await post(srv(server.machine, server.id, "/template"), { name: name.value.trim(), description: description.value.trim() || null, includeFiles: files.input.checked });
                toast("Template saved", { type: "good", text: `${t.settings} settings${t.files.length ? `, ${t.files.length} config file${t.files.length === 1 ? "" : "s"}` : ""}. Pick it when installing a server.` });
                close(true);
            }, "Couldn't save the template") }, icon("save"), "Save template");
            return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), go];
        },
    });
}

/** Applies a saved template to this (stopped) server; admins can also delete templates here. */
export async function applyTemplate(server) {
    const list = h("div", { class: "build-list" }, h("div", { class: "row" }, h("span", { class: "spinner" }), h("span", { class: "muted", text: "Loading templates…" })));
    let picked = null, go = null;
    modal({
        title: `Apply a template to ${server.name}`, iconName: "sliders",
        subtitle: "Replaces its settings and config files with the template's (its name, address and ports stay).",
        body: h("div", { class: "stack" }, list),
        footer: close => {
            go = h("button", { class: "btn primary", disabled: true, onclick: () => busy(go, async () => {
                const res = await post(srv(server.machine, server.id, "/apply-template"), { template: picked.id });
                toast("Template applied", { type: "good", text: res.done });
                store.emit("server:" + server.machine + "/" + server.id, server);
                close(true);
            }, "Couldn't apply the template") }, icon("check"), "Apply");
            return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), go];
        },
    });
    async function paint() {
        let all;
        try { all = await get(`/machines/${encodeURIComponent(server.machine)}/templates`); }
        catch (e) { list.replaceChildren(h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message }))); return; }
        const mine = all.filter(t => t.game === server.game);
        if (!mine.length) { list.replaceChildren(h("div", { class: "callout info" }, icon("info"), h("span", { text: "No templates for this game yet. Use \"Save as template…\" on a server you've set up the way you like." }))); return; }
        list.replaceChildren(...mine.map(t => {
            const radio = h("input", { type: "radio", name: "apply-template", onchange: () => { picked = t; go.disabled = false; } });
            const remove = isAdmin() ? h("button", { class: "btn ghost sm icon-only", title: "Delete template", "aria-label": `Delete ${t.name}`, onclick: async e => {
                e.preventDefault();
                if (!(await confirm({ title: `Delete the template "${t.name}"?`, message: "Servers made from it keep their settings.", confirmLabel: "Delete", danger: true, iconName: "trash" }))) return;
                try { await del(`/machines/${encodeURIComponent(server.machine)}/templates/${t.id}`); if (picked === t) { picked = null; go.disabled = true; } paint(); }
                catch (err) { toastError(err, "Couldn't delete the template"); }
            } }, icon("trash")) : null;
            return h("label", { class: "build-row" }, radio,
                h("div", { class: "grow" }, h("strong", { text: t.name }),
                    h("div", { class: "small muted", text: [t.description, `${t.settings} settings`, t.files.length ? `${t.files.length} config file${t.files.length === 1 ? "" : "s"}` : null, t.fromServer ? `from ${t.fromServer}` : null].filter(Boolean).join(" · ") })),
                remove);
        }));
    }
    paint();
}

/** Every tag in use (for suggestions and filters), most used first. */
export function allTags(servers = [...store.servers.values()]) {
    const counts = new Map();
    for (const s of servers) for (const t of s.tags || []) counts.set(t, (counts.get(t) || 0) + 1);
    return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([t]) => t);
}

/** The tag editor: type and press Enter (or pick a suggestion); × removes. */
export function editTags(server) {
    const tags = [...(server.tags || [])];
    const chips = h("div", { class: "tag-editor-chips" });
    const inputEl = h("input", { class: "input", placeholder: "Add a tag — e.g. EU, friends, modded", maxlength: 24, "aria-label": "New tag" });
    const suggestions = h("div", { class: "tag-suggestions" });
    const err = h("div", { class: "error", hidden: true });
    function paint() {
        chips.replaceChildren(...tags.map(t => h("span", { class: "tag chip" }, t,
            h("button", { type: "button", class: "chip-x", "aria-label": `Remove ${t}`, onclick: () => { tags.splice(tags.indexOf(t), 1); paint(); } }, icon("x")))));
        if (!tags.length) chips.append(h("span", { class: "small faint", text: "No tags yet." }));
        const others = allTags().filter(t => !tags.some(x => x.toLowerCase() === t.toLowerCase())).slice(0, 12);
        suggestions.replaceChildren(...(others.length ? [h("span", { class: "tiny faint", text: "Used elsewhere:" })] : []),
            ...others.map(t => h("button", { type: "button", class: "tag chip suggest", onclick: () => add(t) }, icon("plus"), t)));
    }
    function add(raw) {
        const t = raw.trim().replace(/[,<>]/g, "");
        err.hidden = true;
        if (!t) return;
        if (tags.some(x => x.toLowerCase() === t.toLowerCase())) { inputEl.value = ""; return; }
        if (tags.length >= 8) { err.textContent = "Up to 8 tags per server."; err.hidden = false; return; }
        tags.push(t.slice(0, 24));
        inputEl.value = "";
        paint();
    }
    inputEl.addEventListener("keydown", e => {
        if (e.key === "Enter" || e.key === ",") { e.preventDefault(); add(inputEl.value); }
        if (e.key === "Backspace" && !inputEl.value && tags.length) { tags.pop(); paint(); }
    });
    paint();
    modal({
        title: `Tags for ${server.name}`, subtitle: "Labels for finding and filtering your servers.", iconName: "filter",
        body: h("div", { class: "stack" }, chips, inputEl, err, suggestions),
        footer: close => {
            const save = h("button", { class: "btn primary", onclick: () => busy(save, async () => {
                if (inputEl.value.trim()) add(inputEl.value);
                const saved = await put(srv(server.machine, server.id, "/tags"), { tags });
                server.tags = saved;
                store.emit("server:" + server.machine + "/" + server.id, server);
                store.emit("servers");
                close(true);
            }, "Couldn't save the tags") }, icon("save"), "Save");
            return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), save];
        },
    });
    requestAnimationFrame(() => inputEl.focus());
}
