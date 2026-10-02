// Path-based router (real URLs you can bookmark and share: /machines/m-abc/servers/7/console). Each page
// render gets a Scope; everything registered on it (store listeners, live topics, timers) is torn down
// when you navigate away, so pages never leak pollers or handlers.

export class Scope {
    constructor() { this.alive = true; this._cleanups = []; }
    /** Registers an unsubscribe/cleanup function. Returns it for convenience. */
    add(fn) { if (typeof fn === "function") this._cleanups.push(fn); return fn; }
    /** Runs fn now and then every ms while the tab is visible. */
    every(ms, fn, { now = true } = {}) {
        let timer = null;
        const tick = async () => { if (!this.alive) return; if (!document.hidden) { try { await fn(); } catch (e) { console.warn(e); } } };
        if (now) tick();
        timer = setInterval(tick, ms);
        const onVisible = () => { if (!document.hidden) tick(); };
        document.addEventListener("visibilitychange", onVisible);
        this.add(() => { clearInterval(timer); document.removeEventListener("visibilitychange", onVisible); });
    }
    listen(target, type, fn, opts) { target.addEventListener(type, fn, opts); this.add(() => target.removeEventListener(type, fn, opts)); }
    dispose() {
        this.alive = false;
        for (const fn of this._cleanups.splice(0).reverse()) { try { fn(); } catch (e) { console.warn(e); } }
    }
}

const routes = [];
let current = null;        // { route, params, scope }
let guard = null;          // () => Promise<boolean> — asked before leaving (unsaved changes)
let renderHook = null;     // (route, params, scope) => void

/** route("/machines/:m/servers/:id/:tab?", handler, { title }) */
export function route(pattern, handler, meta = {}) {
    const keys = [];
    if (pattern === "*") { routes.push({ pattern, regex: /^$/, keys, handler, meta }); return; } // fallback
    const regex = new RegExp("^" + pattern.replace(/\/:(\w+)(\?)?/g, (_, key, optional) => {
        keys.push(key);
        return optional ? "(?:/([^/]+))?" : "/([^/]+)";
    }) + "/?$");
    routes.push({ pattern, regex, keys, handler, meta });
}

export function onRender(fn) { renderHook = fn; }

/** While set, navigating away asks fn() first (return true to leave). */
export function setLeaveGuard(fn) { guard = fn; }
export function hasLeaveGuard() { return guard != null; }

export function currentPath() { return location.pathname + location.search; }

export async function navigate(path, { replace = false } = {}) {
    if (path === currentPath() && current) return;
    if (guard && !(await guard())) return;
    guard = null;
    history[replace ? "replaceState" : "pushState"]({}, "", path);
    await render();
}

/** Re-renders the current route (after sign-in, for example). */
export async function render() {
    const path = location.pathname;
    let match = null;
    for (const r of routes) {
        const m = r.regex.exec(path);
        if (m) {
            const params = {};
            r.keys.forEach((k, i) => { if (m[i + 1] !== undefined) params[k] = decodeURIComponent(m[i + 1]); });
            match = { route: r, params };
            break;
        }
    }
    if (!match) match = { route: routes.find(r => r.pattern === "*"), params: {} };

    if (current) current.scope.dispose();
    const scope = new Scope();
    current = { ...match, scope };
    const query = new URLSearchParams(location.search);
    if (renderHook) renderHook(match.route, match.params, scope);
    await match.route.handler({ params: match.params, query, scope });
    window.scrollTo({ top: 0 });
}

export function start() {
    window.addEventListener("popstate", async () => {
        if (guard && !(await guard())) { history.pushState({}, "", current ? location.href : "/"); return; }
        guard = null;
        render();
    });
    // Same-origin links navigate in-app (ctrl/cmd-click and external links behave normally).
    document.addEventListener("click", e => {
        const a = e.target.closest("a[href]");
        if (!a || a.target === "_blank" || a.hasAttribute("download") || e.defaultPrevented) return;
        if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        const url = new URL(a.href, location.href);
        if (url.origin !== location.origin || url.pathname.startsWith("/api/")) return;
        // "#topic" on the same page: let the browser scroll to it.
        if (url.hash && url.pathname === location.pathname && url.search === location.search) return;
        e.preventDefault();
        navigate(url.pathname + url.search + url.hash);
    });
    window.addEventListener("beforeunload", e => { if (guard) { e.preventDefault(); e.returnValue = ""; } });
    return render();
}

/** A server page's URL: serverPath(machine, "7", "console") → /machines/{machine}/servers/7/console */
export const serverPath = (machine, id, tab) => `/machines/${encodeURIComponent(machine)}/servers/${encodeURIComponent(id)}${tab ? "/" + tab : ""}`;
