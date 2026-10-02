// The signed-in frame: sidebar navigation, top bar (breadcrumbs, connection, jobs), the jobs drawer, and
// app-wide notifications (job results, crash alerts, plugin questions waiting for an answer).

import { h, icon, clear, gameLabel, timeAgo } from "./dom.js";
import { post } from "./api.js";
import * as live from "./live.js";
import { store, key } from "./store.js";
import { navigate, serverPath } from "./router.js";
import { toast, toastError, showMenu, progressBar, setProgress, busy } from "./ui.js";
import { signOut } from "./session.js";
import { applyTheme, currentTheme } from "./theme.js";
import { notificationItem, popDesktop } from "./notify.js";
import { openPalette, installShortcuts } from "./palette.js";
import { helpTopicFor } from "./tips.js";

let frame = null; // { app, content, crumbs, nav, jobsBadge, conn, banner }
let drawer = null;

// The desktop app on the server stays signed in while it's open (browsers and phones keep the normal timeout):
// a light request now and then counts as activity.
let keepAlive = null;

export function mountShell() {
    if (window.chrome?.webview && !keepAlive) keepAlive = setInterval(() => { fetch("/api/v2/auth/me", { credentials: "same-origin" }).catch(() => { }); }, 10 * 60 * 1000);
    watchTitle();
    if (frame) return frame;
    const root = document.getElementById("app");
    clear(root);

    const nav = h("nav", { class: "nav", "aria-label": "Main" });
    const adminNav = h("nav", { class: "nav", "aria-label": "Administration" });
    const machineName = h("b", { class: "truncate" });
    const machineStatus = h("small");
    const meName = h("b", { class: "truncate" });
    const meRole = h("small");
    const avatar = h("span", { class: "avatar" });

    const sidebar = h("aside", { class: "sidebar" },
        h("a", { class: "brand", href: "/" },
            h("span", { class: "brand-mark" }, h("img", { src: "/img/logo-96.png", alt: "", width: 38, height: 38 })),
            h("span", {}, h("div", { class: "brand-name" }, "Windows", h("span", { text: "GSM" })), h("div", { class: "brand-sub", text: "Game server control" }))),
        h("button", { class: "machine-switch", title: "Machines", onclick: e => showMenu(e.currentTarget, machineMenu(), { align: "start" }) },
            h("span", { class: "machine-icon" }, icon("machine")),
            h("span", { class: "grow truncate" }, machineName, machineStatus),
            icon("chevronDown")),
        nav,
        h("div", { class: "nav-section upper", text: "Manage", id: "admin-heading" }),
        adminNav,
        h("div", { class: "sidebar-foot" },
            h("button", { class: "me-card", onclick: e => showMenu(e.currentTarget, [
                { label: "Account & security", icon: "shield", onClick: () => navigate("/account") },
                { label: currentTheme() === "light" ? "Dark theme" : "Light theme", icon: currentTheme() === "light" ? "moon" : "sun", onClick: () => applyTheme(currentTheme() === "light" ? "dark" : "light") },
                "-",
                { label: "Sign out", icon: "logout", onClick: () => signOut() },
            ], { align: "start" }) }, avatar, h("span", { class: "grow truncate" }, meName, meRole), icon("more"))),
    );

    const crumbs = h("div", { class: "crumbs grow" });
    const connText = h("span", { class: "conn-text", text: "Connecting" });
    const conn = h("span", { class: "conn connecting", title: "Live connection to the agent" }, h("span", { class: "dot" }), connText);
    const jobsBadge = h("span", { class: "count-badge", hidden: true });
    const jobsButton = h("button", { class: "btn ghost icon-only jobs-button", title: "Activity", "aria-label": "Activity and jobs", onclick: () => openJobs() }, icon("activity"), jobsBadge);
    const bellBadge = h("span", { class: "count-badge", hidden: true });
    const bellButton = h("button", { class: "btn ghost icon-only bell-button", title: "Notifications", "aria-label": "Notifications", "aria-haspopup": "dialog", "aria-expanded": "false",
        onclick: () => toggleBell(bellButton) }, icon("bell"), bellBadge);
    const isMac = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
    const search = h("button", { class: "palette-trigger", "aria-label": "Search or jump to (Ctrl+K)", onclick: () => openPalette() },
        icon("search"), h("span", { class: "grow", text: "Search or jump to…" }), h("kbd", { text: isMac ? "⌘K" : "Ctrl K" }));
    const menuToggle = h("button", { class: "btn ghost icon-only menu-toggle", "aria-label": "Open navigation", onclick: () => app.classList.toggle("nav-open") }, icon("menu"));
    const banner = h("div", { class: "offline-banner", hidden: true }, icon("warn"), h("span", { text: "Lost the live connection to the agent — reconnecting. What you see may be out of date." }));
    // The page's tip sits in its own slot above the page, so pages that redraw themselves never wipe it.
    const tipSlot = h("div", { class: "tip-slot" });
    const pageBody = h("div", { class: "page-body", tabindex: "-1" });
    const content = h("main", { class: "content", id: "content" }, tipSlot, pageBody);

    const app = h("div", { class: "app" },
        sidebar,
        h("div", { class: "main" },
            h("header", { class: "topbar" }, menuToggle, crumbs, search, conn,
                h("a", { class: "btn primary sm", href: "/install" }, icon("plus"), h("span", { text: "New server" })),
                helpTopButton(),
                bellButton,
                jobsButton),
            banner,
            content));
    root.append(app);

    // Close the mobile nav after choosing somewhere to go.
    app.addEventListener("click", e => { if (e.target.closest(".sidebar a") || (app.classList.contains("nav-open") && e.target === app)) app.classList.remove("nav-open"); });
    document.addEventListener("click", e => { if (app.classList.contains("nav-open") && !e.target.closest(".sidebar") && !e.target.closest(".menu-toggle")) app.classList.remove("nav-open"); });

    live.onState(s => {
        conn.className = "conn " + s;
        connText.textContent = s === "live" ? "Live" : s === "connecting" ? "Connecting" : "Offline";
        banner.hidden = s !== "offline";
    });

    const paintMe = () => {
        const me = store.me;
        if (!me) return;
        meName.textContent = me.username;
        meRole.textContent = me.role;
        avatar.textContent = me.username.slice(0, 1);
        const machines = [...store.machines.values()];
        const offline = machines.filter(m => m.online === false).length;
        const servers = `${store.servers.size} server${store.servers.size === 1 ? "" : "s"}`;
        machineName.textContent = store.multiMachine ? `${machines.length} machines` : store.machine?.name || "This machine";
        clear(machineStatus).append(h("span", { class: ["dot", offline ? "st-transition" : "st-running"] }),
            store.multiMachine ? `${offline ? offline + " offline · " : ""}${servers}` : servers);
        paintNav();
    };
    store.on("me", paintMe);
    store.on("servers", paintMe);
    store.on("machine", paintMe);
    store.on("machines", paintMe);

    const paintJobs = () => {
        const n = store.runningJobs().length + store.prompts.size;
        jobsBadge.hidden = n === 0;
        jobsBadge.textContent = n;
        jobsBadge.classList.toggle("accent", store.prompts.size === 0);
    };
    store.on("jobs", paintJobs);
    store.on("prompts", paintJobs);

    const paintBell = () => {
        bellBadge.hidden = store.unread === 0;
        bellBadge.textContent = store.unread > 99 ? "99+" : store.unread;
        bellButton.setAttribute("aria-label", store.unread ? `Notifications, ${store.unread} unread` : "Notifications");
        if (bell) paintBellPanel();
    };
    store.on("notifications", paintBell);

    wireNotifications();
    installShortcuts({ openJobs, signOut });
    frame = { app, content: pageBody, tipSlot, crumbs, nav, adminNav };
    paintMe();
    paintJobs();
    paintBell();
    return frame;
}

