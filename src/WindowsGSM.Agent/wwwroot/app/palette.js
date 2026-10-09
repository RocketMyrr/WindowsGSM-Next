// The command palette (Ctrl+K / ⌘K): jump to any page, server or machine, or run an action ("restart
// valheim") without hunting for it. Plus the app's other keyboard shortcuts ("?" lists them).

import { h, icon, clear, gameLabel, gameTile } from "./dom.js";
import { store } from "./store.js";
import { navigate, serverPath } from "./router.js";
import { ACTIONS, runAction } from "./actions.js";
import { can, canInstall } from "./perms.js";
import { statusOf, modal } from "./ui.js";
import { applyTheme, currentTheme } from "./theme.js";
import { openReport } from "./report.js";

let open = null;
let openJobsFn = null;
let signOutFn = null;

/** Pages anyone might jump to (filtered by role). */
const PAGES = [
    { label: "Overview", icon: "grid", path: "/", words: "home dashboard servers" },
    { label: "Install a server", icon: "plus", path: "/install", words: "new add game", when: () => canInstall() },
    { label: "Schedules", icon: "calendar", path: "/schedules", words: "cron timer restart backup automatic" },
    { label: "Notifications", icon: "bell", path: "/notifications", words: "alerts discord webhook history" },
    { label: "Machines", icon: "machine", path: "/machines", words: "hub pair join computers", when: () => store.me?.canManageUsers },
    { label: "Discord bot", icon: "message", path: "/discord", words: "discord bot panel slash commands", when: () => store.me?.isOwner },
    { label: "Game plugins", icon: "puzzle", path: "/plugins", words: "plugin community github games add", when: () => store.me?.canManageUsers },
    { label: "Users & access", icon: "users", path: "/users", words: "people permissions accounts", when: () => store.me?.canManageUsers },
    { label: "Logs", icon: "logs", path: "/logs", words: "log app crash discord plugin errors diagnostics", when: () => store.me?.canManageUsers },
    { label: "Activity (audit log)", icon: "audit", path: "/logs?tab=activity", words: "audit history who log sign-in", when: () => store.me?.canManageUsers },
    { label: "Automations", icon: "zap", path: "/automations", words: "automation rule if then empty stop cpu restart crash notify", when: () => store.me?.canManageUsers },
    { label: "Storage and clean-up", icon: "disk", path: "/storage", words: "disk space storage clean cleanup free dumps", when: () => store.me?.canManageUsers },
    { label: "Crash logs", icon: "warn", path: "/logs?tab=crash", words: "crash crashed log error", when: () => store.me?.canManageUsers },
    { label: "Health checks", icon: "checkCircle", path: "/health", words: "readiness diagnostics", when: () => store.me?.canManageUsers },
    { label: "Agent settings", icon: "settings", path: "/settings", words: "https port network certificate startup steam account guard", when: () => store.me?.isOwner },
    { label: "Your account", icon: "shield", path: "/account", words: "password 2fa sessions profile" },
    { label: "Help", icon: "help", path: "/help", words: "help how faq guide question port firewall rollback steam guard minecraft ark" },
];

const SERVER_TABS = [
    ["console", "Console", "console", "Console"], ["logs", "Logs", "logs", "View"], ["files", "Files", "folder", "Files"],
    ["gameconfig", "Game config", "fileCode", "EditConfig"], ["backups", "Backups", "backup", "Backup"], ["settings", "Settings", "sliders", "EditConfig"],
];

