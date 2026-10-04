// One server: a live header (status, address, players, actions, the job in progress) and tabs.

import { h, icon, clear, append, gameLabel, gameTile, gameBanner, copyText, fmtDuration } from "../dom.js";
import { tip } from "../tips.js";
import { moveFilesDialog } from "../places.js";
import { store, key as keyOf } from "../store.js";
import { setCrumbs } from "../shell.js";
import { navigate, serverPath, render, hasLeaveGuard } from "../router.js";
import { statusPill, showMenu, toast, tabs, progressBar, setProgress, isRunning, isStopped, empty, loading } from "../ui.js";
import { ACTIONS, runAction, deleteServer, editTags, cloneServer, moveServer, rollbackServer, saveTemplate, applyTemplate } from "../actions.js";
import { can, isAdmin } from "../perms.js";
import { Scope } from "../router.js";

const TABS = [
    { id: "overview", label: "Overview", icon: "grid", cap: "View", load: () => import("./server/overview.js") },
    { id: "console", label: "Console", icon: "console", cap: "Console", load: () => import("./server/console.js") },
    { id: "logs", label: "Logs", icon: "logs", cap: "View", load: () => import("./server/logs.js") },
    { id: "players", label: "Players", icon: "players", cap: "View", load: () => import("./server/players.js") },
    { id: "gameconfig", label: "Game config", icon: "fileCode", cap: "EditConfig", load: () => import("./server/gameconfig.js") },
    { id: "files", label: "Files", icon: "folder", cap: "Files", load: () => import("./server/files.js") },
    { id: "backups", label: "Backups", icon: "backup", cap: "Backup", load: () => import("./server/backups.js") },
    { id: "addons", label: "Add-ons", icon: "puzzle", cap: "Addons", load: () => import("./server/addons.js") },
    { id: "workshop", label: "Workshop", icon: "steam", cap: "Addons", steamOnly: true, load: () => import("./server/workshop.js") },
    { id: "ark", label: "Mods & cluster", icon: "puzzle", cap: "View", arkOnly: true, load: () => import("./server/ark.js") },
    { id: "minecraft", label: "Minecraft", icon: "box", cap: "View", gameOnly: "Minecraft: Java Edition Server", load: () => import("./server/minecraft.js") },
    { id: "rust", label: "Plugins", icon: "puzzle", cap: "View", gameOnly: "Rust Dedicated Server", load: () => import("./server/rust.js") },
    { id: "schedules", label: "Schedules", icon: "calendar", cap: "View", load: () => import("./server/schedules.js") },
    { id: "settings", label: "Settings", icon: "sliders", cap: "EditConfig", load: () => import("./server/settings.js") },
];

