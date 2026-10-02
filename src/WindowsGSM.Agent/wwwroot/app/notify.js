// Notifications, shared by the top-bar bell and the Notifications page: how one looks, where clicking it
// goes, and (opt-in, per device) desktop pop-ups while the panel is in a background tab.

import { h, icon, timeAgo, fmtDateTime } from "./dom.js";
import { store } from "./store.js";
import { navigate, serverPath } from "./router.js";

export const SEVERITY_ICON = { bad: "xCircle", warn: "warn", good: "checkCircle", info: "info" };
const KIND_ICON = { machineOffline: "machine", machineOnline: "machine", joinCode: "key", jobFailed: "jobs", autoUpdated: "update", autoRestarted: "restart", scheduledRestart: "restart", autoStarted: "play" };

/** Where a notification leads: its server, or its machine's servers. */
export function openNotification(n) {
    if (n.server && store.server(n.machine, n.server)) navigate(serverPath(n.machine, n.server));
    else if (n.kind === "jobFailed") navigate("/");
    else navigate(store.multiMachine ? "/?machine=" + encodeURIComponent(n.machine) : "/");
}

/** One notification as a clickable row. unread: highlight it. */
export function notificationItem(n, { unread = false, onOpen } = {}) {
    const where = [n.serverName || (n.server ? "#" + n.server : null), store.multiMachine ? store.machineName(n.machine) : null].filter(Boolean).join(" · ");
    return h("button", { class: ["notif", n.severity, unread && "unread"], onclick: () => { openNotification(n); if (onOpen) onOpen(n); } },
        h("span", { class: "notif-icon" }, icon(KIND_ICON[n.kind] || SEVERITY_ICON[n.severity] || "info")),
        h("span", { class: "notif-main" },
            h("b", { class: "notif-title", text: n.title }),
            n.text ? h("span", { class: "notif-text", text: n.text }) : null,
            h("span", { class: "notif-meta" }, h("span", { title: fmtDateTime(n.at), text: timeAgo(n.at) }), where ? h("span", { text: where }) : null)),
        unread ? h("span", { class: "notif-dot", "aria-label": "Unread" }) : null);
}

// ── Desktop pop-ups (this device only) ──

/** Running inside the WindowsGSM desktop app (WebView2): it shows notifications itself, from the tray. */
export const inDesktopApp = !!(window.chrome && window.chrome.webview);

const pathOf = n => n.server && store.server(n.machine, n.server) ? serverPath(n.machine, n.server)
    : store.multiMachine ? "/?machine=" + encodeURIComponent(n.machine) : "/";

const DESKTOP_KEY = "wgsm-desktop-notifications";

export function desktopSupported() { return typeof Notification !== "undefined"; }

export function desktopEnabled() {
    try { return desktopSupported() && Notification.permission === "granted" && localStorage.getItem(DESKTOP_KEY) === "1"; }
    catch { return false; }
}

/** Turns desktop pop-ups on (asking the browser's permission) or off. Resolves whether they're on. */
export async function setDesktop(on) {
    if (!desktopSupported()) return false;
    if (on && Notification.permission !== "granted") {
        const answer = await Notification.requestPermission();
        if (answer !== "granted") return false;
    }
    try { localStorage.setItem(DESKTOP_KEY, on ? "1" : "0"); } catch { /* private mode: nothing to remember */ }
    return on;
}

/** Shows a pop-up for a new notification when the panel isn't the tab you're looking at. */
export function popDesktop(n) {
    if (inDesktopApp) {
        // The app decides (it only shows one while its window isn't in front, and has its own on/off switch).
        const where = n.serverName ? ` (${n.serverName}${store.multiMachine ? " · " + store.machineName(n.machine) : ""})` : "";
        try { window.chrome.webview.postMessage({ type: "notify", id: n.id, title: n.title, body: (n.text || "") + where, path: pathOf(n) }); } catch { /* not connected */ }
        return;
    }
    if (!document.hidden || !desktopEnabled()) return;
    try {
        const where = n.serverName ? ` (${n.serverName}${store.multiMachine ? " · " + store.machineName(n.machine) : ""})` : "";
        const note = new Notification(n.title, { body: (n.text || "") + where, tag: "wgsm-" + n.id, icon: "/img/logo-192.png" });
        note.onclick = () => { window.focus(); openNotification(n); note.close(); };
    } catch { /* some browsers only allow this from a service worker */ }
}
