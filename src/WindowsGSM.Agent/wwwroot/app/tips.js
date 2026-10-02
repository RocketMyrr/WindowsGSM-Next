// Tips: a short, dismissible hint at the top of each page and server tab ("Got it" hides that one; Your account →
// Appearance turns them all off or brings them back), and which Help topic the top bar's ? opens for the page.

import { h, icon } from "./dom.js";
import { getPref, setPref } from "./prefs.js";

/** key → [help topic, tip text]. Keys: a path ("/schedules") or "server:<tab>". */
const TIPS = {
    "/": ["overview", "Click a server for its console, logs, files and settings. Tick several to start, stop or update them together; filter by tag or search with Ctrl K."],
    "/install": ["start", "Ports are picked for you so servers never clash. Have a template for this game? Pick it on the next step to start with your usual settings and config files."],
    "/schedules": ["schedules", "Schedules run at set times (restarts, backups, updates, commands). For “when something happens” — empty server, high CPU, a crash, low disk — use Automations."],
    "/automations": ["schedules", "Rules watch your servers and act for you: stop an empty server after a while, restart one stuck at full CPU, or tell you when something crashes or the disk runs low."],
    "/notifications": ["notify", "The bell collects crashes, updates and automation results. Add a channel (Discord webhook and others) to get them on your phone too."],
    "/plugins": ["addons", "Plugins add games WindowsGSM doesn't know yet. Install from the list, or add your own .cs file; updates keep the previous version so you can roll back."],
    "/discord": ["notify", "The bot gives your Discord a control panel: /panel, /list and /stats. Only the Discord users you list here can press its buttons, and only for the servers you pick."],
    "/users": ["users", "Give friends their own sign-in instead of sharing yours. Operators run servers; Viewers only look; access can be limited to particular servers."],
    "/machines": ["machines", "Several PCs? Make one the hub and connect the others — every server, one panel. A machine that goes offline keeps running its servers."],
    "/logs": ["trouble", "Everything WindowsGSM did and every error, newest first. The Activity tab shows who did what; Crash logs keep each crash's details."],
    "/storage": ["trouble", "What each server takes, and safe clean-up of old logs, crash dumps and unfinished downloads. Your servers' files and backups are never offered for clean-up."],
    "/health": ["trouble", "Checks that tools, plugins, ports and disk space are all in order. Run it after changing settings or when something won't start."],
    "/settings": ["network", "To use the panel from your phone or another PC, turn on “Reachable from other computers”. Opening it to the internet? Turn on HTTPS first."],
    "/account": ["users", "Turn on two-factor or add a passkey (Windows Hello, your phone) to keep your servers safe — it only takes a minute."],
    "/help": null,

    "server:overview": ["reach", "Players can't join? “Can players reach it?” checks the firewall, your router and Steam's list in one go — and can forward ports for you."],
    "server:console": ["console", "Type a command and press Enter. ↑ / ↓ bring back earlier commands. Switch to RCON to send over the network (set it up in Settings)."],
    "server:logs": ["trouble", "WindowsGSM's log for this server: starts, stops, crashes, updates and errors. The game's own log files are in Files (often logs\\ or Saved\\Logs)."],
    "server:players": ["players", "Who's online now, and who played lately and for how long. Empty while the server runs? This tab says which query port answers."],
    "server:gameconfig": ["files", "The game's own settings files as a form. Changes take effect after a restart; every save keeps the version before (History)."],
    "server:files": ["files", "Browse, edit, upload and download the server's files. Stop the server before replacing files it uses; text files keep their earlier versions."],
    "server:backups": ["backups", "Back up before big changes. Set how many to keep, back up automatically before each start, or copy every backup to another drive."],
    "server:addons": ["addons", "Mods and add-ons the game's plugin knows how to install. They're tracked, so updating never overwrites ones you built yourself."],
    "server:workshop": ["workshop", "Paste a Steam Workshop link or collection link. Mods update before each start if you like; some games need the Steam account (Agent settings)."],
    "server:schedules": ["schedules", "Restart nightly, back up hourly, send a message before a restart. Times use this machine's clock."],
    "server:settings": ["start", "Press ? next to a setting for what it does. Changes apply on the next start; History puts earlier settings back."],
    "server:minecraft": ["minecraft", "Pick Paper for plugins or Fabric for mods, then add them from Modrinth below. Back up before switching software or Minecraft versions."],
    "server:ark": ["ark", "Mods are CurseForge project ids, loaded top to bottom. Servers in one cluster share characters and items — give them the same cluster id."],
};

/** The Help topic for a page (for the top bar's ? button). */
export function helpTopicFor(path, tab) {
    if (tab && TIPS["server:" + tab]) return TIPS["server:" + tab][0];
    if (path.includes("/servers/")) return "start";
    const key = Object.keys(TIPS).filter(k => k.startsWith("/") && (path === k || (k !== "/" && path.startsWith(k)))).sort((a, b) => b.length - a.length)[0];
    return key && TIPS[key] ? TIPS[key][0] : "start";
}

export function tipsOn() { return getPref("tips") !== false; }

/** Brings every dismissed tip back. */
export function resetTips() { setPref("tipsDismissed", []); setPref("tips", true); }

/** The tip for a page or tab, or null (off, dismissed, or none). */
export function tip(key) {
    const t = TIPS[key];
    if (!t || !tipsOn()) return null;
    const dismissed = getPref("tipsDismissed") || [];
    if (dismissed.includes(key)) return null;
    const el = h("div", { class: "page-tip", role: "note" },
        icon("sparkles"),
        h("span", { class: "grow", text: t[1] }),
        h("a", { class: "page-tip-more", href: "/help#" + t[0] }, "More"),
        h("button", { type: "button", class: "page-tip-close", title: "Got it — hide this tip", "aria-label": "Hide this tip", onclick: () => {
            setPref("tipsDismissed", [...new Set([...(getPref("tipsDismissed") || []), key])]);
            el.remove();
        } }, icon("x")));
    return el;
}