export function unmountShell() {
    frame = null;
    closeJobs();
    closeBell();
}

// ─────────────────────────── Notifications (the bell) ───────────────────────────

let bell = null; // { el, anchor, unreadAtOpen, cleanup }

function toggleBell(anchor) {
    if (bell) { closeBell(); return; }
    const el = h("div", { class: "notif-pop", role: "dialog", "aria-label": "Notifications" });
    document.body.append(el);
    const r = anchor.getBoundingClientRect();
    el.style.setProperty("top", Math.round(r.bottom + 8) + "px");
    el.style.setProperty("right", Math.max(8, Math.round(window.innerWidth - r.right - 8)) + "px");
    const onDown = e => { if (!el.contains(e.target) && !anchor.contains(e.target)) closeBell(); };
    const onKey = e => { if (e.key === "Escape") { closeBell(); anchor.focus(); } };
    document.addEventListener("pointerdown", onDown, true);
    document.addEventListener("keydown", onKey);
    anchor.setAttribute("aria-expanded", "true");
    // What was unread when you opened it stays highlighted while it's open; the count clears now.
    bell = { el, anchor, unreadAtOpen: store.readUpTo, cleanup: () => { document.removeEventListener("pointerdown", onDown, true); document.removeEventListener("keydown", onKey); } };
    paintBellPanel();
    store.markNotificationsRead();
}

