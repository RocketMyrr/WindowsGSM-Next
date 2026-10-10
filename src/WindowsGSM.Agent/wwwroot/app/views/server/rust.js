// Plugins tab (Rust): uMod plugins for Oxide or Carbon — what's installed, search and install, update all,
// keep hand-added ones up to date, remove. Oxide/Carbon load and unload plugin files by themselves, so none of
// this needs the server stopped.

import { h, icon, clear, append, debounce, fmtDateTime } from "../../dom.js";
import { get, post, put, del, srv } from "../../api.js";
import { busy, toast, toastError, confirm, loading, toggle } from "../../ui.js";
import { can } from "../../perms.js";

export default async function rustTab(host, { id, machine, server, scope }) {
    const base = srv(machine, id, "/rust");
    const panel = h("section", { class: "panel" });
    const finder = h("section", { class: "panel" });
    host.append(h("div", { class: "stack" }, panel, finder));

    let state;
    let updates = [];
    async function load() {
        try { state = await get(base); }
        catch (e) { clear(panel).append(h("div", { class: "panel-body" }, h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message })))); return; }
        paint();
        checkUpdates();
    }

    // Asks uMod for each kept-up-to-date plugin: in the background, so the tab shows straight away.
    async function checkUpdates() {
        if (!state.framework || !state.tracked.length) { updates = []; return; }
        try { updates = await get(`${base}/plugins/updates`); } catch { updates = []; }
        paint();
    }

    const canEdit = () => can(server(), "Addons");

    function paint() {
        if (!state.framework) {
            append(clear(panel), [
                h("div", { class: "panel-head" }, icon("puzzle"), h("h2", { text: "Plugins" })),
                h("div", { class: "panel-body" }, h("div", { class: "callout info" }, icon("info"), h("span", {},
                    "Rust loads plugins through Oxide (uMod) or Carbon. Install one in the ",
                    h("a", { href: `/machines/${machine}/servers/${id}/addons`, text: "Add-ons tab" }), ", then come back here.")))]);
            clear(finder);
            return;
        }
        const list = h("div", { class: "mc-addons" });
        if (!state.plugins.length) { list.append(h("div", { class: "small faint pad", text: "No plugins yet. Search below to add some." })); }
        for (const p of state.plugins) {
            const t = state.tracked.find(x => x.name.toLowerCase() === p.name.toLowerCase());
            const newer = t && updates.includes(t.name);
            const actions = [];
            if (canEdit() && t) {
                actions.push(h("button", { class: "btn ghost sm", title: "Stop keeping it up to date (the file stays)", onclick: async e => {
                    await busy(e.currentTarget, async () => { await del(`${base}/plugins/${encodeURIComponent(p.name)}/track`); load(); }, "Couldn't change it");
                } }, "Stop updating"));
                actions.push(h("button", { class: "btn ghost sm icon-only", title: "Remove", "aria-label": `Remove ${p.title || p.name}`, onclick: async () => {
                    if (!(await confirm({ title: `Remove ${p.title || p.name}?`, message: `Deletes ${state.folder}\\${p.name}.cs — ${state.framework} unloads it straight away. Its config and data files stay.`, confirmLabel: "Remove", danger: true, iconName: "trash" }))) return;
                    try { await del(`${base}/plugins/${encodeURIComponent(p.name)}`); toast(`${p.title || p.name} removed`, { type: "good" }); load(); } catch (e) { toastError(e, "Couldn't remove it"); }
                } }, icon("trash")));
            } else if (canEdit()) {
                actions.push(h("button", { class: "btn sm", title: "If it's the uMod plugin of this name, keep it up to date from uMod from now on", onclick: async e => {
                    await busy(e.currentTarget, async () => {
                        await post(`${base}/plugins/${encodeURIComponent(p.name)}/track`);
                        toast(`${p.title || p.name} will be kept up to date`, { type: "good", text: "“Update all” brings it to uMod's newest version." });
                        load();
                    }, "Couldn't do that");
                } }, icon("refresh"), "Keep up to date"));
            }
            const detail = [p.version ? `v${p.version}` : null, p.author ? `by ${p.author}` : null,
                t ? (p.edited ? "changed by hand — updates leave it alone" : `from uMod · kept up to date${t.dependency ? " · needed by another plugin" : ""}`) : "added by hand — left alone",
                t ? `added ${fmtDateTime(t.installedAt)}` : null].filter(Boolean).join(" · ");
            list.append(h("div", { class: ["mc-addon", !t && "other"] },
                t?.iconUrl ? h("img", { src: t.iconUrl, alt: "", class: "mc-icon", loading: "lazy", referrerpolicy: "no-referrer" }) : h("span", { class: "mc-icon" }, icon(t ? "puzzle" : "file")),
                h("div", { class: "grow" }, t?.url ? h("a", { href: t.url, target: "_blank", rel: "noopener", text: p.title || p.name }) : h("b", { text: p.title || p.name }),
                    newer ? h("span", { class: "tag violet", text: "update available" }) : null,
                    h("div", { class: "tiny muted", text: detail })),
                ...actions));
        }

        const updateAll = canEdit() && state.tracked.length ? h("button", { class: "btn sm", onclick: () => busy(updateAll, async () => {
            const res = await post(`${base}/plugins/update`);
            toast(res.changes.length ? `Updated ${res.changes.length}` : "Everything is up to date", { type: res.skipped.length ? "warn" : "good", text: [...res.changes, ...res.skipped].join(" · ") });
            load();
        }, "Couldn't update") }, icon("update"), updates.length ? `Update all (${updates.length})` : "Update all") : null;

        const beforeStart = toggle("Update them before every start", state.updateBeforeStart, {
            disabled: !canEdit(),
            hint: "Brings the plugins WindowsGSM keeps up to date to uMod's newest before each start and restart.",
            onChange: async on => {
                try { await put(`${base}/update-before-start`, { on }); toast(on ? "Plugins update before every start" : "Plugins no longer update before starting", { type: "good", timeout: 2500 }); }
                catch (e) { toastError(e, "Couldn't save that"); }
            },
        });

        append(clear(panel), [
            h("div", { class: "panel-head wrap" }, icon("puzzle"), h("h2", { text: "Plugins" }),
                h("span", { class: "sub", text: `${state.framework} · ${state.folder}\\ · no restart needed: ${state.framework} loads changes itself` }),
                h("span", { class: "spacer" }), updateAll),
            h("div", { class: "panel-body stack" }, list, state.tracked.length ? beforeStart : null,
                h("p", { class: "tiny faint", text: "Installs and updates come from umod.org, checked against uMod's checksum. Plugins you added by hand are never changed unless you choose “Keep up to date”; one you've edited since is left alone by updates." })),
        ]);
        paintFinder();
    }

    // ── Search uMod ──
    let finderBuilt = false;
    function paintFinder() {
        if (!canEdit()) { clear(finder); return; }
        if (finderBuilt) return;
        finderBuilt = true;
        const results = h("div", { class: "mc-results" });
        const box = h("input", { class: "input", type: "search", placeholder: "Search uMod for Rust plugins…", "aria-label": "Search uMod plugins" });
        box.addEventListener("input", debounce(() => search(box.value.trim()), 350));

        async function search(q) {
            clear(results).append(loading("Searching…"));
            try {
                const hits = await get(`${base}/plugins/search?q=${encodeURIComponent(q)}`);
                clear(results);
                if (!hits.length) { results.append(h("div", { class: "small faint pad", text: "No Rust plugins on uMod match." })); return; }
                for (const hit of hits) {
                    const add = h("button", { class: "btn sm", disabled: hit.installed, onclick: () => busy(add, async () => {
                        const res = await post(`${base}/plugins`, { name: hit.name });
                        const extra = res.installed.length - 1;
                        toast(`${hit.title} installed`, { type: "good", text: `${extra > 0 ? `With ${extra} plugin${extra === 1 ? "" : "s"} it needs. ` : ""}${state.framework} loads it straight away.` });
                        hit.installed = true;
                        add.disabled = true;
                        add.lastChild.textContent = "Installed";
                        load();
                    }, `Couldn't install ${hit.title}`) }, icon(hit.installed ? "check" : "plus"), h("span", { text: hit.installed ? "Installed" : "Install" }));
                    results.append(h("div", { class: "mc-addon" },
                        hit.iconUrl ? h("img", { src: hit.iconUrl, alt: "", class: "mc-icon", loading: "lazy", referrerpolicy: "no-referrer" }) : h("span", { class: "mc-icon" }, icon("puzzle")),
                        h("div", { class: "grow" }, hit.url ? h("a", { href: hit.url, target: "_blank", rel: "noopener", text: hit.title }) : h("b", { text: hit.title }),
                            h("div", { class: "tiny muted", text: `${hit.description} · v${hit.version} by ${hit.author} · ${Intl.NumberFormat(undefined, { notation: "compact" }).format(hit.downloads)} downloads` })),
                        add));
                }
            } catch (e) { clear(results).append(h("div", { class: "callout bad" }, icon("warn"), h("span", { text: e.message }))); }
        }

        append(clear(finder), [
            h("div", { class: "panel-head" }, icon("search"), h("h2", { text: "Add plugins from uMod" }), h("span", { class: "sub", text: "Most downloaded first" })),
            h("div", { class: "panel-body stack" }, h("div", { class: "search" }, icon("search"), box), results),
        ]);
        search("");
    }

    await load();
}
