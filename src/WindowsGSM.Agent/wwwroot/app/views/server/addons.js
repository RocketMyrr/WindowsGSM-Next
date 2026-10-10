// Add-ons: the built-in ones (Oxide, SourceMod…) and custom zips from a URL, kept up to date on start.

import { h, icon, clear } from "../../dom.js";
import { get, post, del, srv } from "../../api.js";
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
                h("button", { class: "btn primary sm", onclick: addCustom }, icon("plus"), "Add")),
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
            builtIn.append(h("div", { class: "list-item" },
                h("span", { class: ["addon-icon", a.present && "present"] }, icon("puzzle")),
                h("div", { class: "grow" }, h("b", { text: a.label }), h("div", { class: "small muted", text: a.present ? "Installed" : "Not installed" })),
                a.present ? managed : null,
                h("button", { class: ["btn sm", !a.present && "primary"], onclick: e => install(e.currentTarget, a) }, icon(a.present ? "refresh" : "download"), a.present ? "Reinstall" : "Install")));
        }
        clear(custom);
        if (!data.custom.length) custom.append(empty("link", "No custom add-ons", "Add a plugin pack, map or mod from a download link — it's re-downloaded whenever you reinstall."));
        for (const c of data.custom) {
            custom.append(h("div", { class: "list-item" },
                h("span", { class: "addon-icon present" }, icon("link")),
                h("div", { class: "grow truncate" }, h("b", { text: c.name }), h("div", { class: "small muted truncate mono", text: c.url + (c.subfolder ? `  →  /${c.subfolder}` : "") })),
                h("button", { class: "btn sm", onclick: e => busy(e.currentTarget, async () => { const r = await post(srv(machine, id, `/addons/custom/${c.id}/install`)); store.trackJob(r.job, machine); }, "Couldn't reinstall") }, icon("refresh"), "Reinstall"),
                h("button", { class: "btn ghost sm icon-only", "aria-label": `Remove ${c.name}`, onclick: async () => {
                    if (!(await confirm({ title: `Remove ${c.name}?`, message: "Forgets this add-on. Files it already installed stay in place.", confirmLabel: "Remove", danger: true }))) return;
                    try { await del(srv(machine, id, `/addons/custom/${c.id}`)); load(); } catch (e) { toastError(e); }
                } }, icon("trash"))));
        }
    }

    async function install(button, a) {
        await busy(button, async () => {
            const res = await post(srv(machine, id, `/addons/${encodeURIComponent(a.key)}/install`));
            store.trackJob(res.job, machine);
            toast(`Installing ${a.label}`, { type: "info", timeout: 3000 });
        }, `Couldn't install ${a.label}`);
    }

    function addCustom() {
        const name = input({ placeholder: "My plugin pack" });
        const url = input({ type: "url", placeholder: "https://example.com/pack.zip", class: "input mono" });
        const sub = input({ placeholder: "e.g. oxide/plugins (optional)", class: "input mono" });
        const urlField = field("Download link (zip)", url, { hint: "http or https only." });
        modal({
            title: "Add a custom add-on", iconName: "link",
            body: h("div", { class: "stack" }, field("Name", name), urlField, field("Extract into", sub, { hint: "A folder inside the server's files. Leave empty for the top level." })),
            footer: close => {
                const go = h("button", { class: "btn primary", onclick: async () => {
                    if (!/^https?:\/\//i.test(url.value.trim())) { urlField.setError("Enter a full http:// or https:// link."); return; }
                    await busy(go, async () => {
                        const res = await post(srv(machine, id, "/addons/custom"), { name: name.value.trim() || "Custom add-on", url: url.value.trim(), subfolder: sub.value.trim() || null });
                        store.trackJob(res.job, machine);
                        close(true);
                        toast("Downloading add-on", { type: "info", timeout: 3000 });
                    }, "Couldn't add it");
                } }, icon("download"), "Download & install");
                return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), go];
            },
        });
    }

    scope.add(store.on("jobFinished", j => { if (j.serverId === id && j.kind === "addon") load(); }));
    await load();
}