function closeBell() {
    if (!bell) return;
    bell.cleanup();
    bell.el.remove();
    bell.anchor.setAttribute("aria-expanded", "false");
    bell = null;
}

function paintBellPanel() {
    const list = store.notifications.slice(0, 20);
    const body = h("div", { class: "notif-list" }, ...(list.length
        ? list.map(n => notificationItem(n, { unread: n.id > bell.unreadAtOpen, onOpen: closeBell }))
        : [h("div", { class: "empty compact" }, icon("bell"), h("h3", { text: "All quiet" }), h("p", { text: "Crashes, restarts, updates and machines going offline show up here." }))]));
    bell.el.replaceChildren(
        h("div", { class: "notif-pop-head" }, h("b", { class: "grow", text: "Notifications" }),
            h("a", { class: "btn ghost sm", href: "/notifications", onclick: () => closeBell() }, "See all", icon("chevronRight"))),
        body);
}

/** The top bar's ?: Help, opened at the topic for the page (or server tab) you're on. */
function helpTopButton() {
    return h("button", { class: "btn ghost icon-only topbar-help", title: "Help for this page", "aria-label": "Help for this page", onclick: () => {
        const parts = location.pathname.split("/"); // /machines/<m>/servers/<id>/<tab>
        const tab = parts[3] === "servers" ? (parts[5] || "overview") : null;
        navigate("/help#" + helpTopicFor(location.pathname, tab));
    } }, icon("help"));
}

const NAV = [
    { href: "/", label: "Overview", icon: "grid", match: p => p === "/" || p.startsWith("/machines/") },
    { href: "/install", label: "Install a server", icon: "plus", match: p => p.startsWith("/install") },
    { href: "/schedules", label: "Schedules", icon: "calendar", match: p => p.startsWith("/schedules") },
    { href: "/automations", label: "Automations", icon: "zap", admin: true, match: p => p.startsWith("/automations") },
    { href: "/notifications", label: "Notifications", icon: "bell", match: p => p.startsWith("/notifications") },
];
const ADMIN_NAV = [
    { href: "/plugins", label: "Game plugins", icon: "puzzle", admin: true },
    { href: "/discord", label: "Discord bot", icon: "message", owner: true },
    { href: "/users", label: "Users & access", icon: "users", admin: true },
    { href: "/machines", label: "Machines", icon: "machine", admin: true, match: p => p === "/machines" },
    { href: "/logs", label: "Logs", icon: "logs", admin: true, match: p => p.startsWith("/logs") || p.startsWith("/audit") },
    { href: "/storage", label: "Storage", icon: "disk", admin: true },
    { href: "/health", label: "Health checks", icon: "checkCircle", admin: true },
    { href: "/settings", label: "Agent settings", icon: "settings", owner: true },
    { href: "/account", label: "Your account", icon: "shield" },
    { href: "/help", label: "Help", icon: "help" },
];

function paintNav() {
    if (!frame) return;
    const path = location.pathname;
    const me = store.me || {};
    const link = item => h("a", { href: item.href, "aria-current": (item.match ? item.match(path) : path.startsWith(item.href)) ? "page" : null }, icon(item.icon), h("span", { text: item.label }));
    clear(frame.nav).append(...NAV.filter(i => !i.admin || me.canManageUsers).map(link));
    clear(frame.adminNav).append(...ADMIN_NAV.filter(i => (!i.admin || me.canManageUsers) && (!i.owner || me.isOwner)).map(link));
}

/** Sets the breadcrumb trail: [{label, href}] — the last one is the current page. */
export function setCrumbs(...items) {
    if (!frame) return;
    clear(frame.crumbs);
    items.forEach((c, i) => {
        const last = i === items.length - 1;
        if (i > 0) frame.crumbs.append(icon("chevronRight"));
        frame.crumbs.append(last ? h("b", { class: "truncate", text: c.label }) : h("a", { class: "crumb-parent truncate", href: c.href, text: c.label }));
    });
    pageTitle = items.length ? `${items[items.length - 1].label} · WindowsGSM` : "WindowsGSM";
    paintTitle();
    paintNav();
}

