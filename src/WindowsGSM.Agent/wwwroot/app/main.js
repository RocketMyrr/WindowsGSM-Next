// Entry point: routes, and the gate that makes sure you're signed in (or sends you to setup/sign-in).

import { h, clear } from "./dom.js";
import { route, start, navigate, currentPath } from "./router.js";
import { store } from "./store.js";
import { mountShell, unmountShell, contentHost, openJobs, setPageTip } from "./shell.js";
import { probe, onSignedOut } from "./session.js";
import { restoreTheme, restoreAccent } from "./theme.js";
import { tip } from "./tips.js";
import { applyPrefs } from "./prefs.js";
import { loading } from "./ui.js";

restoreTheme();
restoreAccent();
applyPrefs();
onSignedOut(() => unmountShell());

/** Pages outside the shell (sign-in, setup). */
function bare(load) {
    return async ctx => {
        unmountShell();
        const mod = await load();
        const root = clear(document.getElementById("app"));
        await mod.default(root, ctx);
    };
}

/** Pages inside the shell. Signs in / sets up first when needed. */
function inside(load) {
    return async ctx => {
        if (!store.loaded) {
            const root = document.getElementById("app");
            if (!root.querySelector(".app")) clear(root).append(h("div", { class: "auth-form" }, loading("Connecting to the agent…")));
            let state;
            try { state = await probe(); }
            catch (e) {
                clear(root).append(h("div", { class: "auth-form" }, h("div", { class: "auth-card" },
                    h("h2", { text: "Can't reach the agent" }), h("p", { class: "lead", text: e.message }),
                    h("button", { class: "btn primary", onclick: () => location.reload() }, "Try again"))));
                return;
            }
            if (state.info.setupRequired) { navigate("/setup", { replace: true }); return; }
            if (!state.signedIn) { navigate("/login?next=" + encodeURIComponent(currentPath()), { replace: true }); return; }
            await store.load();
        }
        mountShell();
        setPageTip(null);
        const host = clear(contentHost());
        const mod = await load();
        if (!ctx.scope.alive) return;
        // A short tip for the page (dismissible), above it — straight away, while the page loads. Server pages show one per tab.
        if (!location.pathname.includes("/servers/")) setPageTip(tip(location.pathname === "/" ? "/" : "/" + location.pathname.split("/")[1]));
        await mod.default(host, ctx);
        host.focus({ preventScroll: true });
    };
}

route("/login", bare(() => import("./views/auth.js").then(m => ({ default: m.login }))));
route("/setup", bare(() => import("./views/auth.js").then(m => ({ default: m.setup }))));
route("/", inside(() => import("./views/overview.js")));
route("/machines/:machine/servers/:id/:tab?", inside(() => import("./views/server.js")));
route("/notifications", inside(() => import("./views/notifications.js")));
route("/schedules", inside(() => import("./views/schedules.js")));
route("/automations", inside(() => import("./views/automations.js")));
route("/discord", inside(() => import("./views/discord.js")));
route("/plugins", inside(() => import("./views/plugins.js")));
route("/machines", inside(() => import("./views/machines.js")));
route("/install", inside(() => import("./views/install.js")));
route("/account", inside(() => import("./views/account.js")));
route("/users", inside(() => import("./views/users.js")));
route("/logs", inside(() => import("./views/logs.js")));
route("/storage", inside(() => import("./views/storage.js")));
route("/audit", inside(() => import("./views/audit.js"))); // → /logs?tab=activity
route("/health", inside(() => import("./views/health.js")));
route("/settings", inside(() => import("./views/settings.js")));
route("/help", inside(() => import("./views/help.js")));
route("*", inside(() => import("./views/notfound.js")));

// Never leave a blank page: if something breaks while rendering, say so and offer a way out.
function crashed(error) {
    console.error(error);
    const root = document.getElementById("app");
    if (root.querySelector(".crash")) return;
    const box = h("div", { class: "callout bad crash", role: "alert" },
        h("div", { class: "grow" },
            h("b", { text: "Something went wrong on this page." }),
            h("div", { class: "small", text: String(error && error.message || error) })),
        h("button", { class: "btn sm", onclick: () => location.reload() }, "Reload"));
    (root.querySelector("#content") || root).prepend(box);
}
window.addEventListener("error", e => crashed(e.error || e.message));
window.addEventListener("unhandledrejection", e => { if (!(e.reason && e.reason.name === "AbortError")) crashed(e.reason); });

// The desktop app's tray menu and notifications ask for a page without reloading this one.
window.addEventListener("wgsm-navigate", e => {
    const path = String(e.detail || "");
    if (!path.startsWith("/")) return;
    if (path.startsWith("/?activity")) { if (store.loaded) openJobs(); return; }
    navigate(path);
});

start();
