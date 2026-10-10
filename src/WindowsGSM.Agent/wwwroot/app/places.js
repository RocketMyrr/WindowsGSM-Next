// Where a server's game files go: "with WindowsGSM" (the usual place) or a folder on another drive. Used when
// installing and for "Move files to another drive…". Admins only (the agent checks too).

import { h, icon, clear, fmtBytes, debounce } from "./dom.js";
import { get, post, srv } from "./api.js";
import { store } from "./store.js";
import { input, field, modal, busy, toast, toastError } from "./ui.js";

/**
 * A picker: a card per drive (free space shown), and the folder to use on it. value() is null for the usual place,
 * else the folder (each server gets a folder of its own inside it). ready() is false while the folder isn't usable.
 */
export function placePicker(machine, { allowUsual = true, usualLabel = "With WindowsGSM (usual)" } = {}) {
    let places = null, choice = allowUsual ? "usual" : null, problem = null, checking = false;
    const cards = h("div", { class: "place-cards" }, h("div", { class: "small muted", text: "Loading drives…" }));
    const folder = input({ class: "input mono", placeholder: "E:\\GameServers" });
    const status = h("div", { class: "small" });
    const folderField = field("Folder", folder, {
        hint: "Each server gets a folder of its own inside it — several servers can share one.",
        help: ["Pick a folder on any drive in this PC (an internal drive, an SSD, or a USB drive that stays connected). WindowsGSM keeps the server's settings, logs and backups list in its own folder; only the game files go here.",
            "Network shares and mapped network drives can't be used — Windows can't link a server to them."],
    });
    folderField.hidden = true;
    const el = h("div", { class: "stack" }, cards, folderField, status);

    const check = debounce(async () => {
        if (choice === "usual" || !folder.value.trim()) { problem = choice === "usual" ? null : "Choose a folder."; paintStatus(); return; }
        checking = true; paintStatus();
        try { problem = (await get(`/machines/${encodeURIComponent(machine)}/file-places?folder=${encodeURIComponent(folder.value.trim())}`)).problem; }
        catch (e) { problem = e.message; }
        checking = false; paintStatus();
    }, 350);
    folder.addEventListener("input", () => { problem = null; check(); });

    function paintStatus() {
        clear(status);
        if (choice === "usual") { status.append(h("span", { class: "muted", text: `Game files go in ${places?.usual || "WindowsGSM's servers folder"}, as usual.` })); return; }
        if (checking) { status.append(h("span", { class: "muted", text: "Checking the folder…" })); return; }
        if (problem) { status.append(h("span", { class: "rose-text" }, icon("warn"), " ", problem)); return; }
        if (folder.value.trim()) { status.append(h("span", { class: "green-text" }, icon("check"), " ", "This folder works.")); }
    }

    function paintCards() {
        clear(cards);
        if (allowUsual) {
            cards.append(card("usual", usualLabel, "Next to WindowsGSM — nothing to choose.", null));
        }
        for (const d of places.drives) {
            const low = d.free < 20 * 1024 ** 3;
            cards.append(card(d.name, `${d.name}${d.label ? " " + d.label : ""}`, `${fmtBytes(d.free)} free of ${fmtBytes(d.total)}${d.removable ? " · removable" : ""}${d.windowsGsm ? " · WindowsGSM's drive" : ""}`, d, low));
        }
    }

    function card(value, title, sub, drive, low = false) {
        return h("button", { type: "button", class: ["place-card", choice === value && "active", low && "low"], "aria-pressed": String(choice === value), onclick: () => {
            choice = value;
            folderField.hidden = value === "usual";
            if (drive && (!folder.value.trim() || !folder.value.toLowerCase().startsWith(drive.name.toLowerCase()))) folder.value = drive.suggestion;
            paintCards();
            check();
            if (value !== "usual") folder.focus();
        } }, icon(value === "usual" ? "logo" : "disk"), h("span", { class: "grow" }, h("b", { text: title }), h("small", { text: sub })),
            low ? h("span", { class: "tag amber", title: "Under 20 GB free" }, "low") : null);
    }

    get(`/machines/${encodeURIComponent(machine)}/file-places`).then(p => { places = p; paintCards(); paintStatus(); })
        .catch(e => { clear(cards).append(h("div", { class: "small rose-text", text: e.message })); });

    return {
        el,
        value: () => choice === "usual" ? null : folder.value.trim(),
        ready: () => choice === "usual" || (!!folder.value.trim() && !problem && !checking),
        problem: () => choice === "usual" ? null : (problem || (folder.value.trim() ? null : "Choose a folder.")),
    };
}

/** Admin dialog: move a stopped server's game files to another drive, or back to the usual place. */
export async function moveFilesDialog(server) {
    let where;
    try { where = await get(srv(server.machine, server.id, "/files-location")); } catch (e) { toastError(e, "Couldn't read where its files are"); return; }
    const picker = placePicker(server.machine, { allowUsual: where.elsewhere, usualLabel: "Back with WindowsGSM" });
    modal({
        title: `Move ${server.name}'s files`, iconName: "disk", wide: true,
        subtitle: `Now: ${where.path}`,
        body: h("div", { class: "stack" },
            picker.el,
            h("div", { class: "callout info" }, icon("info"), h("span", { text: "The files are copied first and checked; only then does the server switch over and the old copy get removed — if anything goes wrong, it keeps using its files where they are now. Big servers take a while; the server must stay stopped until it's done." }))),
        footer: close => {
            const go = h("button", { class: "btn primary", onclick: () => busy(go, async () => {
                const p = picker.problem();
                if (p) { toast(p, { type: "warn" }); return; }
                const res = await post(srv(server.machine, server.id, "/move-files"), { folder: picker.value() });
                if (res && res.job) store.trackJob(res.job, server.machine);
                toast("Moving the files", { type: "info", text: "Follow it in Activity.", timeout: 4000 });
                close(true);
            }, "Couldn't start the move") }, icon("disk"), "Move files");
            return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), go];
        },
    });
}

/** "Open folder": the server's game files in Windows Explorer — shown only when the panel is used on that PC. */
export function openFolderButton(machine, id, label = "Open folder") {
    const b = h("button", { class: "btn ghost sm", title: "Open the game files in Windows Explorer on this PC", onclick: () => busy(b, async () => {
        await post(srv(machine, id, "/open-folder"));
        toast("Opened in Explorer", { type: "good", timeout: 2500 });
    }, "Couldn't open the folder") }, icon("folder"), label);
    return b;
}
