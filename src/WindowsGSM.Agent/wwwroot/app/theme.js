// Light/dark theme. No stored choice = follow the operating system.

const KEY = "wgsm-theme";

export function currentTheme() {
    const set = document.documentElement.dataset.theme;
    if (set) return set;
    return window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

export function applyTheme(theme) {
    if (theme) document.documentElement.dataset.theme = theme;
    else delete document.documentElement.dataset.theme;
    try { theme ? localStorage.setItem(KEY, theme) : localStorage.removeItem(KEY); } catch { /* private mode */ }
    syncDesktopChrome();
}

/** In the desktop app: the window's title bar takes the panel's colours (Windows 11). */
export function syncDesktopChrome() {
    const webview = window.chrome && window.chrome.webview;
    if (!webview) return;
    const css = getComputedStyle(document.documentElement);
    const v = name => css.getPropertyValue(name).trim();
    try { webview.postMessage({ type: "chrome", theme: currentTheme(), bg: v("--bg"), text: v("--text"), line: v("--line") }); } catch { /* not connected */ }
}

// "Follow Windows": when Windows switches between light and dark, so does the title bar.
try { window.matchMedia("(prefers-color-scheme: light)").addEventListener("change", () => syncDesktopChrome()); } catch { /* old browser */ }

export function restoreTheme() {
    let saved = null;
    try { saved = localStorage.getItem(KEY); } catch { /* private mode */ }
    if (saved === "light" || saved === "dark") document.documentElement.dataset.theme = saved;
    syncDesktopChrome();
}

// Accent colour: blue (default), violet, teal, orange, rose. Per browser, like the theme.
const ACCENT_KEY = "wgsm-accent";
export const ACCENTS = ["blue", "violet", "teal", "orange", "rose"];

export function currentAccent() { return document.documentElement.dataset.accent || "blue"; }

export function applyAccent(accent) {
    if (accent && accent !== "blue" && ACCENTS.includes(accent)) document.documentElement.dataset.accent = accent;
    else delete document.documentElement.dataset.accent;
    try { accent && accent !== "blue" ? localStorage.setItem(ACCENT_KEY, accent) : localStorage.removeItem(ACCENT_KEY); } catch { /* private mode */ }
}

export function restoreAccent() {
    let saved = null;
    try { saved = localStorage.getItem(ACCENT_KEY); } catch { /* private mode */ }
    if (saved && ACCENTS.includes(saved) && saved !== "blue") document.documentElement.dataset.accent = saved;
}
