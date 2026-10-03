// File manager for the server's folder: browse, edit (with validation), upload (drag & drop), rename,
// delete, download. Everything is confined to the server's own files by the agent.

import { h, icon, clear, fmtBytes, timeAgo } from "../../dom.js";
import { get, post, put, srv, upload } from "../../api.js";
import { toast, toastError, confirm, promptText, showMenu, empty, loading, progressBar, setProgress } from "../../ui.js";
import { setLeaveGuard } from "../../router.js";
import { historyDialog } from "./history.js";
import { openFolderButton } from "../../places.js";

const TEXT_EXT = /\.(cfg|ini|txt|json|xml|yml|yaml|properties|conf|config|lua|cs|js|log|md|bat|cmd|ps1|sh|toml|csv|vdf|acf)$/i;

export default async function filesTab(host, { id, machine, key, scope }) {
    let path = sessionStorage.getItem("wgsm-files-" + id) || "";
    const crumbs = h("nav", { class: "file-crumbs", "aria-label": "Folder" });
    const listHost = h("div", { class: "file-list" });
    const uploads = h("div", { class: "uploads" });
    const fileInput = h("input", { type: "file", multiple: true, hidden: true });
    // On the server's own PC: open this folder in Windows Explorer (the agent opens it — a web page can't).
    const explorerSlot = h("span", { class: "explorer-slot" });
    get(srv(machine, id, "/files-location")).then(w => { if (w.canOpen) explorerSlot.append(openFolderButton(machine, id, "Open in Explorer")); }).catch(() => { /* older agent */ });
    const drop = h("div", { class: "drop-overlay", hidden: true }, icon("upload"), h("b", { text: "Drop files to upload" }), h("span", { class: "small muted", text: "They'll go into the folder you're viewing." }));
    const panel = h("section", { class: "panel files-panel" },
        h("div", { class: "panel-head" }, crumbs, h("span", { class: "spacer" }),
            explorerSlot,
            h("button", { class: "btn sm", onclick: newFolder }, icon("folderPlus"), "New folder"),
            h("button", { class: "btn sm primary", onclick: () => fileInput.click() }, icon("upload"), "Upload"),
            h("button", { class: "btn ghost sm icon-only", "aria-label": "Refresh", onclick: () => load() }, icon("refresh"))),
        uploads,
        h("div", { class: "panel-body flush file-body" }, listHost, drop),
        fileInput);
    host.append(panel);

    fileInput.addEventListener("change", () => { uploadFiles([...fileInput.files]); fileInput.value = ""; });
    let dragDepth = 0;
    panel.addEventListener("dragenter", e => { if (e.dataTransfer.types.includes("Files")) { e.preventDefault(); dragDepth++; drop.hidden = false; } });
    panel.addEventListener("dragover", e => { if (e.dataTransfer.types.includes("Files")) e.preventDefault(); });
    panel.addEventListener("dragleave", () => { if (--dragDepth <= 0) { dragDepth = 0; drop.hidden = true; } });
    panel.addEventListener("drop", e => { e.preventDefault(); dragDepth = 0; drop.hidden = true; uploadFiles([...e.dataTransfer.files]); });

    async function load(to = path) {
        clear(listHost).append(loading());
        try {
            const folder = await get(srv(machine, id, "/files?path=" + encodeURIComponent(to)));
            path = folder.path;
            sessionStorage.setItem("wgsm-files-" + id, path);
            paintCrumbs();
            paintList(folder.entries);
        } catch (e) {
            if (to && e.status !== 403) { path = ""; return load(""); }
            clear(listHost).append(empty("folder", "Can't open this folder", e.message));
        }
    }

    function paintCrumbs() {
        const parts = path ? path.split("/") : [];
        clear(crumbs).append(h("button", { class: "crumb", onclick: () => load("") }, icon("folder"), "serverfiles"));
        parts.forEach((p, i) => {
            crumbs.append(icon("chevronRight"));
            const target = parts.slice(0, i + 1).join("/");
            crumbs.append(i === parts.length - 1 ? h("b", { text: p }) : h("button", { class: "crumb", onclick: () => load(target), text: p }));
        });
    }

    function paintList(entries) {
        clear(listHost);
        const rows = [];
        if (path) rows.push(h("button", { class: "file-row up", onclick: () => load(path.split("/").slice(0, -1).join("/")) }, icon("chevronLeft"), h("span", { class: "grow", text: "Back up a folder" })));
        if (!entries.length) rows.push(empty("folder", "This folder is empty", "Drag files here or use Upload."));
        for (const e of entries) {
            const rel = path ? `${path}/${e.name}` : e.name;
            const row = h("div", { class: ["file-row", e.isDirectory && "dir"], tabindex: "0", role: "button", "aria-label": e.isDirectory ? `Open folder ${e.name}` : `Open ${e.name}` },
                icon(e.isDirectory ? "folder" : TEXT_EXT.test(e.name) ? "fileCode" : "file"),
                h("span", { class: "grow truncate", text: e.name }),
                h("span", { class: "file-size num", text: e.isDirectory ? "" : fmtBytes(e.size) }),
                h("span", { class: "file-date", text: timeAgo(e.modified) }),
                h("button", { class: "btn ghost sm icon-only", "aria-label": `Actions for ${e.name}`, onclick: ev => { ev.stopPropagation(); rowMenu(ev.currentTarget, e, rel); } }, icon("more")));
            const open = () => e.isDirectory ? load(rel) : openFile(rel, e);
            row.addEventListener("click", open);
            row.addEventListener("keydown", ev => { if (ev.key === "Enter") open(); });
            rows.push(row);
        }
        listHost.append(...rows);
    }

    function rowMenu(anchor, e, rel) {
        showMenu(anchor, [
            { label: e.isDirectory ? "Open" : "Edit", icon: e.isDirectory ? "folder" : "pencil", onClick: () => e.isDirectory ? load(rel) : openFile(rel, e) },
            { label: "Download", icon: "download", hidden: e.isDirectory, onClick: () => downloadFile(rel) },
            { label: "Rename…", icon: "pencil", onClick: () => rename(rel, e.name) },
            "-",
            { label: "Delete…", icon: "trash", danger: true, onClick: () => remove(rel, e) },
        ]);
    }

    function downloadFile(rel) {
        const a = h("a", { href: `/api/v2${srv(machine, id, "/files/download?path=" + encodeURIComponent(rel))}`, download: "" });
        document.body.append(a); a.click(); a.remove();
    }

    async function newFolder() {
        const name = await promptText({ title: "New folder", label: "Folder name", iconName: "folderPlus", confirmLabel: "Create", validate: validName });
        if (!name) return;
        try { await post(srv(machine, id, "/files/folder"), { path, name: name.trim() }); load(); }
        catch (e) { toastError(e, "Couldn't create the folder"); }
    }

    async function rename(rel, name) {
        const newName = await promptText({ title: `Rename ${name}`, label: "New name", value: name, confirmLabel: "Rename", validate: validName });
        if (!newName || newName === name) return;
        try { await post(srv(machine, id, "/files/rename"), { path: rel, newName: newName.trim() }); load(); }
        catch (e) { toastError(e, "Couldn't rename"); }
    }

    async function remove(rel, e) {
        const ok = await confirm({ title: `Delete ${e.name}?`, message: e.isDirectory ? "The folder and everything in it will be deleted. This can't be undone — take a backup first if you're unsure." : "This can't be undone.", confirmLabel: "Delete", danger: true, iconName: "trash" });
        if (!ok) return;
        try { await post(srv(machine, id, "/files/delete"), { path: rel }); toast("Deleted", { type: "good", text: e.name, timeout: 2500 }); load(); }
        catch (ex) { toastError(ex, "Couldn't delete"); }
    }

    async function uploadFiles(files) {
        if (!files.length) return;
        const target = path;
        const total = files.reduce((n, f) => n + f.size, 0);
        const bar = progressBar(0);
        const label = h("span", { class: "grow truncate small", text: `Uploading ${files.length} file${files.length === 1 ? "" : "s"} (${fmtBytes(total)}) to /${target}` });
        const item = h("div", { class: "upload-item" }, h("div", { class: "row" }, icon("upload"), label, h("span", { class: "num small muted", text: "0%" })), bar);
        uploads.append(item);
        const form = new FormData();
        for (const f of files) form.append("files", f, f.name);
        try {
            const res = await upload(srv(machine, id, "/files/upload?path=" + encodeURIComponent(target)), form, p => {
                setProgress(bar, p * 100);
                item.querySelector(".num").textContent = Math.round(p * 100) + "%";
            });
            setProgress(bar, 100, "done");
            toast(`Uploaded ${res.saved.length} file${res.saved.length === 1 ? "" : "s"}`, { type: res.failed.length ? "warn" : "good", text: res.failed.join(" · ") });
            if (target === path) load();
        } catch (e) { setProgress(bar, 100, "failed"); toastError(e, "Upload failed"); }
        finally { setTimeout(() => item.remove(), 2500); }
    }

    // ── Editor ──

    async function openFile(rel, entry) {
        let file;
        try { file = await get(srv(machine, id, "/files/content?path=" + encodeURIComponent(rel))); }
        catch (e) { toastError(e, "Couldn't open the file"); return; }
        if (file.binary) {
            const ok = await confirm({ title: file.name, message: "This is a binary file, so it can't be edited here. Download it instead?", confirmLabel: "Download", iconName: "file" });
            if (ok) downloadFile(rel);
            return;
        }
        openEditor(rel, file);
    }

    function openEditor(rel, file) {
        const validator = pickValidator(file.name);
        const area = h("textarea", { class: "editor-area mono", spellcheck: "false", "aria-label": `Contents of ${file.name}`, readonly: file.readOnly });
        area.value = file.content ?? "";
        const gutter = h("div", { class: "editor-gutter mono", "aria-hidden": "true" });
        const status = h("div", { class: "editor-status" });
        const saveBtn = h("button", { class: "btn primary", disabled: true, onclick: () => save() }, icon("save"), "Save");
        let original = area.value;
        let modified = file.modified;

        const editor = h("div", { class: "editor" },
            h("div", { class: "editor-head" },
                h("button", { class: "btn ghost sm", onclick: () => close() }, icon("chevronLeft"), "Files"),
                h("div", { class: "grow truncate" }, h("b", { text: file.name }), h("span", { class: "small faint", text: `  /${rel} · ${fmtBytes(file.size)}` })),
                file.readOnly ? h("span", { class: "tag amber" }, icon("lock"), "Read-only") : null,
                validator && validator.format ? h("button", { class: "btn sm", disabled: file.readOnly, onclick: () => { try { area.value = validator.format(area.value); changed(); } catch { toast("Fix the errors first", { type: "warn" }); } } }, icon("sparkles"), "Format") : null,
                h("button", { class: "btn sm", title: "Earlier versions — compare or put one back", "aria-label": "History", onclick: () => historyDialog({ machine, id, path: rel, title: "History", onRestored: async () => { await close(); if (!editor.isConnected) openFile(rel); } }) }, icon("clock")),
                h("button", { class: "btn sm", onclick: () => downloadFile(rel) }, icon("download")),
                saveBtn),
            file.note ? h("div", { class: "callout warn" }, icon("info"), h("span", { text: file.note })) : null,
            h("div", { class: "editor-body" }, gutter, area),
            status);

        panel.hidden = true;
        host.append(editor);
        area.focus();

        const dirty = () => area.value !== original;
        function paintGutter() {
            const lines = area.value.split("\n").length;
            if (gutter.childElementCount !== lines) gutter.textContent = Array.from({ length: lines }, (_, i) => i + 1).join("\n");
        }
        function changed() {
            saveBtn.disabled = !dirty() || file.readOnly;
            paintGutter();
            const v = validator ? validator.fn(area.value) : null;
            clear(status).append(
                v ? (v.ok ? h("span", { class: "ok" }, icon("checkCircle"), `Valid ${validator.label}`) : h("span", { class: "bad" }, icon("xCircle"), `${validator.label}: ${v.message}${v.where ? " (" + v.where + ")" : ""}`)) : h("span", { class: "faint", text: "Plain text" }),
                h("span", { class: "spacer" }),
                h("span", { class: "faint", text: dirty() ? "Unsaved changes · Ctrl+S to save" : "Saved" }));
            setLeaveGuard(dirty() ? () => confirm({ title: "Discard your changes?", message: `${file.name} has unsaved changes.`, confirmLabel: "Discard", danger: true }) : null);
        }
        area.addEventListener("input", changed);
        area.addEventListener("scroll", () => { gutter.scrollTop = area.scrollTop; });
        area.addEventListener("keydown", e => {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") { e.preventDefault(); if (dirty()) save(); }
            if (e.key === "Tab" && !e.shiftKey) { e.preventDefault(); area.setRangeText("    ", area.selectionStart, area.selectionEnd, "end"); changed(); }
        });
        changed();

        async function save(force = false) {
            const v = validator ? validator.fn(area.value) : null;
            if (v && !v.ok && !force) {
                const go = await confirm({ title: `Save invalid ${validator.label}?`, message: `${v.message}${v.where ? " (" + v.where + ")" : ""}. The server may refuse to load it.`, confirmLabel: "Save anyway", danger: true });
                if (!go) return;
            }
            saveBtn.classList.add("busy");
            try {
                await put(srv(machine, id, "/files/content"), { path: rel, content: area.value, expectedModified: force ? null : modified });
                original = area.value;
                const fresh = await get(srv(machine, id, "/files/content?path=" + encodeURIComponent(rel)));
                modified = fresh.modified;
                toast("Saved", { type: "good", text: file.name, timeout: 2000 });
                changed();
            } catch (e) {
                if (e.status === 409) {
                    const overwrite = await confirm({ title: "The file changed on disk", message: "Someone (or the game) saved this file after you opened it. Overwrite their version with yours?", confirmLabel: "Overwrite", danger: true });
                    if (overwrite) { saveBtn.classList.remove("busy"); return save(true); }
                } else toastError(e, "Couldn't save");
            } finally { saveBtn.classList.remove("busy"); }
        }

        async function close() {
            if (dirty() && !(await confirm({ title: "Discard your changes?", message: `${file.name} has unsaved changes.`, confirmLabel: "Discard", danger: true }))) return;
            setLeaveGuard(null);
            editor.remove();
            panel.hidden = false;
            load();
        }
        scope.add(() => setLeaveGuard(null));
    }

    await load();
}