// The browser tab says when something needs you, so it shows from another tab: "(⚠ 2)" for problems (servers
// whose auto-restart gave up, machines offline), else "(3)" for unread notifications.
let pageTitle = "WindowsGSM";
export function paintTitle() {
    const servers = [...store.servers.values()];
    const problems = servers.filter(s => s.crashLoopSuspended).length
        + [...store.machines.values()].filter(m => !store.isOnline(m.id)).length;
    const badge = problems ? `(⚠ ${problems}) ` : store.unread ? `(${store.unread > 99 ? "99+" : store.unread}) ` : "";
    document.title = badge + pageTitle;
}
let titleWatch = false;
function watchTitle() {
    if (titleWatch) return;
    titleWatch = true;
    for (const ev of ["servers", "machines", "machine", "notifications"]) store.on(ev, paintTitle);
}

export function contentHost() { return frame.content; }

/** Shows (or with null, clears) the tip above the current page. */
export function setPageTip(el) {
    if (!frame) return;
    frame.tipSlot.replaceChildren(...(el ? [el] : []));
}

/** The sidebar's machine menu: jump to one machine's servers, or manage the list. */
function machineMenu() {
    const items = [{ heading: "Machines" }];
    if (store.multiMachine) items.push({ label: "All machines", icon: "grid", onClick: () => navigate("/") });
    for (const m of store.machines.values()) {
        items.push({
            label: m.name, icon: "machine",
            hint: m.online === false ? "offline" : `${m.serverCount} server${m.serverCount === 1 ? "" : "s"}`,
            onClick: () => navigate(store.multiMachine ? "/?machine=" + encodeURIComponent(m.id) : "/"),
        });
    }
    if (store.me?.canManageUsers) {
        items.push("-", { label: store.me.isOwner ? "Add another machine…" : "Manage machines", icon: store.me.isOwner ? "plus" : "settings", onClick: () => navigate("/machines") });
    }
    return items;
}

// ─────────────────────────── Notifications ───────────────────────────

const QUIET_JOBS = new Set(["start", "stop", "restart", "kill"]); // their result is visible on the page already

function wireNotifications() {
    // Machines coming and going have no other toast; everything else is announced by its own event below.
    store.on("notificationAdded", n => {
        popDesktop(n);
        if (n.kind === "machineOffline") toast(n.title, { type: "bad", text: n.text, timeout: 15000 });
        if (n.kind === "machineOnline") toast(n.title, { type: "good", text: n.text });
    });
    store.on("jobFinished", job => {
        if (job.status === "Succeeded" && QUIET_JOBS.has(job.kind)) return;
        const where = job.serverId && store.server(job.machine, job.serverId) ? () => navigate(serverPath(job.machine, job.serverId)) : null;
        if (job.status === "Succeeded") toast(job.title, { type: "good", text: "Done.", action: where ? { label: "Open", onClick: where } : null });
        else if (job.status === "Cancelled") toast(job.title, { type: "warn", text: "Cancelled." });
        else toast(job.title + " failed", { type: "bad", text: job.error || "See the activity panel for details.", action: { label: "Details", onClick: () => openJobs() } });
    });
    store.on("alert", e => {
        const kind = e.data.kind;
        const type = kind === "Crashed" || kind === "CrashLoopSuspended" || kind === "MemoryGuard" ? "bad" : kind === "JoinCode" ? "info" : "good";
        const s = store.server(e.machine, e.server);
        toast(e.data.title, { type, text: store.multiMachine ? `${e.data.text || ""} (${store.machineName(e.machine)})`.trim() : e.data.text, action: s ? { label: "Open", onClick: () => navigate(serverPath(s.machine, s.id)) } : null, timeout: type === "bad" ? 15000 : undefined });
    });
    store.on("promptRaised", p => {
        toast("A job needs your answer", { type: "warn", text: p.title, action: { label: "Answer", onClick: () => openJobs() }, timeout: 20000 });
        if (drawer) paintDrawer();
    });
}

// ─────────────────────────── Jobs drawer ───────────────────────────

export function openJobs() {
    if (drawer) return;
    const body = h("div", { class: "drawer-body" });
    const el = h("aside", { class: "drawer", role: "dialog", "aria-label": "Activity" },
        h("div", { class: "drawer-head" }, icon("activity"), h("h2", { class: "grow", text: "Activity" }),
            h("button", { class: "btn ghost sm icon-only", "aria-label": "Close", onclick: closeJobs }, icon("x"))),
        body);
    const scrim = h("div", { class: "drawer-scrim", onclick: closeJobs });
    document.body.append(scrim, el);
    const onKey = e => { if (e.key === "Escape") closeJobs(); };
    document.addEventListener("keydown", onKey);
    const unsubs = [store.on("jobs", () => paintDrawer()), store.on("prompts", () => paintDrawer())];
    drawer = { el, scrim, body, cleanup: () => { unsubs.forEach(u => u()); document.removeEventListener("keydown", onKey); }, open: new Set() };
    paintDrawer();
    el.querySelector(".drawer-head button").focus();
}

