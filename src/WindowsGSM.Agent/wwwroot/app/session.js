// Signing in and out, and what happens when the session ends underneath us.

import { post, get, onUnauthorized } from "./api.js";
import { store } from "./store.js";
import * as live from "./live.js";
import { navigate, currentPath } from "./router.js";
import { toast } from "./ui.js";

let unmount = null;

/** The shell registers how to tear itself down when the session ends. */
export function onSignedOut(fn) { unmount = fn; }

export async function signOut() {
    try { await post("/auth/logout"); } catch { /* already gone */ }
    endSession("/login");
}

function endSession(to, message) {
    const wasLoaded = store.loaded;
    store.unload();
    if (unmount) unmount();
    if (message && wasLoaded) toast(message, { type: "warn" });
    navigate(to, { replace: true });
}

onUnauthorized(() => {
    if (!store.loaded) return;
    const back = currentPath();
    endSession("/login?next=" + encodeURIComponent(back), "You've been signed out. Sign in again to continue.");
});
live.on("signedOut", () => endSession("/login", "Your session ended — it was signed out elsewhere or the account changed."));

/** Is anyone signed in? Also tells us if first-run setup is still needed. */
export async function probe() {
    const info = await get("/info");
    store.info = info;
    if (info.setupRequired) return { info, signedIn: false };
    try { await get("/auth/me"); return { info, signedIn: true }; }
    catch (e) { if (e.status === 401) return { info, signedIn: false }; throw e; }
}
