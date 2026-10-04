// Backups: take one now, restore (safely — the agent rolls back if a restore fails), download, delete,
// and choose what's included and how many are kept.

import { h, icon, clear, fmtBytes, fmtDateTime, timeAgo } from "../../dom.js";
import { get, post, put, del, srv } from "../../api.js";
import { store } from "../../store.js";
import { modal, toast, toastError, confirm, empty, toggle, field, input, busy, isStopped } from "../../ui.js";
import { can, isAdmin } from "../../perms.js";

export default async function backupsTab(host, { id, machine, key, server, scope }) {
    const list = h("div");
    const settingsBody = h("div", { class: "stack" });
    const offsiteList = h("div");
    const offsitePanel = h("section", { class: "panel", hidden: true },
        h("div", { class: "panel-head" }, icon("upload"), h("h3", { text: "Off-site" }), h("span", { class: "sub", text: "Copies in this machine's off-site storage" })),
        h("div", { class: "panel-body flush" }, offsiteList));
    const canRestore = can(server(), "Restore");
    let offsiteReady = false;

    host.append(h("div", { class: "split" },
        h("section", { class: "panel" },
            h("div", { class: "panel-head" }, h("h3", { text: "Backups" }), h("span", { class: "spacer" }),
                h("button", { class: "btn sm", title: "Also includes the server's settings and configs", onclick: e => backup(e.currentTarget, true) }, "Full backup"),
                h("button", { class: "btn primary sm", onclick: e => backup(e.currentTarget, false) }, icon("backup"), "Back up now")),
            h("div", { class: "panel-body flush" }, list)),
        h("section", { class: "panel" },
            h("div", { class: "panel-head" }, h("h3", { text: "Backup settings" })),
            h("div", { class: "panel-body" }, settingsBody))),
        offsitePanel);

    async function backup(button, everything) {
        await busy(button, async () => {
            const res = await post(srv(machine, id, "/backups"), { everything });
            store.trackJob(res.job, machine);
            toast("Backup started", { type: "info", text: "You can keep working — it runs in the background.", timeout: 3000 });
        }, "Couldn't start the backup");
    }

    async function loadList() {
        const backups = await get(srv(machine, id, "/backups"));
        clear(list);
        if (!backups.length) { list.append(empty("backup", "No backups yet", "Take one now, or turn on backups before each start.")); return; }
        const newest = backups.reduce((a, b) => (Date.parse(a.created) > Date.parse(b.created) ? a : b));
        list.append(h("table", { class: "table" },
            h("thead", {}, h("tr", {}, h("th", { text: "Backup" }), h("th", { text: "Size" }), h("th", { text: "Taken" }), h("th", { class: "actions" }))),
            h("tbody", {}, ...backups.sort((a, b) => Date.parse(b.created) - Date.parse(a.created)).map(b => h("tr", {},
                h("td", {}, h("div", { class: "row" }, icon("backup"), h("span", { class: "mono small truncate", text: b.name }),
                    b === newest ? h("span", { class: "tag accent", text: "Latest" }) : null,
                    b.format !== "Next" ? h("span", { class: "tag", title: "Made by the previous WindowsGSM — still restorable", text: "Legacy" }) : null)),
                h("td", { class: "num muted", text: fmtBytes(b.size) }),
                h("td", { class: "muted", title: fmtDateTime(b.created), text: timeAgo(b.created) }),
                h("td", { class: "actions" },
                    canRestore ? h("button", { class: "btn sm", onclick: () => restore(b) }, icon("restore"), "Restore") : null,
                    offsiteReady ? h("button", { class: "btn ghost sm icon-only", "aria-label": "Upload off-site", title: "Upload this backup to the off-site storage now", onclick: e => uploadOffsite(b, e.currentTarget) }, icon("upload")) : null,
                    h("button", { class: "btn ghost sm icon-only", "aria-label": "Test this backup", title: "Test this backup — checks every file in it is intact, without restoring anything", onclick: e => test(b, e.currentTarget) }, icon("checkCircle")),
                    h("a", { class: "btn ghost sm icon-only", "aria-label": "Download", title: "Download", href: `/api/v2${srv(machine, id, `/backups/${encodeURIComponent(b.name)}/download`)}`, download: "" }, icon("download")),
                    h("button", { class: "btn ghost sm icon-only", "aria-label": "Delete", title: "Delete", onclick: () => remove(b) }, icon("trash"))))))));
    }

    async function restore(b) {
        const s = server();
        const includeConfig = toggle("Also restore the server's settings", false, { hint: "Only if the backup includes them (full backups do).", help: "Puts back the server's WindowsGSM settings (ports, start parameters, auto-restart…) as they were when the backup was made — useful when restoring onto a fresh install. Leave off to keep today's settings and only restore the files." });
        const running = !isStopped(s);
        const m = modal({
            title: `Restore ${s.name}?`,
            subtitle: `Replaces the server's files with the backup from ${fmtDateTime(b.created)}. If anything goes wrong part-way, the current files are put back.`,
            iconName: "restore", tone: "danger",
            body: h("div", { class: "stack" },
                running ? h("div", { class: "callout warn" }, icon("warn"), h("span", { text: "Stop the server before restoring." })) : null,
                includeConfig),
            footer: close => [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"),
                h("button", { class: "btn danger solid", disabled: running, onclick: () => close(true) }, icon("restore"), "Restore")],
        });
        if (!(await m.done)) return;
        try {
            const res = await post(srv(machine, id, "/restore"), { name: b.name, includeConfig: includeConfig.input.checked });
            store.trackJob(res.job, machine);
            toast("Restore started", { type: "info", timeout: 3000 });
        } catch (e) { toastError(e, "Couldn't restore"); }
    }

    async function test(b, button) {
        await busy(button, async () => {
            const res = await post(srv(machine, id, `/backups/${encodeURIComponent(b.name)}/test`));
            store.trackJob(res.job, machine);
            toast("Testing the backup", { type: "info", text: "Every file is read back and checked. Nothing is changed.", timeout: 3000 });
        }, "Couldn't test the backup");
    }

    async function uploadOffsite(b, button) {
        await busy(button, async () => {
            const res = await post(srv(machine, id, `/backups/${encodeURIComponent(b.name)}/upload-offsite`));
            store.trackJob(res.job, machine);
            toast("Uploading off-site", { type: "info", text: "It runs in the background — the server isn't held up.", timeout: 3000 });
        }, "Couldn't start the upload");
    }

    // ── Off-site copies ──
    async function loadOffsite() {
        let res;
        try { res = await get(srv(machine, id, "/backups/offsite")); }
        catch (e) { offsitePanel.hidden = false; clear(offsiteList).append(h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message }))); return; }
        offsiteReady = res.ready;
        offsitePanel.hidden = !res.ready;
        if (!res.ready) return;
        clear(offsiteList);
        if (!res.backups.length) {
            offsiteList.append(empty("upload", "Nothing off-site yet", res.upload ? "The next backup is uploaded automatically — or use the upload button on a backup above." : "Turn on “Also upload each backup off-site” in Backup settings, or upload one with its button above."));
            return;
        }
        offsiteList.append(h("table", { class: "table" },
            h("thead", {}, h("tr", {}, h("th", { text: "Backup" }), h("th", { text: "Size" }), h("th", { text: "Uploaded" }), h("th", { class: "actions" }))),
            h("tbody", {}, ...res.backups.map(b => h("tr", {},
                h("td", {}, h("div", { class: "row" }, icon("upload"), h("span", { class: "mono small truncate", text: b.name }))),
                h("td", { class: "num muted", text: fmtBytes(b.size) }),
                h("td", { class: "muted", title: fmtDateTime(b.uploaded), text: timeAgo(b.uploaded) }),
                h("td", { class: "actions" }, canRestore ? h("button", { class: "btn sm", title: "Download it into this server's backups, to restore from there", onclick: e => bringBack(b, e.currentTarget) }, icon("download"), "Bring back") : null))))));
    }

    async function bringBack(b, button) {
        await busy(button, async () => {
            const res = await post(srv(machine, id, `/backups/offsite/${encodeURIComponent(b.name)}/download`));
            store.trackJob(res.job, machine);
            toast("Bringing it back", { type: "info", text: "When it's done it appears in the Backups list — restore it from there.", timeout: 4000 });
        }, "Couldn't bring it back");
    }

    async function remove(b) {
        if (!(await confirm({ title: "Delete this backup?", message: `${b.name} (${fmtBytes(b.size)}) will be deleted permanently.`, confirmLabel: "Delete", danger: true, iconName: "trash" }))) return;
        try { await del(srv(machine, id, `/backups/${encodeURIComponent(b.name)}`)); loadList(); }
        catch (e) { toastError(e, "Couldn't delete the backup"); }
    }

    async function loadSettings() {
        const cfg = await get(srv(machine, id, "/backups/settings"));
        const beforeStart = toggle("Back up before every start", cfg.beforeStart, { hint: "Also before automatic restarts after a crash.", help: "A safety net: every start makes a backup first, so a bad mod or a broken update can always be undone. Starts take a little longer on big servers — combine with Keep at most so old ones are cleared." });
        const keepCount = input({ type: "number", min: 0, value: cfg.keepCount, class: "input num" });
        const keepDays = input({ type: "number", min: 0, value: cfg.keepDays, class: "input num" });
        const paths = h("textarea", { class: "input mono", rows: 4, placeholder: "Leave empty to back up all server files.\nOne folder or file per line, e.g.\nsaves\nconfig/server.cfg" });
        paths.value = cfg.paths.join("\n");
        const external = h("textarea", { class: "input mono", rows: 2, disabled: !isAdmin(), placeholder: "%USERPROFILE%\\AppData\\LocalLow\\SomeGame\\Worlds" });
        external.value = cfg.externalLocations.join("\n");
        const location = input({ value: cfg.location, disabled: !isAdmin(), placeholder: "Default: the data folder's backups folder" });
        const copyTo = input({ value: cfg.copyTo || "", disabled: !isAdmin(), placeholder: "e.g. E:\\Backups or \\\\nas\\wgsm (optional)" });
        const uploadOffsite = toggle("Also upload each backup off-site", !!cfg.uploadOffsite, {
            disabled: !isAdmin() || (!cfg.offsiteReady && !cfg.uploadOffsite),
            hint: !cfg.offsiteReady ? "Set up off-site storage first: Agent settings → Off-site backups." : isAdmin() ? "Each new backup is uploaded in the background; the newest few are kept there." : "Only admins can change this.",
            help: "A copy away from this PC — safe from a dead drive, theft or fire. Uploads run as their own job, so the server isn't held up; a failed upload is reported and the backup on this PC is kept either way.",
        });
        const saveBtn = h("button", { class: "btn primary", onclick: async () => {
            await busy(saveBtn, async () => {
                await put(srv(machine, id, "/backups/settings"), {
                    paths: lines(paths.value), externalLocations: lines(external.value), beforeStart: beforeStart.input.checked,
                    keepCount: Number(keepCount.value) || 0, keepDays: Number(keepDays.value) || 0, location: location.value.trim(), copyTo: copyTo.value.trim(),
                    uploadOffsite: uploadOffsite.input.checked,
                });
                toast("Backup settings saved", { type: "good", timeout: 2500 });
            }, "Couldn't save the backup settings");
        } }, icon("save"), "Save");

        clear(settingsBody).append(
            beforeStart,
            h("div", { class: "form-grid two" },
                field("Keep at most", keepCount, { hint: "backups (0 = no limit)", help: "When a new backup is made, the oldest ones beyond this number are deleted. Backups you made by hand count too." }),
                field("Delete after", keepDays, { hint: "days (0 = never)", help: "Backups older than this are deleted when the next one is made. Use it with Keep at most, or on its own." })),
            field("What to back up", paths, { hint: "Paths inside the server's files.", help: ["Empty backs up everything (the safe choice). Listing folders — e.g. Saved, world, server/my_server_identity — makes backups much smaller and faster by skipping the game's own files, which an update or Verify can always bring back."] }),
            field("Folders outside the server", external, { help: "Some games keep worlds or saves outside the server folder (for example in Documents or AppData). List those folders so they're backed up — and restored — too.", hint: isAdmin() ? "Some games keep worlds elsewhere. Restores only write back to places listed here." : "Only admins can change this." }),
            field("Save backups to", location, { help: "Where the backup zips go. Empty uses the server's own backups folder. A different drive or a network share (\\nas\backups) protects you if this drive fails.", hint: isAdmin() ? "Another drive or a network share is safer." : "Only admins can change this." }),
            uploadOffsite,
            field("Also copy each backup to", copyTo, { help: "A second copy of every backup somewhere else — the classic \"two places\" rule. If the copy fails (a share offline), the main backup is still kept and the log says why.", hint: isAdmin() ? "A second copy somewhere else — another drive or a network share. The same keep rules apply there." : "Only admins can change this." }),
            h("div", { class: "row" }, h("span", { class: "spacer" }), saveBtn));
    }

    scope.add(store.on("jobFinished", j => {
        if (j.serverId !== id) return;
        if (j.kind === "backup" || j.kind === "restore" || j.kind === "backup-test" || j.kind === "offsite") loadList();
        if (j.kind === "offsite" || j.kind === "backup") loadOffsite();
    }));
    await loadOffsite(); // first: the list shows upload buttons when off-site storage is set up
    await Promise.all([loadList(), loadSettings()]);
}

function lines(text) { return text.split(/\r?\n/).map(s => s.trim()).filter(Boolean); }