export function closeJobs() {
    if (!drawer) return;
    drawer.cleanup();
    drawer.el.remove();
    drawer.scrim.remove();
    drawer = null;
}

function paintDrawer() {
    if (!drawer) return;
    const body = drawer.body;
    const scroll = body.scrollTop;
    clear(body);

    for (const p of [...store.prompts.values()]) body.append(promptCard(p));

    const jobs = store.jobsNewestFirst();
    if (jobs.length === 0 && store.prompts.size === 0) {
        body.append(h("div", { class: "empty" }, icon("activity"), h("h3", { text: "Nothing yet" }), h("p", { text: "Installs, updates, backups and restarts show up here with live progress." })));
        return;
    }
    for (const job of jobs.slice(0, 60)) body.append(jobRow(job));
    body.scrollTop = scroll;
}

const JOB_ICONS = { rollback: "restore", install: "plus", import: "download", update: "update", validate: "validate", backup: "backup", restore: "restore", addon: "puzzle", start: "play", stop: "stop", restart: "restart", "auto-restart": "restart", kill: "kill", delete: "trash" };

function jobRow(job) {
    const status = job.status.toLowerCase();
    const id = key(job.machine, job.id);
    const isOpen = drawer.open.has(id);
    const bar = job.status === "Running" ? progressBar(job.percent, "") : null;
    const log = h("pre", { class: "job-log", hidden: !isOpen, text: (job.recentLog || []).join("\n") || "No output." });
    const server = job.serverId ? store.server(job.machine, job.serverId) : null;
    const serverName = server && !job.title.includes(server.name) ? server.name : null;
    const where = [serverName, store.multiMachine ? store.machineName(job.machine) : null].filter(Boolean).join(" · ");
    const row = h("div", { class: ["job", status] },
        h("div", { class: "job-top" },
            h("span", { class: "job-icon" }, icon(job.status === "Failed" ? "xCircle" : job.status === "Succeeded" ? "check" : JOB_ICONS[job.kind] || "jobs")),
            h("div", { class: "grow" },
                h("div", { class: "job-title truncate", text: job.title }),
                h("div", { class: "job-stage truncate", text: job.status === "Running" ? (job.stage || "Working…") + (job.percent != null ? ` · ${job.percent}%` : "") : job.status === "Failed" ? job.error || "Failed" : `${job.status} · ${timeAgo(job.endedAt || job.startedAt)}` })),
            job.status === "Running" ? h("button", { class: "btn ghost sm", onclick: e => busy(e.currentTarget, () => post(`/machines/${encodeURIComponent(job.machine)}/jobs/${job.id}/cancel`), "Couldn't cancel") }, "Cancel") : null,
            h("button", { class: "btn ghost sm icon-only", "aria-label": isOpen ? "Hide log" : "Show log", "aria-expanded": String(isOpen), onclick: () => {
                if (drawer.open.has(id)) drawer.open.delete(id); else drawer.open.add(id);
                paintDrawer();
            } }, icon(isOpen ? "chevronDown" : "chevronRight"))),
        bar,
        where ? h("div", { class: "tiny faint", text: where }) : null,
        log);
    if (isOpen) requestAnimationFrame(() => { log.scrollTop = log.scrollHeight; });
    return row;
}

function promptCard(p) {
    const job = store.jobs.get(key(p.machine, p.jobId));
    const answer = (value, button) => busy(button, async () => {
        await post(`/machines/${encodeURIComponent(p.machine)}/prompts/${p.id}`, { answer: value });
        store.prompts.delete(key(p.machine, p.id));
        store.emit("prompts");
    }, "Couldn't send the answer");
    return h("div", { class: "prompt-card" },
        h("h3", {}, icon("help"), p.title || "Question"),
        job ? h("div", { class: "tiny faint", text: "From: " + job.title + (store.multiMachine ? ` on ${store.machineName(p.machine)}` : "") }) : null,
        h("p", { text: p.message }),
        h("div", { class: "tiny faint", text: "No answer by " + new Date(p.expiresAt).toLocaleTimeString() + " counts as no." }),
        h("div", { class: "row" },
            h("button", { class: "btn primary", onclick: e => answer(true, e.currentTarget) }, icon("check"), "Yes, continue"),
            h("button", { class: "btn", onclick: e => answer(false, e.currentTarget) }, "No")));
}

export { gameLabel };