export default async function serverPage(host, { params, scope }) {
    const id = params.id;
    // "local" (old links, bookmarks) means the machine serving this page.
    const machine = !params.machine || params.machine === "local" ? store.localId : params.machine;
    const key = keyOf(machine, id);
    // Overview › (the machine, when there are several) › this server
    const crumbs = last => [{ label: "Overview", href: "/" },
        ...(store.multiMachine ? [{ label: store.machineName(machine), href: "/?machine=" + encodeURIComponent(machine) }] : []), { label: last }];
    let server = store.server(machine, id);
    if (!server && store.machines.has(machine)) {
        try { await store.refreshServer(machine, id); } catch { /* handled below */ }
        server = store.server(machine, id);
    }
    if (!server) {
        setCrumbs(...crumbs("Server not found"));
        host.append(empty("servers", "Server not found", "It may have been deleted, or you don't have access to it.", h("a", { class: "btn primary", href: "/" }, "Back to overview")));
        return;
    }

    const offline = h("div", { class: "callout warn server-offline", hidden: true });
    // An offline machine can only show what the hub remembers: the overview.
    // Steam-only tabs (Workshop) for Steam games; the game list is cached, so this is quick after the first time.
    let steam = true;
    let ark = /ARK/i.test(server.game);
    if (store.isOnline(machine)) {
        try {
            const g = (await store.loadGames(machine)).find(x => x.name === server.game);
            steam = !!g?.isSteam;
            ark = ["2430930", "376030"].includes(g?.appId) || /Ascended/i.test(server.game);
        } catch { /* show it */ }
    }
    const visibleTabs = TABS.filter(t => can(server, t.cap) && (store.isOnline(machine) || t.id === "overview") && (!t.steamOnly || steam) && (!t.gameOnly || server.game === t.gameOnly) && (!t.arkOnly || ark));
    let active = visibleTabs.some(t => t.id === params.tab) ? params.tab : "overview";

    // ── Header ──
    const hero = h("section", { class: "server-hero" });
    const jobStrip = h("div", { class: "job-strip", hidden: true });
    const tabBar = tabs(visibleTabs, active, tid => {
        if (tid === active) return;
        navigate(serverPath(machine, id, tid === "overview" ? "" : tid));
    });
    const tabHost = h("div", { class: "tab-host", role: "tabpanel" });
    const tabWrap = h("div", { class: "server-tabs" }, tabBar);
    const tabTipSlot = h("div", { class: "tip-slot" }); // the tab's tip, outside the tab so its redraws keep it
    host.append(hero, offline, jobStrip, tabWrap, tabTipSlot, tabHost);
    // More tabs than fit: fade the edge that has more, so it's clear the row scrolls.
    const edges = () => {
        tabWrap.classList.toggle("more-right", tabBar.scrollLeft + tabBar.clientWidth < tabBar.scrollWidth - 2);
        tabBar.classList.toggle("more-left", tabBar.scrollLeft > 2);
        tabWrap.classList.toggle("more-left", tabBar.scrollLeft > 2);
    };
    tabBar.addEventListener("scroll", edges, { passive: true });
    scope.listen(window, "resize", edges);
    requestAnimationFrame(() => { tabBar.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest", inline: "nearest" }); edges(); });

    function paintHero() {
        const s = store.server(machine, id);
        if (!s) return;
        server = s;
        setCrumbs(...crumbs(s.name));
        const address = [s.ip, s.port].filter(Boolean).join(":");
        offline.hidden = store.isOnline(machine);
        if (!offline.hidden) clear(offline).append(icon("warn"), h("span", {}, h("b", { text: `${store.machineName(machine)} is offline.` }), " This is the last state it reported; actions come back when it reconnects."));
        const uptime = isRunning(s) && s.startedAt ? fmtDuration((Date.now() - Date.parse(s.startedAt)) / 1000) : null;
        append(clear(hero), [
            gameBanner(s.bannerUrl, "server-hero-banner"),
            h("div", { class: "server-hero-bg" }),
            gameTile(s.game, s.iconUrl, "lg", s.artUrl),
            h("div", { class: "server-hero-main grow" },
                h("div", { class: "row wrap" }, h("h1", { class: "truncate", text: s.name }), statusPill(s)),
                h("div", { class: "server-hero-meta" },
                    h("span", { text: `#${s.id} · ${gameLabel(s.game)}` }),
                    address ? h("button", { class: "address", title: "Copy address", onclick: async () => { if (await copyText(address)) toast("Address copied", { type: "good", text: address, timeout: 2500 }); } }, icon("copy"), address) : null,
                    s.queryPort ? h("span", { class: "faint", text: `query ${s.queryPort}` }) : null,
                    uptime ? h("span", {}, icon("clock"), ` up ${uptime}`) : null,
                    isRunning(s) && s.players != null ? h("span", {}, icon("players"), ` ${s.players}${s.maxPlayers ? " / " + s.maxPlayers : ""} online`) : null,
                    s.crashLoopSuspended ? h("span", { class: "tag rose" }, icon("warn"), "Auto-restart paused after repeated crashes") : null,
                    s.updatesHeld ? h("span", { class: "tag amber", title: "Rolled back to an earlier build — auto-update and update on start are paused. More → Roll back game update to resume." }, icon("clock"), "Updates on hold") : null,
                    s.updateAvailable ? h("span", { class: "tag accent", title: isStopped(s) ? "Use Update to install it." : "Stop the server to update, or let auto-update do it." }, icon("update"), "Update available") : null,
                    ...(s.tags || []).map(t => h("a", { class: "tag chip", href: "/?tag=" + encodeURIComponent(t), title: "Show servers tagged " + t, text: t })))),
            h("div", { class: "server-hero-actions" }, ...heroActions(s))]);
    }

    function heroActions(s) {
        const out = [];
        const act = (a, kind = "") => h("button", { class: ["btn", kind], onclick: async e => {
            const b = e.currentTarget;
            b.classList.add("busy");
            try { await runAction(s, a); } catch { /* toasted */ } finally { b.classList.remove("busy"); }
        } }, icon(ACTIONS[a].icon), ACTIONS[a].label);
        if (isStopped(s) && can(s, "Start")) out.push(act("start", "primary"));
        if (isRunning(s) && can(s, "Stop")) out.push(act("stop"));
        if (isRunning(s) && can(s, "Restart")) out.push(act("restart"));
        if (isStopped(s) && can(s, "Update")) out.push(act("update"));
        out.push(h("button", { class: "btn icon-only", "aria-label": "More actions", "aria-haspopup": "menu", onclick: e => showMenu(e.currentTarget, [
            ...["backup", "validate", "update", "restart", "kill"].map(a => ({
                label: ACTIONS[a].label, icon: ACTIONS[a].icon, danger: ACTIONS[a].danger,
                hidden: !can(s, ACTIONS[a].cap), disabled: !ACTIONS[a].when(s),
                onClick: () => runAction(s, a).catch(() => { }),
            })),
            "-",
            { label: "Roll back game update…", icon: "restore", hidden: !can(s, "Update") || !steam, disabled: !isStopped(s), onClick: () => rollbackServer(s) },
            { label: "Tags…", icon: "filter", hidden: !can(s, "EditConfig"), onClick: () => editTags(s) },
            { label: "Save as template…", icon: "save", hidden: !can(s, "EditConfig"), onClick: () => saveTemplate(s) },
            { label: "Apply a template…", icon: "sliders", hidden: !can(s, "EditConfig"), disabled: !isStopped(s), onClick: () => applyTemplate(s) },
            { label: "Move files to another drive…", icon: "disk", hidden: !isAdmin(), disabled: !isStopped(s), onClick: () => moveFilesDialog(s) },
            { label: "Copy server…", icon: "copy", hidden: !can(s, "Files"), disabled: !isStopped(s), onClick: () => cloneServer(s) },
            { label: "Move to another machine…", icon: "machine", hidden: !store.multiMachine || !can(s, "Files"), disabled: !isStopped(s), onClick: () => moveServer(s) },
            { label: "Copy address", icon: "copy", hidden: !s.port, onClick: () => copyText([s.ip, s.port].filter(Boolean).join(":")).then(() => toast("Address copied", { type: "good", timeout: 2500 })) },
            { label: "Delete server…", icon: "trash", danger: true, hidden: !can(s, "Delete"), disabled: !isStopped(s), onClick: async () => { if (await deleteServer(s)) navigate("/"); } },
        ]) }, icon("more")));
        return out;
    }

    function paintJob() {
        const job = [...store.jobs.values()].filter(j => j.machine === machine && j.serverId === id && j.status === "Running").sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt))[0];
        jobStrip.hidden = !job;
        if (!job) return;
        clear(jobStrip).append(
            h("div", { class: "row" }, h("span", { class: "spinner" }), h("b", { text: job.title }), h("span", { class: "muted small grow truncate", text: job.stage || "" }),
                h("span", { class: "num small", text: job.percent != null ? job.percent + "%" : "" })),
            progressBar(job.percent));
    }

    scope.add(store.on("server:" + key, s => { if (!s) { toast("This server was deleted", { type: "warn" }); navigate("/"); return; } paintHero(); }));
    scope.add(store.on("jobs", paintJob));
    // The machine dropped off or came back: rebuild, since which tabs and actions work depends on it
    // (unless there are unsaved edits on screen — then just the banner changes).
    const wasOnline = store.isOnline(machine);
    scope.add(store.on("machines", () => { if (store.isOnline(machine) !== wasOnline && !hasLeaveGuard()) render(); else paintHero(); }));
    scope.every(30000, paintHero, { now: false }); // keeps "up 2h 5m" honest
    paintHero();
    paintJob();

    // ── Tab content (its own scope, so switching tabs cleans up that tab's live subscriptions) ──
    const tabScope = new Scope();
    scope.add(() => tabScope.dispose());
    const def = visibleTabs.find(t => t.id === active);
    tabHost.append(loading());
    const mod = await def.load();
    if (!scope.alive) return;
    clear(tabHost);
    await mod.default(tabHost, { id, machine, key, server: () => store.server(machine, id), scope: tabScope });
    if (scope.alive) tabTipSlot.replaceChildren(...[tip("server:" + def.id)].filter(Boolean));
}
