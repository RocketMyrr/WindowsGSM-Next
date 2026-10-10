// Minecraft tab (Java Edition): which server software runs (Vanilla, Paper, Purpur, Fabric) and which version,
// and plugins / mods from Modrinth.

import { h, icon, clear, append, debounce, fmtDateTime } from "../../dom.js";
import { get, post, del, srv } from "../../api.js";
import { store } from "../../store.js";
import { select, busy, toast, toastError, confirm, loading, isStopped } from "../../ui.js";
import { can } from "../../perms.js";

export default async function minecraftTab(host, { id, machine, server, scope }) {
    const base = srv(machine, id, "/minecraft");
    const software = h("section", { class: "panel" });
    const addons = h("section", { class: "panel" });
    host.append(h("div", { class: "stack" }, software, addons));

    let state;
    async function load() {
        try { state = await get(base); }
        catch (e) { clear(software).append(h("div", { class: "panel-body" }, h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message })))); return; }
        paintSoftware();
        paintAddons();
    }

    // ── Server software ──
    function paintSoftware() {
        const i = state.installed;
        const flavorName = f => state.flavors.find(x => x.id === f)?.name || f;
        let flavor = i?.flavor || "paper";
        const cards = h("div", { class: "mc-flavors" });
        const version = select([{ value: "latest", label: "Newest version" }], "latest");
        const versionNote = h("span", { class: "small muted" });
        const go = h("button", { class: "btn primary", disabled: !can(server(), "Update"), onclick: () => install() }, icon("download"), "Install");

        function paintCards() {
            clear(cards).append(...state.flavors.map(f => h("button", { class: ["mc-flavor", f.id === flavor && "active"], type: "button", onclick: () => { flavor = f.id; paintCards(); loadVersions(); } },
                h("b", { text: f.name }), h("span", { class: "small muted", text: f.description }),
                i?.flavor === f.id ? h("span", { class: "tag green" }, "installed") : null)));
        }
        async function loadVersions() {
            clear(version).append(h("option", { value: "latest" }, "Newest version"));
            versionNote.textContent = "Loading versions…";
            const asked = flavor;
            try {
                const list = await get(`${base}/versions?flavor=${flavor}`);
                if (asked !== flavor) return;
                for (const v of list.slice(0, 80)) version.append(h("option", { value: v }, v));
                if (i && i.flavor === flavor && !i.followLatest && list.includes(i.version)) version.value = i.version;
                versionNote.textContent = "";
            } catch (e) { versionNote.textContent = e.message; }
        }
        async function install() {
            const s = server();
            if (!isStopped(s)) { toast("Stop the server first", { type: "warn" }); return; }
            const changing = i && i.flavor !== flavor;
            if (changing && !(await confirm({
                title: `Switch to ${flavorName(flavor)}?`,
                message: `${flavorName(i.flavor)} is installed now. Worlds stay; the old server.jar is kept as server.jar.previous. ${flavor === "fabric" || i.flavor === "fabric" ? "Plugins and Fabric mods don't mix — what's installed for one won't load in the other." : ""}`,
                confirmLabel: "Switch", iconName: "restart",
            }))) return;
            await busy(go, async () => {
                const res = await post(`${base}/software`, { flavor, version: version.value });
                if (res && res.job) store.trackJob(res.job, machine);
                toast(`Installing ${flavorName(flavor)}`, { type: "info", text: "Follow it in Activity.", timeout: 4000 });
                const done = () => load();
                scope.add(store.on("jobFinished", j => { if (j.serverId === id) done(); }));
            }, "Couldn't install it");
        }

        append(clear(software), [
            h("div", { class: "panel-head wrap" }, icon("box"), h("h2", { text: "Server software" }),
                i ? h("span", { class: "sub", text: `${flavorName(i.flavor)} ${i.version}${i.build ? ` · build ${i.build}` : ""}${i.followLatest ? " · follows the newest version" : ""}` })
                  : h("span", { class: "sub", text: "Vanilla, as installed (updates take the newest Minecraft)" })),
            h("div", { class: "panel-body stack" },
                cards,
                h("div", { class: "row wrap" }, h("label", { class: "small", text: "Minecraft version" }), version, versionNote, h("span", { class: "spacer" }), go),
                h("p", { class: "tiny faint", text: "Updates (and auto-update) then fetch the newest build of this software for the same Minecraft version — pick \"Newest version\" to move to new Minecraft releases automatically. Back up before switching between big versions." })),
        ]);
        paintCards();
        loadVersions();
    }

    // ── Plugins / mods (Modrinth) ──
    function paintAddons() {
        const a = state.addons;
        if (!a) {
            append(clear(addons), [h("div", { class: "panel-head" }, icon("puzzle"), h("h2", { text: "Plugins & mods" })),
                h("div", { class: "panel-body" }, h("div", { class: "callout info" }, icon("info"), h("span", { text: "Vanilla can't load plugins or mods. Install Paper or Purpur for plugins, or Fabric for mods." })))]);
            return;
        }
        const noun = a.kind === "plugin" ? "plugin" : "mod";
        const canEdit = can(server(), "Addons");
        const list = h("div", { class: "mc-addons" });
        const results = h("div", { class: "mc-results" });
        const box = h("input", { class: "input", type: "search", placeholder: `Search Modrinth for ${noun}s…`, "aria-label": `Search ${noun}s` });
        box.addEventListener("input", debounce(() => search(box.value.trim()), 300));

        function paintList() {
            clear(list);
            if (!a.tracked.length && !a.others.length) { list.append(h("div", { class: "small faint pad", text: `No ${noun}s yet. Search below to add some.` })); return; }
            for (const t of a.tracked) {
                const remove = canEdit ? h("button", { class: "btn ghost sm icon-only", title: "Remove", "aria-label": `Remove ${t.title}`, onclick: async () => {
                    if (!isStopped(server())) { toast("Stop the server first", { type: "warn" }); return; }
                    if (!(await confirm({ title: `Remove ${t.title}?`, message: `Deletes ${a.folder}\\${t.file}. Its config folder stays.`, confirmLabel: "Remove", danger: true, iconName: "trash" }))) return;
                    try { await del(`${base}/addons/${encodeURIComponent(t.projectId)}`); load(); } catch (e) { toastError(e, "Couldn't remove it"); }
                } }, icon("trash")) : null;
                list.append(h("div", { class: "mc-addon" },
                    t.iconUrl ? h("img", { src: t.iconUrl, alt: "", class: "mc-icon", loading: "lazy", referrerpolicy: "no-referrer" }) : h("span", { class: "mc-icon" }, icon("puzzle")),
                    h("div", { class: "grow" }, h("b", { text: t.title }), h("div", { class: "tiny muted", text: `${t.versionNumber} · ${t.file}${t.dependency ? " · needed by another " + noun : ""} · added ${fmtDateTime(t.installedAt)}` })),
                    remove));
            }
            for (const f of a.others) list.append(h("div", { class: "mc-addon other" }, h("span", { class: "mc-icon" }, icon("file")),
                h("div", { class: "grow" }, h("b", { text: f }), h("div", { class: "tiny muted", text: "Added by hand — WindowsGSM leaves it alone." }))));
        }

        async function search(q) {
            clear(results).append(loading("Searching…"));
            try {
                const hits = await get(`${base}/addons/search?q=${encodeURIComponent(q)}`);
                clear(results);
                if (!hits.length) { results.append(h("div", { class: "small faint pad", text: `Nothing for this Minecraft version matches.` })); return; }
                for (const hit of hits) {
                    const add = canEdit ? h("button", { class: "btn sm", disabled: hit.installed, onclick: () => busy(add, async () => {
                        const res = await post(`${base}/addons`, { projectId: hit.projectId });
                        toast(`${hit.title} installed`, { type: "good", text: [res.installed.length > 1 ? `With ${res.installed.length - 1} dependenc${res.installed.length === 2 ? "y" : "ies"}.` : "", res.restart ? "Restart the server to load it." : ""].filter(Boolean).join(" ") });
                        hit.installed = true;
                        add.disabled = true;
                        add.lastChild.textContent = "Installed";
                        load();
                    }, `Couldn't install ${hit.title}`) }, icon(hit.installed ? "check" : "plus"), h("span", { text: hit.installed ? "Installed" : "Install" })) : null;
                    results.append(h("div", { class: "mc-addon" },
                        hit.iconUrl ? h("img", { src: hit.iconUrl, alt: "", class: "mc-icon", loading: "lazy", referrerpolicy: "no-referrer" }) : h("span", { class: "mc-icon" }, icon("puzzle")),
                        h("div", { class: "grow" }, h("a", { href: `https://modrinth.com/project/${encodeURIComponent(hit.slug)}`, target: "_blank", rel: "noopener", text: hit.title }),
                            h("div", { class: "tiny muted", text: `${hit.description} · ${Intl.NumberFormat(undefined, { notation: "compact" }).format(hit.downloads)} downloads` })),
                        add));
                }
            } catch (e) { clear(results).append(h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message }))); }
        }

        const updateAll = canEdit && a.tracked.length ? h("button", { class: "btn sm", onclick: () => busy(updateAll, async () => {
            if (!isStopped(server())) { toast("Stop the server first", { type: "warn" }); return; }
            const res = await post(`${base}/addons/update`);
            toast(res.changes.length ? `Updated ${res.changes.length}` : "Everything is up to date", { type: "good", text: res.changes.join(" · ") });
            load();
        }, "Couldn't update") }, icon("update"), "Update all") : null;

        append(clear(addons), [
            h("div", { class: "panel-head wrap" }, icon("puzzle"), h("h2", { text: a.kind === "plugin" ? "Plugins" : "Mods" }),
                h("span", { class: "sub", text: `${a.folder}\\ · from Modrinth, for this Minecraft version` }), h("span", { class: "spacer" }), updateAll),
            h("div", { class: "panel-body stack" }, list,
                canEdit ? h("div", { class: "search" }, icon("search"), box) : null,
                results),
        ]);
        paintList();
        if (canEdit) search("");
    }

    await load();
}
