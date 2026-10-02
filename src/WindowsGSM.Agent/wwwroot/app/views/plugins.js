// Game plugins: the community-made games installed on a machine, and a catalog of more from GitHub
// (repositories named WindowsGSM.<Game>). Plugins are code, so only owners install, update or remove them.

import { h, icon, clear, append, timeAgo, debounce, gameTile } from "../dom.js";
import { get, post, del, upload } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { navigate } from "../router.js";
import { toast, toastError, busy, confirm, empty, loading, modal, field, input } from "../ui.js";
import { machinePicker } from "./machines.js";

export default async function plugins(host, { query, scope }) {
    setCrumbs({ label: "Game plugins" });
    if (!store.me.canManageUsers) {
        host.append(h("div", { class: "callout warn" }, icon("lock"), h("span", { text: "Only admins and owners can see plugins." })));
        return;
    }
    const owner = !!store.me.isOwner;
    let machine = store.machines.has(query.get("machine")) && store.isOnline(query.get("machine")) ? query.get("machine") : store.localId;
    let installed = [];
    const base = () => `/machines/${encodeURIComponent(machine)}/plugins`;

    const installedBody = h("div", { class: "panel-body flush" });
    const results = h("div", { class: "plugin-grid" });
    const status = h("div", { class: "small muted" });
    const search = h("input", { class: "input", type: "search", placeholder: "Search plugins — e.g. Palworld, Enshrouded, Satisfactory", "aria-label": "Search plugins" });
    search.addEventListener("input", debounce(() => loadCatalog(), 500));
    const picker = machinePicker(machine, m => { machine = m; history.replaceState({}, "", "/plugins?machine=" + encodeURIComponent(m)); loadAll(); }, { onlineOnly: true });

    append(host, [
        h("div", { class: "page-head" },
            h("div", {}, h("h1", { text: "Game plugins" }), h("p", { text: "Add games WindowsGSM doesn't ship with, made by the community." })),
            h("div", { class: "actions" }, picker)),
        h("div", { class: "callout warn" }, icon("shield"), h("span", {},
            h("b", { text: "Plugins run as code on this machine. " }), "Install ones from authors you trust — check the repository, stars and recent activity first.")),
        h("div", { class: "stack loose plugins-page" },
            h("section", { class: "panel" },
                h("div", { class: "panel-head wrap" }, h("h3", { class: "grow", text: "Installed" }),
                    owner ? h("button", { class: "btn sm", onclick: () => addFromLink() }, icon("link"), "Add from a GitHub link") : null,
                    owner ? h("button", { class: "btn sm", onclick: () => addFromFile() }, icon("upload"), "Add your own") : null),
                installedBody),
            h("section", { class: "panel" },
                h("div", { class: "panel-head wrap" }, h("h3", { text: "Find more" }), h("span", { class: "spacer" }), h("div", { class: "search grow plugin-search" }, icon("search"), search)),
                h("div", { class: "panel-body stack" }, status, results)))]);

    // ── Installed ──

    async function loadInstalled() {
        clear(installedBody).append(loading());
        try { installed = await get(base()); }
        catch (e) { clear(installedBody).append(empty("warn", "Couldn't load plugins", e.message)); return; }
        clear(installedBody);
        if (!installed.length) { installedBody.append(h("div", { class: "empty compact" }, icon("puzzle"), h("p", { text: "No plugins yet. Find one below." }))); return; }
        for (const p of installed) installedBody.append(installedRow(p));
    }

    function installedRow(p) {
        const title = p.game.replace(/\s*\[[^\]]*\]\s*$/, "");
        return h("div", { class: ["list-item plugin-row", !p.loaded && "broken"] },
            p.loaded ? gameTile(title, p.hasIcon ? `/api/v2${base()}/${encodeURIComponent(p.file)}/icon` : null)
                : h("span", { class: "plugin-icon" }, icon("warn")),
            h("div", { class: "grow plugin-main" },
                h("div", { class: "row wrap" }, h("b", { class: "truncate", text: p.loaded ? title : p.file }),
                    p.version ? h("span", { class: "tag", text: "v" + String(p.version).replace(/^v/i, "") }) : null,
                    p.loaded ? null : h("span", { class: "tag rose", text: "Didn't load" })),
                h("div", { class: "small muted truncate", text: [p.author ? "by " + p.author : null, p.file, p.servers ? `${p.servers} server${p.servers === 1 ? "" : "s"}` : "not used yet"].filter(Boolean).join(" · ") }),
                p.loaded ? null : h("div", { class: "small rose-text plugin-error", text: p.error || "Unknown error" })),
            p.repo ? h("a", { class: "btn ghost sm icon-only", href: `https://github.com/${p.repo}`, target: "_blank", rel: "noopener", title: "Open on GitHub", "aria-label": `Open ${p.file} on GitHub` }, icon("link")) : null,
            owner && p.hasPrevious ? h("button", { class: ["btn sm", p.loaded ? "ghost icon-only" : ""], title: "Go back to the version you had before the last update", "aria-label": `Previous version of ${p.file}`, onclick: e => previous(p, e.currentTarget) }, icon("restore"), p.loaded ? null : "Previous version") : null,
            owner && p.repo ? h("button", { class: "btn sm", onclick: e => install(p.repo, e.currentTarget, "Updated") }, icon("update"), "Update") : null,
            owner ? h("button", { class: "btn ghost sm icon-only", title: p.servers ? "In use — delete its servers first" : "Remove", "aria-label": `Remove ${p.file}`, disabled: p.servers > 0, onclick: e => remove(p, e.currentTarget) }, icon("trash")) : null);
    }

    async function previous(p, button) {
        if (p.loaded && !(await confirm({ title: `Go back to the previous ${p.file}?`, message: "Puts back the version you had before the last update. You can switch back again the same way.", confirmLabel: "Go back", iconName: "restore" }))) return;
        await busy(button, async () => {
            const r = await post(`${base()}/${encodeURIComponent(p.file)}/previous`);
            store.games.delete(machine);
            if (r.loaded) toast("Previous version restored", { type: "good", text: r.file });
            else toast(`${r.file} still doesn't load`, { type: "bad", text: r.error || "", timeout: 15000 });
            await loadInstalled();
        }, "Couldn't go back");
    }

    async function remove(p, button) {
        if (!(await confirm({ title: `Remove ${p.file}?`, message: "The game disappears from the install list. You can add it again from the catalog.", confirmLabel: "Remove plugin", danger: true, iconName: "trash" }))) return;
        await busy(button, async () => {
            await del(`${base()}/${encodeURIComponent(p.file)}`);
            store.games.delete(machine);
            toast("Plugin removed", { type: "good", text: p.file });
            await loadInstalled();
            paintResults();
        }, "Couldn't remove it");
    }

    async function install(repo, button, verb = "Installed") {
        await busy(button, async () => {
            const p = await post(base(), { repo });
            store.games.delete(machine);
            if (p.loaded) toast(`${verb} ${p.game.replace(/\s*\[[^\]]*\]\s*$/, "")}`, { type: "good", text: "It's in the install list now.", action: { label: "Install a server", onClick: () => navigate(`/install?machine=${encodeURIComponent(machine)}&game=${encodeURIComponent(p.game)}`) } });
            else failed(p);
            await loadInstalled();
            paintResults();
        }, "Couldn't install the plugin");
    }

    // A new version that doesn't compile: say why, and offer the one that worked.
    function failed(p) {
        toast(`${p.file} didn't load`, { type: "bad", text: p.error || "The plugin has errors — check with its author.", timeout: 20000,
            action: p.hasPrevious ? { label: "Go back to the previous version", onClick: () => previous({ ...p, loaded: false }, null) } : null });
    }

    // ── Adding one by hand ──

    function addFromFile() {
        const file = h("input", { type: "file", class: "input", accept: ".cs,.zip" });
        const logo = h("input", { type: "file", class: "input", accept: ".png,image/png" });
        const progress = h("div", { class: "small muted" });
        modal({
            title: "Add your own plugin", iconName: "upload",
            subtitle: "A plugin you wrote or got from somewhere other than GitHub search.",
            body: h("div", { class: "stack" },
                field("Plugin", file, { help: "A community plugin's .cs file, or the .zip of its repository from GitHub (Code → Download ZIP). Plugins run on this machine with WindowsGSM's rights, so only add ones from people you trust.", hint: "Its .cs file (e.g. Palworld.cs), or a .zip of the plugin folder or repository." }),
                field("Logo (optional)", logo, { hint: "A PNG shown on its game tiles. A .zip that has one already brings it." }),
                h("div", { class: "callout warn" }, icon("shield"), h("span", { text: "It runs as code on this machine — only add plugins you trust." })),
                progress),
            footer: close => {
                const go = h("button", { class: "btn primary", onclick: () => busy(go, async () => {
                    const f = file.files[0];
                    if (!f) throw new Error("Choose the plugin's .cs file or a .zip first.");
                    const form = new FormData();
                    form.append("file", f, f.name);
                    if (logo.files[0]) form.append("logo", logo.files[0], logo.files[0].name);
                    const p = await upload(`${base()}/upload`, form, x => { progress.textContent = `Uploading… ${Math.round(x * 100)}%`; });
                    close(true);
                    added(p, "Added");
                }, "Couldn't add the plugin") }, icon("upload"), "Add plugin");
                return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), go];
            },
        });
    }

    function addFromLink() {
        const link = input({ placeholder: "https://github.com/owner/WindowsGSM.Game", autocomplete: "off" });
        modal({
            title: "Add from a GitHub link", iconName: "link",
            subtitle: "For plugins the search doesn't find — forks, or repositories named differently.",
            body: h("div", { class: "stack" },
                field("Repository", link, { help: "A GitHub repository that holds a WindowsGSM plugin — e.g. https://github.com/someone/WindowsGSM.Valheim. WindowsGSM downloads its latest release and can update it later.", hint: "Its github.com link, or owner/name." }),
                h("div", { class: "callout warn" }, icon("shield"), h("span", { text: "It runs as code on this machine — check the repository first." }))),
            footer: close => {
                const go = h("button", { class: "btn primary", onclick: () => busy(go, async () => {
                    if (!link.value.trim()) throw new Error("Paste the repository's link.");
                    const p = await post(base(), { repo: link.value.trim() });
                    close(true);
                    added(p, "Installed");
                }, "Couldn't install the plugin") }, icon("download"), "Install");
                return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), go];
            },
        });
        setTimeout(() => link.focus(), 0);
    }

    async function added(p, verb) {
        store.games.delete(machine);
        if (p.loaded) toast(`${verb} ${p.game.replace(/\s*\[[^\]]*\]\s*$/, "")}`, { type: "good", text: "It's in the install list now.", action: { label: "Install a server", onClick: () => navigate(`/install?machine=${encodeURIComponent(machine)}&game=${encodeURIComponent(p.game)}`) } });
        else failed(p);
        await loadInstalled();
        paintResults();
    }

    // ── Catalog ──

    let catalog = [];
    async function loadCatalog() {
        const q = search.value.trim();
        status.textContent = q ? `Searching GitHub for “${q}”…` : "Loading popular plugins from GitHub…";
        clear(results);
        try { catalog = await get(`${base()}/catalog?q=${encodeURIComponent(q)}`); }
        catch (e) { status.textContent = e.message; return; }
        if (q !== search.value.trim()) return;
        status.textContent = catalog.length ? (q ? `${catalog.length} found` : "Most starred on GitHub") : `Nothing found for “${q}”.`;
        paintResults();
    }

    function paintResults() {
        clear(results);
        const installedRepos = new Map(installed.filter(p => p.repo).map(p => [p.repo.toLowerCase(), p]));
        const installedFiles = new Set(installed.map(p => p.file.toLowerCase()));
        for (const c of catalog) {
            const here = installedRepos.get(c.repo.toLowerCase());
            const sameGame = !here && installedFiles.has((c.game + ".cs").toLowerCase());
            results.append(h("article", { class: "plugin-card" },
                h("div", { class: "row" }, gameTile(c.game, `/api/v2${base()}/catalog/icon?repo=${encodeURIComponent(c.repo)}&game=${encodeURIComponent(c.game)}${c.branch ? "&branch=" + encodeURIComponent(c.branch) : ""}`),
                    h("div", { class: "grow plugin-main" }, h("b", { class: "truncate", text: c.game }), h("span", { class: "small muted truncate", text: `by ${c.owner}` }))),
                h("p", { class: "small plugin-desc", text: c.description || "No description." }),
                h("div", { class: "row small muted plugin-meta" },
                    h("span", { title: "GitHub stars" }, icon("heart"), " " + c.stars),
                    c.updatedAt ? h("span", { text: "updated " + timeAgo(c.updatedAt) }) : null,
                    h("span", { class: "spacer" }),
                    h("a", { href: c.url, target: "_blank", rel: "noopener", class: "btn ghost sm" }, "Details", icon("link"))),
                here ? h("div", { class: "row" }, h("span", { class: "tag green" }, icon("check"), "Installed"), h("span", { class: "spacer" }),
                        owner ? h("button", { class: "btn sm", onclick: e => install(c.repo, e.currentTarget, "Updated") }, icon("update"), "Update") : null)
                    : owner ? h("button", { class: ["btn sm", sameGame ? "" : "primary"], title: sameGame ? `Replaces the ${c.game}.cs you have` : "", onclick: e => install(c.repo, e.currentTarget) }, icon("download"), sameGame ? "Install (replace)" : "Install")
                        : null));
        }
    }

    async function loadAll() { await loadInstalled(); await loadCatalog(); }
    await loadAll();
}