function validName(v) {
    const name = (v || "").trim();
    if (!name) return "Enter a name.";
    if (/[\\/:*?"<>|]/.test(name) || name === "." || name === "..") return "Names can't contain \\ / : * ? \" < > |";
    return null;
}

function pickValidator(name) {
    if (/\.json$/i.test(name)) return { label: "JSON", fn: validateJson, format: t => JSON.stringify(JSON.parse(t), null, 2) };
    if (/\.xml$/i.test(name)) return { label: "XML", fn: validateXml };
    return null;
}

function validateJson(text) {
    if (!text.trim()) return { ok: true };
    try { JSON.parse(text); return { ok: true }; }
    catch (e) {
        const msg = String(e.message || "Invalid JSON");
        const pos = /position (\d+)/i.exec(msg);
        let where = "";
        const lc = /line (\d+) column (\d+)/i.exec(msg);
        if (lc) where = `line ${lc[1]}, col ${lc[2]}`;
        else if (pos) { const upto = text.slice(0, +pos[1]); where = `line ${upto.split("\n").length}, col ${upto.length - upto.lastIndexOf("\n")}`; }
        return { ok: false, where, message: msg.replace(/^JSON\.parse:?\s*/i, "").replace(/\s*(in JSON )?at position \d+.*$/i, "").replace(/\s*\(line \d+ column \d+.*\)/i, "") || "Invalid JSON" };
    }
}

function validateXml(text) {
    if (!text.trim()) return { ok: true };
    const doc = new DOMParser().parseFromString(text, "application/xml");
    const err = doc.querySelector("parsererror");
    if (!err) return { ok: true };
    const msg = (err.textContent || "Invalid XML").replace(/\s+/g, " ").trim();
    const lc = /line\D*(\d+)\D+column\D*(\d+)/i.exec(msg);
    return { ok: false, where: lc ? `line ${lc[1]}, col ${lc[2]}` : "", message: msg.replace(/^This page contains the following errors:\s*/i, "").replace(/Below is a rendering.*$/i, "").replace(/^error on line \d+ at column \d+:\s*/i, "").slice(0, 160) };
}
