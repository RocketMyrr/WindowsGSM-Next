// Live console: game output streamed over the event socket, command box with history, RCON option.

import { h, icon, clear, copyText, debounce } from "../../dom.js";
import { get, post, srv } from "../../api.js";
import * as live from "../../live.js";
import { store } from "../../store.js";
import { toast, toastError, isRunning, segmented } from "../../ui.js";
import { getPref, setPref } from "../../prefs.js";

const MAX_LINES = 5000;

export default async function consoleTab(host, { id, machine, key, server, scope }) {
    const out = h("div", { class: "console-out", role: "log", "aria-live": "off", tabindex: "0" });
    const jump = h("button", { class: "console-jump btn sm", hidden: true, onclick: () => { stick = true; out.scrollTop = out.scrollHeight; jump.hidden = true; } }, icon("arrowUp"), "New output — jump to latest");
    const filterBox = h("input", { class: "input", type: "search", placeholder: "Filter output…", "aria-label": "Filter console output" });
    // Kept running through an agent restart (e.g. an update): its captured console can't be reconnected.
    const reattachedText = h("span");
    const reattached = h("div", { class: "console-banner", hidden: true }, icon("info"), reattachedText);
    // Not captured: the game's output goes to its own window on the server PC, so this view stays quiet.
    const windowedText = h("span");
    const windowed = h("div", { class: "console-banner", hidden: true }, icon("monitor"), windowedText);
    const notRunning = h("div", { class: "console-banner", hidden: true }, icon("info"), h("span", { text: "The server isn't running — showing its last output. Commands need a running server." }));
    const cmd = h("input", { class: "input mono", placeholder: "Type a command and press Enter…", autocomplete: "off", spellcheck: "false", "aria-label": "Console command" });
    const sendBtn = h("button", { class: "btn primary", type: "submit" }, icon("send"), "Send");
    let route = "auto";
    const routeToggle = segmented([{ value: "auto", label: "Console" }, { value: "rcon", label: "RCON" }], route, v => { route = v; cmd.focus(); });
    routeToggle.title = "Console types into the game's own console; RCON sends over the network (needs RCON set up in Settings).";

    const wrapBtn = h("button", { class: "btn ghost sm", title: "Wrap long lines", "aria-pressed": String(getPref("consoleWrap")), onclick: () => { setPref("consoleWrap", !getPref("consoleWrap")); wrapBtn.setAttribute("aria-pressed", String(getPref("consoleWrap"))); } }, "Wrap");

    // Servers whose output isn't captured run in their own console window on the server's screen: show / hide it.
    const windowBtn = h("button", { class: "btn ghost sm", hidden: true });
    async function paintWindow() {
        let w;
        try { w = await get(srv(machine, id, "/console-window")); } catch { windowBtn.hidden = true; return; }
        windowBtn.hidden = !w.hasWindow;
        reattached.hidden = !(w.reattached && w.captured);
        windowed.hidden = !w.running || w.captured;
        windowedText.textContent = w.hasWindow
            ? "This server runs in its own console window on the server PC, so its output shows there rather than here. Commands you send from here are typed into that window — Show window puts it on the server's screen."
            : "This server's output isn't captured here, and it has no console window WindowsGSM can show. Restarting the server gives it one — or turn on \"Capture the console here\" in Settings to see its output in this tab.";
        if (!reattached.hidden) {
            reattachedText.textContent = "This server kept running while WindowsGSM restarted (e.g. for an update), so its new output can't be shown here until the server's next restart. "
                + (w.rcon ? "Commands go over RCON meanwhile." : "Set up RCON in Settings to send commands meanwhile.");
            if (w.rcon && route !== "rcon") { route = "rcon"; routeToggle.set("rcon"); }
        }
        clear(windowBtn).append(icon(w.visible ? "eye" : "monitor"), w.visible ? "Hide window" : "Show window");
        windowBtn.title = w.visible ? "Hide the game's console window on the server's screen" : "Show the game's console window on the server's screen";
        windowBtn.onclick = async () => {
            try { await post(srv(machine, id, "/console-window"), { visible: !w.visible }); toast(w.visible ? "Console window hidden" : "Console window shown on the server's screen", { type: "good", timeout: 2500 }); paintWindow(); }
            catch (e) { toastError(e); }
        };
    }
    paintWindow();
    let windowState = server()?.state;
    scope.add(store.on("server:" + key, () => { if (server()?.state !== windowState) { windowState = server()?.state; setTimeout(paintWindow, 3000); } }));

    let stick = true;          // follow new output while the view is at the bottom
    let filter = "";
    let lineCount = 0;
    const history = loadHistory(id);
    let historyIndex = history.length;

    host.append(h("div", { class: "console" },
        h("div", { class: "console-bar" },
            h("div", { class: "search grow" }, icon("search"), filterBox),
            h("button", { class: "btn ghost sm", title: "Copy all", onclick: async () => { if (await copyText(allText())) toast("Console copied", { type: "good", timeout: 2000 }); } }, icon("copy"), "Copy"),
            h("button", { class: "btn ghost sm", title: "Download as text", onclick: download }, icon("download"), "Save"),
            h("button", { class: "btn ghost sm", title: "Clear this view (the server keeps its buffer)", onclick: () => { clear(out); lineCount = 0; } }, icon("trash"), "Clear view"),
            windowBtn,
            // Text size and wrapping, right here (also under Your account → Appearance).
            h("button", { class: "btn ghost sm icon-only", title: "Smaller text", "aria-label": "Smaller text", onclick: () => setPref("consoleSize", getPref("consoleSize") === "large" ? "medium" : "small") }, "A−"),
            h("button", { class: "btn ghost sm icon-only", title: "Larger text", "aria-label": "Larger text", onclick: () => setPref("consoleSize", getPref("consoleSize") === "small" ? "medium" : "large") }, "A+"),
            wrapBtn),
        notRunning,
        reattached,
        windowed,
        h("div", { class: "console-wrap" }, out, jump),
        h("form", { class: "console-input", onsubmit: e => { e.preventDefault(); send(); } },
            h("span", { class: "prompt mono", text: "›" }), cmd, routeToggle, sendBtn)));

    function classify(line) {
        if (/\b(error|exception|fatal|failed|crash)/i.test(line)) return "err";
        if (/\bwarn(ing)?\b/i.test(line)) return "warn";
        return "";
    }

    function addLine(text, kind = "") {
        const el = h("div", { class: ["cl", kind || classify(text)] }, text);
        if (filter && !text.toLowerCase().includes(filter)) el.hidden = true;
        out.append(el);
        lineCount++;
        if (lineCount > MAX_LINES) { out.firstElementChild?.remove(); lineCount--; }
    }

    function afterAppend() {
        if (stick) out.scrollTop = out.scrollHeight;
        else jump.hidden = false;
    }

    out.addEventListener("scroll", () => {
        stick = out.scrollHeight - out.scrollTop - out.clientHeight < 40;
        if (stick) jump.hidden = true;
    });

    filterBox.addEventListener("input", debounce(() => {
        filter = filterBox.value.trim().toLowerCase();
        for (const el of out.children) el.hidden = !!filter && !el.textContent.toLowerCase().includes(filter);
        if (stick) out.scrollTop = out.scrollHeight;
    }, 120));

    async function load() {
        const snap = await get(srv(machine, id, "/console"));
        clear(out);
        lineCount = 0;
        for (const l of snap.lines) addLine(l);
        if (!snap.lines.length) addLine("No output yet. Start the server to see its console here.", "sys");
        out.scrollTop = out.scrollHeight;
    }

    async function send() {
        const text = cmd.value.trim();
        if (!text) return;
        if (!isRunning(server())) { toast("Start the server first", { type: "warn", text: "Commands can only be sent to a running server." }); return; }
        sendBtn.classList.add("busy");
        try {
            const res = await post(srv(machine, id, "/console"), { command: text, preferRcon: route === "rcon" });
            if (history[history.length - 1] !== text) { history.push(text); if (history.length > 100) history.shift(); saveHistory(id, history); }
            historyIndex = history.length;
            cmd.value = "";
            addLine(`› ${text}${res.route && res.route !== "Embedded" ? `   (${res.route})` : ""}`, res.sent ? "cmd" : "err");
            if (!res.sent) addLine(res.error || "Couldn't send that command.", "err");
            if (res.reply) for (const l of res.reply.split(/\r?\n/)) addLine(l, "reply");
            stick = true;
            afterAppend();
        } catch (e) { toastError(e, "Couldn't send the command"); }
        finally { sendBtn.classList.remove("busy"); cmd.focus(); }
    }

    cmd.addEventListener("keydown", e => {
        if (e.key === "ArrowUp" && history.length) { e.preventDefault(); historyIndex = Math.max(0, historyIndex - 1); cmd.value = history[historyIndex] || ""; }
        if (e.key === "ArrowDown" && history.length) { e.preventDefault(); historyIndex = Math.min(history.length, historyIndex + 1); cmd.value = history[historyIndex] || ""; }
    });

    function allText() { return [...out.children].map(el => el.textContent).join("\n"); }
    function download() {
        const blob = new Blob([allText()], { type: "text/plain" });
        const a = h("a", { href: URL.createObjectURL(blob), download: `server-${id}-console.txt` });
        document.body.append(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    }

    function paintState() {
        const running = isRunning(server());
        notRunning.hidden = running;
        cmd.disabled = !running;
        sendBtn.disabled = !running;
        cmd.placeholder = running ? "Type a command and press Enter…  (↑ ↓ for history)" : "Start the server to send commands";
    }

    // Live lines — batched into one DOM update per frame so a chatty server doesn't lock up the page.
    let pending = [];
    let frame = 0;
    scope.add(store.on("console:" + key, line => {
        pending.push(line);
        if (!frame) frame = requestAnimationFrame(() => {
            frame = 0;
            const batch = pending;
            pending = [];
            if (out.firstElementChild && out.firstElementChild.classList.contains("sys") && lineCount === 1) { clear(out); lineCount = 0; }
            for (const l of batch) addLine(l);
            afterAppend();
        });
    }));
    scope.add(live.watch(`console:${key}`));
    scope.add(store.on("server:" + key, s => {
        paintState();
        if (s && s.state === "Starting") { clear(out); lineCount = 0; addLine("Starting…", "sys"); } // the engine clears its buffer on start
    }));
    scope.add(store.on("reset", () => load().catch(() => { })));

    paintState();
    try { await load(); } catch (e) { addLine("Couldn't load the console: " + e.message, "err"); }
    if (isRunning(server())) cmd.focus();
}

function loadHistory(id) { try { return JSON.parse(sessionStorage.getItem("wgsm-cmd-" + id) || "[]"); } catch { return []; } }
function saveHistory(id, list) { try { sessionStorage.setItem("wgsm-cmd-" + id, JSON.stringify(list)); } catch { /* private mode */ } }
