// Game config: the game's own settings files (server.cfg, server.properties, Game.ini, serverconfig.xml…),
// found automatically and shown as a form. Saving changes only the values you edit — comments and layout
// in the file are kept.

import { h, icon, clear, append, fmtBytes, timeAgo, debounce } from "../../dom.js";
import { get, patch, srv } from "../../api.js";
import { toast, toastError, confirm, empty, loading, busy } from "../../ui.js";
import { setLeaveGuard, navigate, serverPath } from "../../router.js";
import { historyDialog } from "./history.js";

const SECRET = /pass(word)?|secret|token|webhook|apikey|api_key|rcon_?pw/i;

export default async function gameConfigTab(host, { id, machine, key, scope }) {
    const list = h("nav", { class: "cfg-files", "aria-label": "Config files" });
    const editor = h("section", { class: "cfg-editor panel" });
    host.append(h("div", { class: "cfg-layout" }, h("div", { class: "cfg-side" }, h("div", { class: "upper", text: "Config files" }), list), editor));

    let files = [];
    let current = null;          // parsed file shown
    const changes = new Map();   // entry id → new value
    const storeKey = "wgsm-gameconfig-" + id;

    async function loadFiles() {
        clear(list).append(loading("Looking for config files…"));
        try { files = await get(srv(machine, id, "/gameconfig")); }
        catch (e) { clear(list); clear(editor).append(empty("warn", "Couldn't look for config files", e.message)); return; }
        paintFiles();
        if (!files.length) {
            clear(editor).append(empty("fileCode", "No config files found yet",
                "Most games create their settings files the first time the server starts. Start it once, then come back — or browse everything in the Files tab.",
                h("a", { class: "btn", href: serverPath(machine, id, "files") }, icon("folder"), "Open Files")));
            return;
        }
        const remembered = sessionStorage.getItem(storeKey);
        await open(files.find(f => f.path === remembered) ? remembered : files[0].path);
    }

    function paintFiles() {
        clear(list);
        const known = files.filter(f => f.known), other = files.filter(f => !f.known);
        const item = f => h("button", { class: ["cfg-file", current && current.path === f.path && "active"], title: f.path, onclick: () => switchTo(f.path) },
            icon(f.known ? "sparkles" : "fileCode"),
            h("span", { class: "grow truncate" }, h("b", { class: "truncate", text: f.label || f.name }), h("small", { class: "truncate", text: f.path })),
            h("span", { class: "tag", text: f.format.toUpperCase() }));
        if (known.length) list.append(h("div", { class: "cfg-group", text: "This game's settings" }), ...known.map(item));
        if (other.length) list.append(h("div", { class: "cfg-group", text: known.length ? "Other config files" : "Found in the server folder" }), ...other.map(item));
    }

    async function switchTo(path) {
        if (current && current.path === path) return;
        if (changes.size && !(await confirm({ title: "Discard unsaved changes?", message: `You changed ${changes.size} setting${changes.size === 1 ? "" : "s"} in ${current.path}.`, confirmLabel: "Discard", danger: true }))) return;
        await open(path);
    }

    async function open(path) {
        changes.clear();
        guard();
        clear(editor).append(loading());
        try { current = await get(srv(machine, id, "/gameconfig/file?path=" + encodeURIComponent(path))); }
        catch (e) { current = null; clear(editor).append(empty("warn", "Couldn't read this file", e.message)); paintFiles(); return; }
        sessionStorage.setItem(storeKey, path);
        paintFiles();
        paintEditor();
    }

    function paintEditor() {
        const file = files.find(f => f.path === current.path);
        const search = h("input", { class: "input", type: "search", placeholder: `Search ${current.entries.length} settings…`, "aria-label": "Search settings" });
        const onlyChanged = h("input", { type: "checkbox" });
        const body = h("div", { class: "cfg-body" });
        const saveBar = h("div", { class: "cfg-save", hidden: true },
            icon("info"), h("span", { class: "grow cfg-save-text" }),
            h("button", { class: "btn sm", onclick: () => { changes.clear(); guard(); paintEditor(); } }, "Discard"),
            h("button", { class: "btn primary sm", onclick: e => save(e.currentTarget) }, icon("save"), "Save"));

        append(clear(editor), [
            h("div", { class: "panel-head wrap" },
                h("div", { class: "grow" },
                    h("h3", { text: file?.label || current.path.split("/").pop() }),
                    h("div", { class: "small faint mono truncate", text: `${current.path} · ${current.format} · edited ${timeAgo(current.modified)}` })),
                h("div", { class: "search" }, icon("search"), search),
                h("button", { class: "btn sm", title: "Earlier versions of this file — compare or put one back", onclick: () => historyDialog({ machine, id, path: current.path, title: "History", onRestored: () => open(current.path) }) }, icon("clock"), "History"),
                h("label", { class: "row small nowrap" }, onlyChanged, "Changed only"),
                h("button", { class: "btn ghost sm", title: "Edit the raw file", onclick: () => openRaw() }, icon("pencil"), "Edit as text")),
            current.note ? h("div", { class: "callout info cfg-note" }, icon("info"), h("span", { text: current.note })) : null,
            body,
            saveBar]);

        const paint = () => {
            const q = search.value.trim().toLowerCase();
            clear(body);
            const visible = current.entries.filter(e => (!q || [e.key, e.value, e.comment, e.section].some(v => v && v.toLowerCase().includes(q))) && (!onlyChanged.checked || changes.has(e.id)));
            if (!visible.length) { body.append(empty("search", q ? "No settings match" : "Nothing changed yet", q ? "Try another word." : "")); return; }
            let section;
            for (const e of visible) {
                if (e.section !== section) {
                    section = e.section;
                    if (section) body.append(h("div", { class: "cfg-section", text: section }));
                }
                body.append(row(e));
            }
        };
        const paintSave = () => {
            saveBar.hidden = changes.size === 0;
            saveBar.querySelector(".cfg-save-text").textContent = `${changes.size} unsaved change${changes.size === 1 ? "" : "s"} to ${current.path.split("/").pop()}`;
            guard();
        };

        function row(e) {
            const value = changes.has(e.id) ? changes.get(e.id) : e.value;
            const set = v => {
                if (v === e.value) changes.delete(e.id); else changes.set(e.id, v);
                el.classList.toggle("changed", changes.has(e.id));
                paintSave();
            };
            let control;
            if (e.readOnly) {
                control = h("code", { class: "cfg-readonly", text: value });
            } else if (e.type === "bool") {
                const box = h("input", { type: "checkbox", role: "switch", checked: value.toLowerCase() === "true", "aria-label": e.key });
                box.addEventListener("change", () => set(box.checked ? "true" : "false"));
                control = h("label", { class: "switch" }, box, h("span", { class: "track" }));
            } else {
                const secret = SECRET.test(e.key);
                const inp = h("input", { class: ["input", e.type === "number" && "num"], value, type: secret ? "password" : "text", inputmode: e.type === "number" ? "decimal" : null, spellcheck: "false", autocomplete: "off", "aria-label": e.key });
                inp.addEventListener("input", () => set(inp.value));
                control = secret
                    ? h("div", { class: "cfg-secret" }, inp, h("button", { class: "btn ghost sm icon-only", type: "button", "aria-label": "Show value", onclick: () => { inp.type = inp.type === "password" ? "text" : "password"; } }, icon("eye")))
                    : inp;
            }
            const el = h("div", { class: ["cfg-row", changes.has(e.id) && "changed"] },
                h("div", { class: "cfg-key" },
                    h("span", { class: "mono", text: e.key }),
                    e.comment ? h("small", { text: e.comment }) : null),
                h("div", { class: "cfg-control" }, control));
            return el;
        }

        search.addEventListener("input", debounce(paint, 120));
        onlyChanged.addEventListener("change", paint);
        paint();
        paintSave();
    }

    async function save(button) {
        const body = { path: current.path, expectedModified: current.modified, changes: [...changes].map(([id, value]) => ({ id, value })) };
        await busy(button, async () => {
            try {
                current = await patch(srv(machine, id, "/gameconfig/file"), body);
            } catch (e) {
                if (e.status === 409) {
                    const reload = await confirm({ title: "The file changed on disk", message: "The server (or someone else) saved this file after you opened it. Reload it to see the current values — your unsaved edits will be lost.", confirmLabel: "Reload", iconName: "refresh" });
                    if (reload) { changes.clear(); await open(current.path); }
                    return;
                }
                throw e;
            }
            const n = changes.size;
            changes.clear();
            toast(`Saved ${n} setting${n === 1 ? "" : "s"}`, { type: "good", text: "Most games read their config when they start — restart the server to apply." });
            paintEditor();
        }, "Couldn't save the settings");
    }

    async function openRaw() {
        if (changes.size && !(await confirm({ title: "Discard unsaved changes?", message: "Switching to the text editor drops your unsaved edits here.", confirmLabel: "Discard", danger: true }))) return;
        changes.clear();
        guard();
        // Open the Files tab in the file's folder.
        sessionStorage.setItem("wgsm-files-" + id, current.path.includes("/") ? current.path.slice(0, current.path.lastIndexOf("/")) : "");
        navigate(serverPath(machine, id, "files"));
    }

    function guard() {
        setLeaveGuard(changes.size ? () => confirm({ title: "Discard unsaved settings?", message: `You changed ${changes.size} setting${changes.size === 1 ? "" : "s"} that haven't been saved.`, confirmLabel: "Discard", danger: true }) : null);
    }
    scope.add(() => setLeaveGuard(null));

    await loadFiles();
}