/** Everything the palette can do right now, as {label, sub, icon|tile, words, run}. */
function commands() {
    const out = [];
    for (const p of PAGES) {
        if (p.when && !p.when()) continue;
        out.push({ group: "Go to", label: p.label, icon: p.icon, words: p.words, run: () => navigate(p.path) });
    }
    const multi = store.multiMachine;
    for (const s of store.sortedServers()) {
        const where = multi ? ` · ${store.machineName(s.machine)}` : "";
        const tile = () => gameTile(s.game, s.iconUrl, "sm", s.artUrl);
        const words = `${s.name} ${gameLabel(s.game)} #${s.id} ${s.port || ""} ${multi ? store.machineName(s.machine) : ""}`;
        out.push({ group: "Servers", label: s.name, sub: `#${s.id} · ${gameLabel(s.game)} · ${statusOf(s).label}${where}`, tile, words, run: () => navigate(serverPath(s.machine, s.id)) });
        for (const a of ["start", "stop", "restart", "update", "backup"]) {
            const def = ACTIONS[a];
            if (!can(s, def.cap) || !def.when(s)) continue;
            out.push({ group: "Actions", label: `${def.label} ${s.name}`, sub: `#${s.id}${where}`, icon: def.icon, words: `${a} ${words}`, action: true, run: () => runAction(s, a).catch(() => { }) });
        }
        for (const [tab, label, ic, cap] of SERVER_TABS) {
            if (!can(s, cap)) continue;
            out.push({ group: "Open", label: `${s.name}: ${label}`, sub: `#${s.id}${where}`, icon: ic, words: `${label} ${words}`, deep: true, run: () => navigate(serverPath(s.machine, s.id, tab)) });
        }
    }
    if (multi) {
        for (const m of store.machines.values()) {
            out.push({ group: "Machines", label: m.name, sub: m.online === false ? "Offline" : `${m.serverCount} servers`, icon: "machine", words: "machine " + m.name, run: () => navigate("/?machine=" + encodeURIComponent(m.id)) });
        }
    }
    out.push({ group: "App", label: "Activity and jobs", icon: "activity", words: "jobs progress running", run: () => openJobsFn && openJobsFn() });
    out.push({ group: "App", label: currentTheme() === "light" ? "Switch to dark theme" : "Switch to light theme", icon: currentTheme() === "light" ? "moon" : "sun", words: "theme dark light mode", run: () => applyTheme(currentTheme() === "light" ? "dark" : "light") });
    out.push({ group: "App", label: "Report a problem", icon: "link", words: "bug issue report github feedback crash broken", run: () => openReport() });
    out.push({ group: "App", label: "Keyboard shortcuts", icon: "help", words: "keys help shortcuts", run: () => showShortcuts() });
    out.push({ group: "App", label: "Sign out", icon: "logout", words: "logout log out", run: () => signOutFn && signOutFn() });
    return out;
}

