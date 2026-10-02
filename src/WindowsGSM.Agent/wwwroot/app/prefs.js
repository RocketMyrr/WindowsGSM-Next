// Display preferences kept per browser (like the theme): how dense the server list is, and the console's text
// size and line wrapping. Applied as attributes on <html> so plain CSS does the work.

const KEY = "wgsm-prefs";
const DEFAULTS = { density: "comfortable", consoleSize: "medium", consoleWrap: true, tips: true, tipsDismissed: [] };
let prefs = load();

function load() {
    try { return { ...DEFAULTS, ...JSON.parse(localStorage.getItem(KEY) || "{}") }; } catch { return { ...DEFAULTS }; }
}

export function getPref(name) { return prefs[name]; }

export function setPref(name, value) {
    prefs = { ...prefs, [name]: value };
    try { localStorage.setItem(KEY, JSON.stringify(prefs)); } catch { /* private mode */ }
    applyPrefs();
    window.dispatchEvent(new CustomEvent("wgsm-prefs", { detail: { name, value } }));
}

export function applyPrefs() {
    const d = document.documentElement.dataset;
    d.density = prefs.density;
    d.consoleSize = prefs.consoleSize;
    d.consoleWrap = prefs.consoleWrap ? "on" : "off";
}
