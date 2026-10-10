// ARK tab: the mod list (ARK: Survival Ascended — CurseForge project ids, loaded in order) and clusters (several
// ARK servers sharing one folder so players can move characters and items between maps).

import { h, icon, clear, append } from "../../dom.js";
import { get, put, srv } from "../../api.js";
import { input, field, busy, toast, toastError } from "../../ui.js";
import { can } from "../../perms.js";

export default async function arkTab(host, { id, machine, server }) {
    const base = srv(machine, id, "/ark");
    const modsPanel = h("section", { class: "panel" });
    const clusterPanel = h("section", { class: "panel" });
    host.append(h("div", { class: "stack" }, modsPanel, clusterPanel));
    const editable = can(server(), "EditConfig");
    let state;

    async function load() {
        try { state = await get(base); }
        catch (e) { clear(modsPanel).append(h("div", { class: "panel-body" }, h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message })))); clusterPanel.hidden = true; return; }
        paintMods();
        paintCluster();
    }

    // ── Mods (ASA) ──
    function paintMods() {
        if (state.kind !== "asa") {
            append(clear(modsPanel), [h("div", { class: "panel-head" }, icon("puzzle"), h("h2", { text: "Mods" })),
                h("div", { class: "panel-body" }, h("div", { class: "callout info" }, icon("info"), h("span", { text: "ARK: Survival Evolved loads Steam Workshop mods — use the Workshop tab." })))]);
            return;
        }
        const mods = state.mods.map(m => ({ ...m }));
        const list = h("div", { class: "ark-mods" });
        const newId = input({ class: "input mono", placeholder: "CurseForge project id, e.g. 928793", inputmode: "numeric", maxlength: 10 });
        const newName = input({ placeholder: "Name (optional, for you)", maxlength: 80 });
        const save = h("button", { class: "btn primary", hidden: true, onclick: () => busy(save, async () => {
            state.mods = await put(`${base}/mods`, { mods });
            toast("Mod list saved", { type: "good", text: "Restart the server to load the changes — ARK downloads new mods as it starts." });
            paintMods();
        }, "Couldn't save the mods") }, icon("save"), "Save mod list");
        const dirty = () => { save.hidden = false; };

        function paintList() {
            clear(list);
            if (!mods.length) { list.append(h("div", { class: "small faint pad", text: "No mods. Add CurseForge project ids below — the number shown on the mod's CurseForge page (Project ID)." })); return; }
            mods.forEach((m, i) => list.append(h("div", { class: "ark-mod" },
                h("span", { class: "ark-mod-order mono", text: String(i + 1) }),
                h("div", { class: "grow" }, h("b", { text: m.name || `Mod ${m.id}` }),
                    h("div", { class: "tiny muted" }, h("span", { class: "mono", text: m.id }), " · ",
                        h("a", { href: `https://www.curseforge.com/projects/${encodeURIComponent(m.id)}`, target: "_blank", rel: "noopener" }, "CurseForge"))),
                editable ? h("div", { class: "row ark-mod-actions" },
                    h("button", { class: "btn ghost sm icon-only", title: "Load earlier", "aria-label": "Move up", disabled: i === 0, onclick: () => { [mods[i - 1], mods[i]] = [mods[i], mods[i - 1]]; paintList(); dirty(); } }, icon("arrowUp")),
                    h("button", { class: "btn ghost sm icon-only", title: "Remove", "aria-label": `Remove ${m.name || m.id}`, onclick: () => { mods.splice(i, 1); paintList(); dirty(); } }, icon("x"))) : null)));
        }
        function add() {
            const idText = newId.value.trim();
            if (!/^\d{3,10}$/.test(idText)) { toast("That isn't a CurseForge project id", { type: "warn", text: "Use the number from the mod's CurseForge page (Project ID), e.g. 928793." }); return; }
            if (mods.some(m => m.id === idText)) { toast("Already in the list", { type: "info" }); return; }
            mods.push({ id: idText, name: newName.value.trim() || null });
            newId.value = ""; newName.value = "";
            paintList(); dirty();
            newId.focus();
        }
        newId.addEventListener("keydown", e => { if (e.key === "Enter") add(); });
        append(clear(modsPanel), [
            h("div", { class: "panel-head wrap" }, icon("puzzle"), h("h2", { text: "Mods" }), h("span", { class: "sub", text: "CurseForge · loaded top to bottom" }), h("span", { class: "spacer" }), save),
            h("div", { class: "panel-body stack" }, list,
                editable ? h("div", { class: "row wrap" }, h("div", { class: "grow" }, newId), h("div", { class: "grow" }, newName), h("button", { class: "btn", onclick: add }, icon("plus"), "Add")) : null,
                h("p", { class: "tiny faint", text: "Saved as -mods=… in the server's extra start parameters (Settings). The server downloads and updates the mods itself when it starts." })),
        ]);
        paintList();
    }

    // ── Cluster ──
    function paintCluster() {
        const c = state.cluster;
        const others = state.candidates.filter(o => o.id !== id);
        const clusterId = input({ class: "input mono", value: c.id || "", placeholder: "e.g. my-cluster", maxlength: 40 });
        const dir = input({ class: "input mono", value: c.dir || "", placeholder: state.defaultDir.replace(/my-cluster$/, "<cluster id>") });
        const picks = others.map(o => {
            const box = h("input", { type: "checkbox", checked: !!c.id && o.cluster === c.id, disabled: !editable });
            return { o, box, el: h("label", { class: "ark-pick" }, box, h("span", { text: `${o.name} (#${o.id})` }),
                o.cluster && o.cluster !== c.id ? h("span", { class: "tag amber", title: "Picking it moves it to this cluster" }, `in ${o.cluster}`) : null) };
        });
        const apply = h("button", { class: "btn primary", disabled: !editable, onclick: () => busy(apply, async () => {
            const res = await put(`${base}/cluster`, { clusterId: clusterId.value.trim() || null, servers: picks.filter(p => p.box.checked).map(p => p.o.id), dir: dir.value.trim() || null });
            toast(res.id ? `Cluster "${res.id}" saved` : "Left the cluster", { type: "good", text: res.id ? `${res.members.length} server${res.members.length === 1 ? "" : "s"} share ${res.dir}. Restart them to apply.` : "Restart the server to apply." });
            load();
        }, "Couldn't save the cluster") }, icon("save"), c.id ? "Save cluster" : "Create cluster");
        const leave = c.id && editable ? h("button", { class: "btn", onclick: () => busy(leave, async () => {
            await put(`${base}/cluster`, { clusterId: null });
            toast("Left the cluster", { type: "good", text: "Restart the server to apply." });
            load();
        }, "Couldn't leave the cluster") }, "Leave cluster") : null;

        append(clear(clusterPanel), [
            h("div", { class: "panel-head wrap" }, icon("link"), h("h2", { text: "Cluster" }),
                h("span", { class: "sub", text: c.id ? `"${c.id}" · ${c.members.length} server${c.members.length === 1 ? "" : "s"}` : "Not in a cluster" })),
            h("div", { class: "panel-body stack" },
                h("p", { class: "small muted", text: "Servers in one cluster share a folder, so players can upload a character, dinos and items at an obelisk or terminal and download them on another map." }),
                h("div", { class: "form-grid" }, field("Cluster id", clusterId, { hint: "The same on every server of the cluster." }), field("Shared folder", dir, { hint: "Leave empty for WindowsGSM's clusters folder." })),
                others.length ? h("div", { class: "stack tight" }, h("span", { class: "upper", text: "Other servers in it" }), ...picks.map(p => p.el))
                    : h("div", { class: "small faint", text: "Install another ARK server on this machine to cluster them." }),
                h("div", { class: "row" }, h("span", { class: "spacer" }), leave, apply),
                h("p", { class: "tiny faint", text: "Saved as -clusterid and -ClusterDirOverride in each server's extra start parameters. Each map needs its own ports — WindowsGSM picks them when installing." })),
        ]);
    }

    try { await load(); } catch (e) { toastError(e); }
}
