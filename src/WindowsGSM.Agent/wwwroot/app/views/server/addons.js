// Add-ons: the built-in ones (Oxide, SourceMod…) and custom zips from a URL, kept up to date on start.

import { h, icon, clear } from "../../dom.js";
import { get, post, put, del, srv } from "../../api.js";
import { store } from "../../store.js";
import { modal, toast, toastError, confirm, empty, field, input, busy, toggle } from "../../ui.js";

export default async function addonsTab(host, { id, machine, key, scope }) {
    const builtIn = h("div");
    const custom = h("div");
    host.append(h("div", { class: "stack loose" },
        h("section", { class: "panel" },
            h("div", { class: "panel-head" }, h("h2", { text: "Built-in add-ons" }), h("span", { class: "sub", text: "Mod frameworks WindowsGSM knows how to install and update." })),
            h("div", { class: "panel-body flush" }, builtIn)),
        h("section", { class: "panel" },
            h("div", { class: "panel-head" }, h("h2", { text: "Custom add-ons" }), h("span", { class: "sub", text: "Any zip from a web address, extracted into the server." }), h("span", { class: "spacer" }),
                h("button", { class: "btn primary sm", onclick: () => customDialog() }, icon("plus"), "Add")),
            h("div", { class: "panel-body flush" }, custom))));

    async function load() {
        const data = await get(srv(machine, id, "/addons"));
        clear(builtIn);
        if (!data.builtIn.length) builtIn.append(empty("puzzle", "None for this game", "Built-in add-ons exist for games like Rust (Oxide/uMod) and Source games (MetaMod, SourceMod)."));
        for (const a of data.builtIn) {
            const managed = toggle("Keep updated", a.managed, {
                hint: a.managed ? "Updated before each start (when update-add-ons-on-start is on)." : "Left alone — use this for custom builds.",
                onChange: async on => {
                    try { await post(srv(machine, id, `/addons/${encodeURIComponent(a.key)}/manage`), { managed: on }); load(); }
                    catch (e) { toastError(e); managed.input.checked = !on; }
                },
            });
            // A custom add-on installs this one (e.g. Oxide staging): it's updated from there, so no built-in buttons.
            const action = a.providedBy ? null
                : a.conflictsWith && !a.present
                    ? h("button", { class: "btn sm", disabled: true, title: `Remove ${a.conflictsWith} first — they can't run together.` }, icon("download"), "Install")
                    : h("button", { class: ["btn sm", (!a.present || a.updateAvailable) && "primary"], onclick: e => install(e.currentTarget, a) },
                        icon(a.present ? "refresh" : "download"), !a.present ? "Install" : a.updateAvailable ? `Update to ${a.latestVersion}` : "Reinstall");
            const remove = a.removable && !a.providedBy
                ? h("button", { class: "btn ghost sm icon-only", "aria-label": `Remove ${a.label}`, title: `Remove ${a.label}`, onclick: e => removeBuiltIn(e.currentTarget, a) }, icon("trash"))
                : null;
            builtIn.append(h("div", { class: "list-item" },
                h("span", { class: ["addon-icon", a.present && "present"] }, icon("puzzle")),
                h("div", { class: "grow" }, h("b", { text: a.label }), h("div", { class: "small muted" }, versionLine(a))),
                a.present && !a.providedBy ? managed : null,
                action, remove));
        }
        clear(custom);
        if (!data.custom.length) custom.append(empty("link", "No custom add-ons", "Add a plugin pack, map or mod from a download link — it's re-downloaded whenever you reinstall."));
        for (const c of data.custom) {
            custom.append(h("div", { class: "list-item" },
                h("span", { class: "addon-icon present" }, icon("link")),
                h("div", { class: "grow truncate" }, h("b", { text: c.name }), h("div", { class: "small muted truncate mono", text: c.url + (c.subfolder ? `  →  /${c.subfolder}` : "") })),
                h("button", { class: "btn sm", onclick: e => busy(e.currentTarget, async () => { const r = await post(srv(machine, id, `/addons/custom/${c.id}/install`)); store.trackJob(r.job, machine); }, "Couldn't reinstall") }, icon("refresh"), "Reinstall"),
                h("button", { class: "btn ghost sm icon-only", "aria-label": `Edit ${c.name}`, title: "Edit", onclick: () => customDialog(c) }, icon("pencil")),
                h("button", { class: "btn ghost sm icon-only", "aria-label": `Remove ${c.name}`, onclick: async () => {
                    if (!(await confirm({ title: `Remove ${c.name}?`, message: "Forgets this add-on. Files it already installed stay in place.", confirmLabel: "Remove", danger: true }))) return;
                    try { await del(srv(machine, id, `/addons/custom/${c.id}`)); load(); } catch (e) { toastError(e); }
                } }, icon("trash"))));
        }
    }

    // "Installed 2.0.7807 · Latest 2.0.7830", with a staging build called out (Update installs the release).
    function versionLine(a) {
        if (!a.present) return a.latestVersion ? [`Not installed · Latest ${a.latestVersion}`] : ["Not installed"];
        const parts = [h("span", { text: a.installedVersion ? `Installed ${a.installedVersion}` : "Installed" })];
        if (a.staging) parts.push(" ", h("span", { class: "tag amber", title: a.providedBy ? "A staging/development build, from your custom add-on." : "A staging/development build, not the release. Update installs the release.", text: "staging" }));
        if (a.conflictsWith) parts.push(" ", h("span", { class: "tag rose", title: `${a.label} and ${a.conflictsWith} can't run on the same server.`, text: `Conflicts with ${a.conflictsWith}` }));
        if (a.providedBy) {
            parts.push(" ", h("span", { class: "tag cyan", title: "Update add-ons installs this custom add-on instead of the release. Remove the custom add-on to go back to the release.", text: `From custom add-on “${a.providedBy}”` }));
            return parts;
        }
        if (a.latestVersion) parts.push(` · Latest ${a.latestVersion}`);
        if (a.updateAvailable) parts.push(" ", h("span", { class: "tag violet", text: "Update available" }));
        else if (a.installedVersion && a.latestVersion) parts.push(" ", h("span", { class: "tag green", text: "Up to date" }));
        return parts;
    }

    async function install(button, a) {
        await busy(button, async () => {
            const res = await post(srv(machine, id, `/addons/${encodeURIComponent(a.key)}/install`));
            store.trackJob(res.job, machine);
            toast(`Installing ${a.label}`, { type: "info", timeout: 3000 });
        }, `Couldn't install ${a.label}`);
    }

    async function removeBuiltIn(button, a) {
        const oxide = a.key === "oxide";
        const ok = await confirm({
            title: `Remove ${a.label}?`,
            message: oxide
                ? "Deletes Oxide's files, then runs Verify files to put back the Rust files Oxide replaced. The oxide folder (plugins, configs, data) is kept. The server must be stopped."
                : "Deletes Carbon's loader and program files. The carbon folder's plugins, configs and data are kept. The server must be stopped.",
            confirmLabel: "Remove", danger: true,
        });
        if (!ok) return;
        await busy(button, async () => {
            const res = await post(srv(machine, id, `/addons/${encodeURIComponent(a.key)}/remove`));
            store.trackJob(res.job, machine);
        }, `Couldn't remove ${a.label}`);
    }

    // Adds a custom add-on (downloads it now), or edits a saved one (used from its next install).
    function customDialog(existing) {
        const name = input({ placeholder: "My plugin pack", value: existing?.name ?? "" });
        const url = input({ type: "url", placeholder: "https://example.com/pack.zip", class: "input mono", value: existing?.url ?? "" });
        const sub = input({ placeholder: "e.g. oxide/plugins (optional)", class: "input mono", value: existing?.subfolder ?? "" });
        const urlField = field("Download link (zip)", url, { hint: "http or https only." });
        modal({
            title: existing ? `Edit ${existing.name}` : "Add a custom add-on", iconName: existing ? "pencil" : "link",
            body: h("div", { class: "stack" }, field("Name", name), urlField, field("Extract into", sub, { hint: "A folder inside the server's files. Leave empty for the top level." })),
            footer: close => {
                const go = h("button", { class: "btn primary", onclick: async () => {
                    if (!/^https?:\/\//i.test(url.value.trim())) { urlField.setError("Enter a full http:// or https:// link."); return; }
                    const body = { name: name.value.trim() || "Custom add-on", url: url.value.trim(), subfolder: sub.value.trim() || null };
                    await busy(go, async () => {
                        if (existing) {
                            await put(srv(machine, id, `/addons/custom/${existing.id}`), body);
                            close(true);
                            toast("Saved — Reinstall to download it now", { type: "success", timeout: 3000 });
                            load();
                            return;
                        }
                        const res = await post(srv(machine, id, "/addons/custom"), body);
                        store.trackJob(res.job, machine);
                        close(true);
                        toast("Downloading add-on", { type: "info", timeout: 3000 });
                    }, existing ? "Couldn't save it" : "Couldn't add it");
                } }, icon(existing ? "check" : "download"), existing ? "Save" : "Download & install");
                return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), go];
            },
        });
    }

    scope.add(store.on("jobFinished", j => { if (j.serverId === id && j.kind === "addon") load(); }));
    await load();
}
