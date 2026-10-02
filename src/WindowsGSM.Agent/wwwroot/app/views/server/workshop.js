// Steam Workshop mods: paste an item or collection link, download them (and keep them up to date), remove.
// Where they go depends on the game: their own folders, Arma/DayZ @Mod folders with keys and -mod=, or
// Conan's modlist.txt.

import { h, icon, clear, append, fmtBytes, timeAgo, fmtDateTime } from "../../dom.js";
import { get, post, put, del, srv } from "../../api.js";
import { store } from "../../store.js";
import { toast, toastError, confirm, empty, loading, busy, field, input, select, toggle, isStopped } from "../../ui.js";

const STYLES = [
    { value: "folder", label: "Each mod in its own folder" },
    { value: "arma", label: "Arma 3 / DayZ: @Mod folders, keys, -mod= start parameter" },
    { value: "conan", label: "Conan Exiles: listed in modlist.txt" },
];

export default async function workshopTab(host, { id, machine, key, server, scope }) {
    const list = h("div", { class: "panel-body flush" });
    const settingsBody = h("div", { class: "panel-body stack" });
    const link = input({ placeholder: "Paste a Workshop link or collection link (or the number)", "aria-label": "Workshop link" });
    const addBtn = h("button", { class: "btn primary", onclick: () => add() }, icon("plus"), "Add");
    const updateBtn = h("button", { class: "btn sm", onclick: () => update(false) }, icon("download"), "Download & update");
    const status = h("div", { class: "small muted" });
    let data;

    host.append(h("div", { class: "split workshop-split" },
        h("section", { class: "panel" },
            h("div", { class: "panel-head wrap" }, h("h3", { text: "Workshop mods" }), status, h("span", { class: "spacer" }),
                h("button", { class: "btn ghost sm", title: "Check Steam for newer versions", onclick: e => busy(e.currentTarget, async () => { await post(srv(machine, id, "/workshop/refresh")); await load(); }, "Couldn't reach Steam") }, icon("refresh")),
                updateBtn),
            h("form", { class: "panel-body row workshop-add", onsubmit: e => { e.preventDefault(); add(); } }, h("div", { class: "grow" }, link), addBtn),
            list),
        h("section", { class: "panel" }, h("div", { class: "panel-head" }, h("h3", { text: "Settings" })), settingsBody)));

    async function load() {
        try { data = await get(srv(machine, id, "/workshop")); }
        catch (e) { clear(list).append(empty("warn", "Couldn't load the mods", e.message)); return; }
        paintList();
        paintSettings();
    }

    function paintList() {
        const items = data.settings.items;
        const outdated = items.filter(i => i.installed !== i.updated || !i.folder).length;
        status.textContent = items.length ? `${items.length} mod${items.length === 1 ? "" : "s"}${outdated ? ` · ${outdated} to download` : " · up to date"}` : "";
        updateBtn.disabled = !items.length || !isStopped(server());
        updateBtn.title = isStopped(server()) ? "" : "Stop the server first";
        clear(list);
        if (!items.length) {
            list.append(empty("steam", "No Workshop mods yet", data.steam ? "Paste a mod's or a collection's Workshop link above." : "This game isn't from Steam, so it has no Workshop."));
            return;
        }
        for (const i of items) {
            const state = !i.folder ? h("span", { class: "tag", text: "Not downloaded" })
                : i.installed !== i.updated ? h("span", { class: "tag amber", text: "Update available" })
                : h("span", { class: "tag green" }, icon("check"), "Installed");
            list.append(h("div", { class: "list-item" },
                h("span", { class: "sched-icon" }, icon("box")),
                h("div", { class: "grow" },
                    h("div", { class: "row wrap" }, h("b", { class: "truncate", text: i.title || i.id }), state),
                    h("div", { class: "small muted truncate" },
                        h("a", { href: `https://steamcommunity.com/sharedfiles/filedetails/?id=${encodeURIComponent(i.id)}`, target: "_blank", rel: "noopener", text: i.id }),
                        ` · ${fmtBytes(i.size)} · updated `, h("span", { title: fmtDateTime(i.updated * 1000), text: timeAgo(i.updated * 1000) }),
                        i.folder ? ` · ${i.folder}` : "")),
                h("button", { class: "btn ghost sm icon-only", "aria-label": `Remove ${i.title}`, title: "Remove", onclick: () => remove(i) }, icon("trash"))));
        }
    }

    function paintSettings() {
        const s = data.settings;
        const appId = input({ value: s.appId, class: "input mono", inputmode: "numeric", placeholder: "e.g. 221100" });
        const style = select(STYLES, s.style);
        const path = input({ value: s.path, class: "input mono", placeholder: "workshop" });
        const account = toggle("Use the Steam account", s.useSteamAccount, { hint: "Many games (DayZ, Arma 3…) only let owners download their Workshop items. Uses the Steam account in Agent settings.", help: "Anonymous downloads work for most games. If mods fail with \"access denied\" or nothing downloads, the game wants an owner: sign in under Agent settings → Steam account (it handles Steam Guard), then turn this on." });
        const before = toggle("Update mods before every start", s.updateBeforeStart, { help: "Checks the Workshop for newer versions of your mods each time the server starts, so players with updated mods can still join. Adds a few seconds to starting." });
        const pathField = field("Folder", path, { hint: "Inside the server's files.", help: "Where mods are put. The install style below fills in the usual place for the game; change it only if a guide for your game says so." });
        const sync = () => { pathField.hidden = style.value === "arma"; };
        style.addEventListener("change", sync);
        sync();
        const save = h("button", { class: "btn primary", onclick: () => busy(save, async () => {
            await put(srv(machine, id, "/workshop/settings"), { appId: appId.value.trim(), style: style.value, path: path.value.trim(), useSteamAccount: account.input.checked, updateBeforeStart: before.input.checked });
            toast("Workshop settings saved", { type: "good", timeout: 2500 });
            await load();
        }, "Couldn't save") }, icon("save"), "Save");
        append(clear(settingsBody), [
            data.known ? h("div", { class: "callout info" }, icon("info"), h("span", { text: `Set up for ${data.known.game} (Workshop app ${data.known.appId}).` })) : null,
            field("Game's Steam app id", appId, { hint: "Workshop mods belong to the game, not its dedicated server — it's the number in the game's Steam store link. Filled in from the first mod you add if empty." }),
            field("Install as", style, { help: "How the game expects its mods: DayZ / Arma want @Mod folders plus keys and a -mod= list; Conan Exiles wants modlist.txt; others just want the files in a folder." }), pathField, account, before,
            h("div", { class: "row" }, h("span", { class: "spacer" }), save)]);
    }

    async function add() {
        const text = link.value.trim();
        if (!text) { link.focus(); return; }
        await busy(addBtn, async () => {
            const r = await post(srv(machine, id, "/workshop/items"), { link: text });
            if (r.added.length) toast(`Added ${r.added.length} mod${r.added.length === 1 ? "" : "s"}`, { type: "good", text: "Download them with “Download & update” (server stopped)." });
            if (r.problems.length) toast(r.added.length ? "Some weren't added" : "Nothing added", { type: "warn", text: r.problems.join(" · "), timeout: 12000 });
            link.value = "";
            await load();
        }, "Couldn't add the mod");
    }

    async function update(everything) {
        await busy(updateBtn, async () => {
            const res = await post(srv(machine, id, "/workshop/update"), { everything });
            store.trackJob(res.job, machine);
            toast("Downloading mods", { type: "info", text: "Follow it in Activity.", timeout: 3000 });
        }, "Couldn't update the mods");
    }

    async function remove(i) {
        if (!(await confirm({ title: `Remove ${i.title}?`, message: i.folder ? "Its files are deleted from the server, and it's taken out of the game's mod list." : "It's taken off the list.", confirmLabel: "Remove", danger: true, iconName: "trash" }))) return;
        try { await del(srv(machine, id, `/workshop/items/${encodeURIComponent(i.id)}`)); await load(); }
        catch (e) { toastError(e, "Couldn't remove it"); }
    }

    scope.add(store.on("jobFinished", j => { if (j.serverId === id && j.kind === "workshop") load(); }));
    scope.add(store.on("server:" + key, () => { if (data) paintList(); }));
    await load();
}