/** Lower is better; null = no match. Every word must appear; starts of words count most. */
function score(cmd, words) {
    const label = cmd.label.toLowerCase();
    const hay = (label + " " + (cmd.words || "")).toLowerCase();
    let total = 0;
    for (const w of words) {
        const i = hay.indexOf(w);
        if (i < 0) return null;
        const inLabel = label.indexOf(w);
        const atWordStart = inLabel === 0 || (inLabel > 0 && /[\s#:(-]/.test(label[inLabel - 1]));
        total += inLabel < 0 ? 40 : atWordStart ? 0 : 10;
    }
    return total + (cmd.action ? 3 : 0) + (cmd.deep ? 6 : 0) + label.length / 100;
}

export function openPalette() {
    if (open) { open.input.focus(); return; }
    const all = commands();
    const input = h("input", { class: "palette-input", placeholder: "Jump to a server or page, or type an action…", "aria-label": "Search commands", autocomplete: "off", spellcheck: "false", role: "combobox", "aria-expanded": "true", "aria-controls": "palette-list" });
    const list = h("div", { class: "palette-list", id: "palette-list", role: "listbox" });
    const box = h("div", { class: "palette", role: "dialog", "aria-modal": "true", "aria-label": "Command palette" },
        h("div", { class: "palette-search" }, icon("search"), input, h("kbd", { text: "Esc" })), list,
        h("div", { class: "palette-foot" }, h("span", {}, h("kbd", { text: "↑" }), h("kbd", { text: "↓" }), " to move"), h("span", {}, h("kbd", { text: "Enter" }), " to go"), h("span", { class: "spacer" }), h("span", {}, h("kbd", { text: "?" }), " all shortcuts")));
    const scrim = h("div", { class: "scrim palette-scrim" }, box);
    const previous = document.activeElement;
    let shown = [];
    let active = 0;

    function paint() {
        const q = input.value.trim().toLowerCase();
        const words = q.split(/\s+/).filter(Boolean);
        if (!words.length) {
            // Nothing typed: pages, then servers, then the app itself — actions only once you ask.
            shown = all.filter(c => !c.action && !c.deep && c.group !== "App").concat(all.filter(c => c.group === "App"));
        } else {
            shown = all.map(c => ({ c, s: score(c, words) })).filter(x => x.s != null).sort((a, b) => a.s - b.s).map(x => x.c);
        }
        shown = shown.slice(0, 60);
        active = Math.min(active, Math.max(0, shown.length - 1));
        clear(list);
        if (!shown.length) { list.append(h("div", { class: "palette-empty", text: `Nothing matches “${input.value.trim()}”.` })); return; }
        let group = null;
        shown.forEach((c, i) => {
            if (!words.length && c.group !== group) { group = c.group; list.append(h("div", { class: "palette-group", text: group })); }
            const row = h("button", { class: ["palette-item", i === active && "active"], role: "option", "aria-selected": String(i === active), id: "pal-" + i, tabindex: "-1",
                onmousemove: () => { if (active !== i) { active = i; mark(); } }, onclick: () => choose(c) },
                c.tile ? c.tile() : h("span", { class: "palette-icon" }, icon(c.icon || "chevronRight")),
                h("span", { class: "grow palette-text" }, h("b", { class: "truncate", text: c.label }), c.sub ? h("span", { class: "truncate", text: c.sub }) : null),
                words.length ? h("span", { class: "palette-kind", text: c.group }) : null);
            list.append(row);
        });
        mark();
    }

    function mark() {
        list.querySelectorAll(".palette-item").forEach((el, i) => { el.classList.toggle("active", i === active); el.setAttribute("aria-selected", String(i === active)); });
        const el = list.querySelector("#pal-" + active);
        if (el) { el.scrollIntoView({ block: "nearest" }); input.setAttribute("aria-activedescendant", el.id); }
    }

    function choose(c) { close(); c.run(); }

    function close() {
        if (!open) return;
        scrim.remove();
        document.removeEventListener("keydown", onKey, true);
        open = null;
        if (previous && previous.focus && document.contains(previous)) previous.focus();
    }

    function onKey(e) {
        if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(); return; }
        if (e.key === "ArrowDown") { e.preventDefault(); active = (active + 1) % Math.max(1, shown.length); mark(); }
        else if (e.key === "ArrowUp") { e.preventDefault(); active = (active - 1 + shown.length) % Math.max(1, shown.length); mark(); }
        else if (e.key === "Enter") { e.preventDefault(); if (shown[active]) choose(shown[active]); }
        else if (e.key === "Tab") { e.preventDefault(); input.focus(); }
    }

    input.addEventListener("input", () => { active = 0; paint(); });
    scrim.addEventListener("mousedown", e => { if (e.target === scrim) close(); });
    document.addEventListener("keydown", onKey, true);
    document.body.append(scrim);
    open = { input, close };
    paint();
    input.focus();
}

export function closePalette() { if (open) open.close(); }

const SHORTCUTS = [
    [["Ctrl", "K"], "Search and jump anywhere (also ⌘K)"],
    [["/"], "Same, when you're not typing in a field"],
    [["g", "o"], "Go to the overview"],
    [["g", "n"], "Go to notifications"],
    [["g", "i"], "Install a server"],
    [["g", "m"], "Go to machines"],
    [["a"], "Open activity and jobs"],
    [["?"], "Show this list"],
];

export function showShortcuts() {
    modal({
        title: "Keyboard shortcuts", iconName: "help",
        body: h("div", { class: "shortcut-list" }, ...SHORTCUTS.map(([keys, text]) =>
            h("div", { class: "shortcut" }, h("span", { class: "keys" }, ...keys.flatMap((k, i) => [i ? h("span", { class: "faint", text: keys[0] === "Ctrl" ? "+" : "then" }) : null, h("kbd", { text: k })]).filter(Boolean)), h("span", { text })))),
        footer: close => [h("button", { class: "btn primary", onclick: () => close(true) }, "Got it")],
    });
}

/** Wires the global shortcuts once. openJobs/signOut are passed in to avoid an import cycle with the shell. */
export function installShortcuts({ openJobs, signOut }) {
    openJobsFn = openJobs;
    signOutFn = signOut;
    if (installShortcuts.done) return;
    installShortcuts.done = true;
    let pendingG = 0;
    document.addEventListener("keydown", e => {
        if (!store.loaded) return;
        if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === "k") { e.preventDefault(); openPalette(); return; }
        if (e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
        const t = e.target;
        if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
        if (document.querySelector(".scrim")) return; // a dialog is open
        if (Date.now() - pendingG < 1200) {
            pendingG = 0;
            const to = { o: "/", n: "/notifications", i: "/install", m: "/machines" }[e.key];
            if (to) { e.preventDefault(); navigate(to); }
            return;
        }
        if (e.key === "g") { pendingG = Date.now(); return; }
        if (e.key === "/") { e.preventDefault(); openPalette(); }
        else if (e.key === "?") { e.preventDefault(); showShortcuts(); }
        else if (e.key === "a") { e.preventDefault(); openJobsFn && openJobsFn(); }
    });
}
