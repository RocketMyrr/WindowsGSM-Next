// Reusable UI pieces: toasts, dialogs, menus, status pills, meters, charts, form fields.

import { h, icon, clear, append } from "./dom.js";

// ─────────────────────────── Toasts ───────────────────────────

let toastHost = null;

// The announcer is on the page from the start: screen readers only read out changes to a live region that already
// exists, so one made together with the first toast would stay silent.
function toastArea() {
    if (!toastHost) {
        toastHost = h("div", { class: "toasts", role: "status", "aria-live": "polite" });
        document.body.append(toastHost);
    }
    return toastHost;
}
if (document.body) toastArea();

/**
 * toast("Saved", { type: "good" | "bad" | "warn" | "info", text, action: {label, onClick}, timeout })
 * Errors stay up longer; everything can be clicked away.
 */
export function toast(title, { type = "info", text = "", action = null, timeout } = {}) {
    toastArea();
    const iconName = { good: "checkCircle", bad: "xCircle", warn: "warn", info: "info" }[type] || "info";
    const el = h("div", { class: ["toast", type] },
        icon(iconName),
        h("div", { class: "grow" }, h("div", { class: "toast-title", text: title }), text ? h("div", { class: "toast-text", text }) : null),
        action ? h("button", { class: "btn sm", onclick: (e) => { e.stopPropagation(); action.onClick(); close(); } }, action.label) : null,
    );
    const close = () => {
        if (!el.isConnected) return;
        el.classList.add("leaving");
        setTimeout(() => el.remove(), 220);
    };
    el.addEventListener("click", close);
    toastHost.append(el);
    while (toastHost.children.length > 5) toastHost.firstElementChild.remove();
    setTimeout(close, timeout ?? (type === "bad" ? 9000 : type === "warn" ? 7000 : 4500));
    return close;
}

export function toastError(e, title = "That didn't work") {
    const details = e && e.details && e.details.length ? e.details.join(" · ") : "";
    toast(title, { type: "bad", text: [e && e.message, details].filter(Boolean).join(" — ") });
}

// ─────────────────────────── Dialogs ───────────────────────────

/**
 * Opens a modal. content: Node or (close) => Node. Returns { close, el, done: Promise<any> }.
 * Focus is trapped inside; Escape closes (resolving undefined).
 */
export function modal({ title, subtitle, iconName, tone = "", body, footer, wide = false, onClose }) {
    const previouslyFocused = document.activeElement;
    let resolve;
    const done = new Promise(r => resolve = r);
    const titleId = "dlg-" + Math.random().toString(36).slice(2);
    const dialog = h("div", { class: ["dialog", wide && "wide"], role: "dialog", "aria-modal": "true", "aria-labelledby": titleId });
    const scrim = h("div", { class: "scrim" }, dialog);

    const close = (value) => {
        if (!scrim.isConnected) return;
        scrim.remove();
        document.removeEventListener("keydown", onKey, true);
        if (onClose) onClose(value);
        resolve(value);
        if (previouslyFocused && previouslyFocused.focus) previouslyFocused.focus();
    };

    append(dialog, [
        h("div", { class: "dialog-head" },
            iconName ? h("div", { class: ["dialog-icon", tone] }, icon(iconName)) : null,
            h("div", { class: "grow" }, h("h2", { id: titleId, text: title }), subtitle ? h("p", { text: subtitle }) : null),
            h("button", { class: "btn ghost sm icon-only", "aria-label": "Close", onclick: () => close(undefined) }, icon("x")),
        ),
        h("div", { class: "dialog-body" }, typeof body === "function" ? body(close) : body),
        footer ? h("div", { class: "dialog-foot" }, typeof footer === "function" ? footer(close) : footer) : null,
    ]);

    function onKey(e) {
        if (e.key === "Escape") { e.stopPropagation(); close(undefined); return; }
        if (e.key !== "Tab") return;
        const focusable = [...dialog.querySelectorAll("button, [href], input, select, textarea, [tabindex]:not([tabindex='-1'])")].filter(x => !x.disabled && x.offsetParent !== null);
        if (!focusable.length) return;
        const first = focusable[0], last = focusable[focusable.length - 1];
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    }
    document.addEventListener("keydown", onKey, true);
    scrim.addEventListener("mousedown", e => { if (e.target === scrim) close(undefined); });
    document.body.append(scrim);
    requestAnimationFrame(() => {
        const autofocus = dialog.querySelector("[autofocus]") || dialog.querySelector(".dialog-body input, .dialog-body select, .dialog-body textarea") || dialog.querySelector(".dialog-foot .btn:last-child");
        autofocus && autofocus.focus();
    });
    return { close, el: dialog, done };
}

/** Resolves true/false. */
export function confirm({ title, message, confirmLabel = "Confirm", danger = false, iconName }) {
    const m = modal({
        title,
        subtitle: message,
        iconName: iconName || (danger ? "warn" : "help"),
        tone: danger ? "danger" : "",
        body: null,
        footer: (close) => [
            h("button", { class: "btn", onclick: () => close(false) }, "Cancel"),
            h("button", { class: ["btn", danger ? "danger solid" : "primary"], onclick: () => close(true) }, confirmLabel),
        ],
    });
    return m.done.then(v => v === true);
}

/** Resolves the entered string, or null if cancelled. validate(value) → error string | null. */
export function promptText({ title, message, label, value = "", placeholder = "", confirmLabel = "OK", validate, iconName = "pencil" }) {
    const input = h("input", { class: "input", value, placeholder, "aria-label": label || title });
    const error = h("div", { class: "error", hidden: true });
    const m = modal({
        title, subtitle: message, iconName,
        body: h("div", { class: "field" }, label ? h("label", { text: label }) : null, input, error),
        footer: (close) => {
            const submit = () => {
                const problem = validate ? validate(input.value) : null;
                if (problem) { error.textContent = problem; error.hidden = false; input.focus(); return; }
                close(input.value);
            };
            input.addEventListener("keydown", e => { if (e.key === "Enter") submit(); });
            return [h("button", { class: "btn", onclick: () => close(null) }, "Cancel"), h("button", { class: "btn primary", onclick: submit }, confirmLabel)];
        },
    });
    requestAnimationFrame(() => { input.focus(); input.select(); });
    return m.done.then(v => (typeof v === "string" ? v : null));
}

// ─────────────────────────── Menus ───────────────────────────

let openMenu = null;

/** items: [{ label, icon, onClick, danger, disabled, hidden } | "-" | { heading }] */
export function showMenu(anchor, items, { align = "end" } = {}) {
    closeMenu();
    const menu = h("div", { class: "menu", role: "menu" });
    for (const item of items) {
        if (!item || item.hidden) continue;
        if (item === "-") { menu.append(h("hr")); continue; }
        if (item.heading) { menu.append(h("div", { class: "menu-label", text: item.heading })); continue; }
        menu.append(h("button", {
            role: "menuitem", class: item.danger ? "danger" : "", disabled: item.disabled,
            onclick: () => { closeMenu(); item.onClick(); },
        }, item.icon ? icon(item.icon) : null, h("span", { class: "grow", text: item.label }), item.hint ? h("span", { class: "tiny faint", text: item.hint }) : null));
    }
    document.body.append(menu);
    const r = anchor.getBoundingClientRect();
    const mw = menu.offsetWidth, mh = menu.offsetHeight;
    let left = align === "end" ? r.right - mw : r.left;
    let top = r.bottom + 6;
    if (top + mh > window.innerHeight - 8) top = Math.max(8, r.top - mh - 6);
    left = Math.max(8, Math.min(left, window.innerWidth - mw - 8));
    menu.style.setProperty("left", left + "px");
    menu.style.setProperty("top", top + "px");
    anchor.setAttribute("aria-expanded", "true");
    openMenu = { menu, anchor };
    const first = menu.querySelector("button:not(:disabled)");
    first && first.focus();
    menu.addEventListener("keydown", e => {
        const buttons = [...menu.querySelectorAll("button:not(:disabled)")];
        const i = buttons.indexOf(document.activeElement);
        if (e.key === "ArrowDown") { e.preventDefault(); buttons[(i + 1) % buttons.length].focus(); }
        if (e.key === "ArrowUp") { e.preventDefault(); buttons[(i - 1 + buttons.length) % buttons.length].focus(); }
        if (e.key === "Escape") { closeMenu(); anchor.focus(); }
    });
}

export function closeMenu() {
    if (!openMenu) return;
    openMenu.menu.remove();
    openMenu.anchor.setAttribute("aria-expanded", "false");
    openMenu = null;
}
document.addEventListener("mousedown", e => { if (openMenu && !openMenu.menu.contains(e.target) && !openMenu.anchor.contains(e.target)) closeMenu(); });
window.addEventListener("resize", closeMenu);
window.addEventListener("scroll", closeMenu, true);

// ─────────────────────────── Status ───────────────────────────

const STATES = {
    Running: ["st-running", "Running"],
    Starting: ["st-transition", "Starting"],
    Stopping: ["st-transition", "Stopping"],
    Restarting: ["st-transition", "Restarting"],
    Installing: ["st-busy", "Installing"],
    Updating: ["st-busy", "Updating"],
    UpdatingAddons: ["st-busy", "Updating add-ons"],
    BackingUp: ["st-busy", "Backing up"],
    Restoring: ["st-busy", "Restoring"],
    Moving: ["st-busy", "Moving files"],
    Deleting: ["st-busy", "Deleting"],
    Stopped: ["", "Stopped"],
};

export function statusOf(server) {
    if (server.crashLoopSuspended && server.state === "Stopped") return { cls: "st-bad", label: "Crash loop — paused" };
    const [cls, label] = STATES[server.state] || ["", server.state];
    return { cls, label };
}

export function statusPill(server) {
    const { cls, label } = statusOf(server);
    return h("span", { class: ["pill", cls] }, h("span", { class: "dot" }), label);
}

export const isRunning = s => s.state === "Running";
export const isStopped = s => s.state === "Stopped";
export const isBusy = s => !isRunning(s) && !isStopped(s);

// ─────────────────────────── Meters & charts ───────────────────────────

export function meter(label, percent, valueText, kind = "cpu") {
    const bar = h("span");
    const p = Math.max(0, Math.min(100, percent || 0));
    bar.style.setProperty("width", p + "%");
    const cls = ["meter", kind, p >= 90 ? "critical" : p >= 75 ? "hot" : ""];
    return h("div", { class: cls },
        h("div", { class: "meter-top" }, h("span", { text: label }), h("b", { text: valueText })),
        h("div", { class: "progress", role: "progressbar", "aria-valuenow": Math.round(p), "aria-valuemin": 0, "aria-valuemax": 100, "aria-label": label }, bar));
}

export function progressBar(percent, state = "") {
    const bar = h("span");
    const el = h("div", { class: ["progress", state], role: "progressbar", "aria-valuemin": 0, "aria-valuemax": 100 }, bar);
    setProgress(el, percent, state);
    return el;
}

export function setProgress(el, percent, state = "") {
    const bar = el.firstElementChild;
    el.className = ["progress", state, percent == null && state === "" ? "indeterminate" : ""].filter(Boolean).join(" ");
    bar.style.setProperty("width", (percent == null ? 40 : Math.max(2, Math.min(100, percent))) + "%");
    if (percent != null) el.setAttribute("aria-valuenow", Math.round(percent)); else el.removeAttribute("aria-valuenow");
}

function svg(tag, attrs = {}) {
    const el = document.createElementNS("http://www.w3.org/2000/svg", tag);
    for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
    return el;
}

/** Small area sparkline. values: numbers (oldest first). */
export function sparkline(values, kind = "cpu", max = null) {
    const W = 120, H = 34;
    const root = svg("svg", { class: "spark " + kind, viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: "none", "aria-hidden": "true" });
    const pts = values.filter(v => v != null);
    if (pts.length < 2) return root;
    const top = max ?? Math.max(...pts, 1);
    const step = W / (pts.length - 1);
    const coords = pts.map((v, i) => [i * step, H - 2 - (Math.min(v, top) / top) * (H - 4)]);
    const line = coords.map((c, i) => (i ? "L" : "M") + c[0].toFixed(1) + " " + c[1].toFixed(1)).join(" ");
    root.append(svg("path", { class: "area", d: `${line} L${W} ${H} L0 ${H} Z` }), svg("path", { class: "line", d: line }));
    return root;
}

/**
 * Time-series chart: CPU % (left axis, 0–100), RAM (MB on its own scale, or % with ramPercent), and
 * optionally disk % and players (on their own scale). points: [{at (ms), cpu, ram, disk?, players?, maxPlayers?}]
 * oldest first. Long gaps (the server was off) break the lines instead of drawing across them.
 */
export function metricsChart(points, { height = 180, ramPercent = false, disk = false, players = false, empty = "Collecting samples…" } = {}) {
    const W = 720, H = height, padL = 34, padR = 52, padT = 10, padB = 22;
    const root = svg("svg", { class: "chart", viewBox: `0 0 ${W} ${H}`, role: "img", "aria-label": "Performance over time" });
    const iw = W - padL - padR, ih = H - padT - padB;
    for (let i = 0; i <= 4; i++) {
        const y = padT + (ih * i) / 4;
        root.append(svg("line", { class: "grid-line", x1: padL, x2: W - padR, y1: y, y2: y }));
        const lbl = svg("text", { class: "axis-label", x: padL - 6, y: y + 3, "text-anchor": "end" });
        lbl.textContent = (100 - i * 25) + "%";
        root.append(lbl);
    }
    if (points.length < 2) {
        const t = svg("text", { class: "axis-label", x: W / 2, y: H / 2, "text-anchor": "middle" });
        t.textContent = empty;
        root.append(t);
        return root;
    }
    const t0 = points[0].at, t1 = points[points.length - 1].at, span = Math.max(1, t1 - t0);
    const x = at => padL + ((at - t0) / span) * iw;
    // A gap is anything much longer than the usual spacing between points.
    const steps = points.slice(1).map((p, i) => p.at - points[i].at).sort((a, b) => a - b);
    const gap = Math.max(steps[Math.floor(steps.length / 2)] * 3, 1);

    // The right-hand labels are where each scale tops out (the top grid line) — not current values; the legend
    // above the chart has those. Each says so when hovered.
    const rightLabels = [];
    const ramMax = ramPercent ? 100 : Math.max(...points.map(p => p.ram || 0), 1) * 1.15;
    if (!ramPercent) rightLabels.push([ramMax >= 1024 ? (ramMax / 1024).toFixed(1) + " GB" : Math.round(ramMax) + " MB", "Top of the memory scale (not current use — that's in the legend above)"]);
    const slots = Math.max(...points.map(p => p.maxPlayers || 0), 0);
    const peak = Math.max(...points.map(p => p.players || 0), 0);
    const playerMax = players ? Math.max(slots, peak, 1) : 1;
    // NEXT: with no slot count and nobody ever on, the scale's top of 1 read as "1 pl." — as if someone had played.
    if (players && (slots > 0 || peak > 0)) {
        rightLabels.push([slots >= peak ? `${playerMax} slots` : `${playerMax} pl.`, slots >= peak ? `Top of the players scale: the server's ${slots} slots` : `Top of the players scale: the most players seen (${peak})`]);
    }
    rightLabels.forEach(([text, hint], i) => {
        const l = svg("text", { class: "axis-label", x: W - padR + 6, y: padT + 8 + i * 13 });
        l.textContent = text;
        const tip = svg("title");
        tip.textContent = hint;
        l.append(tip);
        root.append(l);
    });

    const series = (key, scaleMax, cls, area = true) => {
        let segment = [];
        const segments = [segment];
        let prev = null;
        for (const p of points) {
            if (p[key] == null) continue;
            if (prev && p.at - prev.at > gap) { segment = []; segments.push(segment); }
            segment.push([x(p.at), padT + ih - (Math.min(p[key], scaleMax) / scaleMax) * ih]);
            prev = p;
        }
        for (const c of segments) {
            if (c.length < 2) continue;
            const d = c.map((p, i) => (i ? "L" : "M") + p[0].toFixed(1) + " " + p[1].toFixed(1)).join(" ");
            if (area) root.append(svg("path", { class: "area-" + cls, d: `${d} L${c[c.length - 1][0]} ${padT + ih} L${c[0][0]} ${padT + ih} Z` }));
            root.append(svg("path", { class: "line-" + cls, d }));
        }
    };
    series("ram", ramMax, "ram");
    if (disk) series("disk", 100, "disk", false);
    series("cpu", 100, "cpu");
    if (players) series("players", playerMax, "players", false);

    const start = svg("text", { class: "axis-label", x: padL, y: H - 6 });
    start.textContent = agoLabel(Date.now() - t0);
    const end = svg("text", { class: "axis-label", x: W - padR, y: H - 6, "text-anchor": "end" });
    end.textContent = Date.now() - t1 < 10 * 60000 ? "now" : agoLabel(Date.now() - t1);
    root.append(start, end);
    return root;
}

function agoLabel(ms) {
    const mins = Math.round(ms / 60000);
    if (mins < 1) return "moments ago";
    if (mins < 90) return `${mins} min ago`;
    const hours = Math.round(mins / 60);
    if (hours < 48) return `${hours} h ago`;
    return `${Math.round(hours / 24)} days ago`;
}

// ─────────────────────────── Forms ───────────────────────────

export function field(label, control, { hint, id, span, help } = {}) {
    const fid = id || control.id || "f-" + Math.random().toString(36).slice(2);
    control.id = fid;
    const err = h("div", { class: "error", id: fid + "-error", hidden: true });
    const hintEl = hint ? h("div", { class: "hint", id: fid + "-hint", text: hint }) : null;
    const helpBtn = help ? helpButton(label, help) : null;
    const labelRow = help ? h("div", { class: "label-row" }, h("label", { for: fid, text: label }), helpBtn) : h("label", { for: fid, text: label });
    const el = h("div", { class: ["field", span && "span-all"] }, labelRow, helpBtn ? helpBtn.helpBox : null, control, hintEl, err);
    // Screen readers read the hint (and any error) with the field's name.
    const describe = withError => {
        const ids = [withError && err.id, hintEl && hintEl.id].filter(Boolean).join(" ");
        if (ids) control.setAttribute("aria-describedby", ids); else control.removeAttribute("aria-describedby");
    };
    describe(false);
    el.setError = (msg) => {
        err.textContent = msg || ""; err.hidden = !msg; el.classList.toggle("invalid", !!msg);
        if (msg) control.setAttribute("aria-invalid", "true"); else control.removeAttribute("aria-invalid");
        describe(!!msg);
    };
    return el;
}

export function input(props = {}) { return h("input", { class: ["input", props.mono && "mono"], ...props, mono: undefined }); }

export function select(options, value, props = {}) {
    const el = h("select", { class: "input", ...props });
    for (const o of options) {
        const opt = typeof o === "string" ? { value: o, label: o } : o;
        el.append(h("option", { value: opt.value, selected: String(opt.value) === String(value) }, opt.label));
    }
    return el;
}

export function toggle(label, checked, { hint, onChange, disabled, help } = {}) {
    const box = h("input", { type: "checkbox", checked, disabled, role: "switch" });
    if (onChange) box.addEventListener("change", () => onChange(box.checked));
    const el = h("label", { class: "switch" }, box, h("span", { class: "track" }), h("span", { class: "switch-text" }, h("span", { text: label }), hint ? h("small", { text: hint }) : null));
    el.input = box;
    if (!help) return el;
    // The ? sits beside the switch (not inside its label, where a click would flip it); its text opens below.
    const btn = helpButton(label, help);
    const wrap = h("div", { class: "switch-help" }, h("div", { class: "row switch-help-row" }, el, btn), btn.helpBox);
    wrap.input = box;
    return wrap;
}

export function segmented(options, value, onChange) {
    const el = h("div", { class: "segmented", role: "group" });
    const buttons = options.map(o => h("button", { type: "button", "aria-pressed": String(o.value === value), onclick: () => set(o.value, true) }, o.icon ? icon(o.icon) : null, o.label, o.count != null ? h("span", { class: "count", text: o.count }) : null));
    el.append(...buttons);
    function set(v, fire) {
        value = v;
        buttons.forEach((b, i) => b.setAttribute("aria-pressed", String(options[i].value === v)));
        if (fire && onChange) onChange(v);
    }
    el.set = v => set(v, false);
    el.setCount = (v, n) => { const i = options.findIndex(o => o.value === v); const c = buttons[i]?.querySelector(".count"); if (c) c.textContent = n; };
    return el;
}

/** Runs an async action with the button showing it's busy; errors become a toast. */
export async function busy(button, fn, errorTitle) {
    if (button) { button.classList.add("busy"); button.disabled = true; }
    try { return await fn(); }
    catch (e) { toastError(e, errorTitle); return undefined; }
    finally { if (button) { button.classList.remove("busy"); button.disabled = false; } }
}

export function empty(iconName, title, text, ...actions) {
    return h("div", { class: "empty" }, icon(iconName), h("h2", { text: title }), text ? h("p", { text }) : null, actions.length ? h("div", { class: "row wrap" }, actions) : null);
}

export function loading(text = "Loading…") {
    return h("div", { class: "empty" }, h("div", { class: "spinner lg" }), h("p", { text }));
}

export function btn(label, { icon: ic, kind = "", onClick, title, size = "", type = "button", disabled } = {}) {
    return h("button", { type, class: ["btn", kind, size, !label && "icon-only"], title: title || (label ? undefined : ""), "aria-label": label ? undefined : title, disabled, onclick: onClick }, ic ? icon(ic) : null, label || null);
}

export function tabs(items, active, onSelect) {
    const list = h("div", { class: "tabs", role: "tablist" });
    const buttons = items.map(t => h("button", { role: "tab", class: "tab", id: "tab-" + t.id, "aria-selected": String(t.id === active), tabindex: t.id === active ? "0" : "-1", onclick: () => onSelect(t.id) },
        t.icon ? icon(t.icon) : null, t.label, t.badge != null ? h("span", { class: "count-badge", text: t.badge }) : null));
    list.append(...buttons);
    list.addEventListener("keydown", e => {
        const i = buttons.indexOf(document.activeElement);
        if (i < 0) return;
        if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
            e.preventDefault();
            const next = buttons[(i + (e.key === "ArrowRight" ? 1 : -1) + buttons.length) % buttons.length];
            next.focus();
            next.click();
        }
    });
    list.select = id => buttons.forEach((b, i) => { const on = items[i].id === id; b.setAttribute("aria-selected", String(on)); b.tabIndex = on ? 0 : -1; });
    return list;
}

export { clear };

/**
 * A small "?" that opens a short explanation under it (tap or click; works on phones, unlike a hover title).
 * text: a string, or an array of paragraphs.
 */
export function helpButton(topic, text) {
    const box = h("div", { class: "help-pop", hidden: true, role: "note" }, ...(Array.isArray(text) ? text : [text]).map(t => h("p", { text: t })));
    const btn = h("button", { type: "button", class: "help-btn", "aria-label": `About ${topic}`, "aria-expanded": "false", onclick: e => {
        e.preventDefault();
        box.hidden = !box.hidden;
        btn.setAttribute("aria-expanded", String(!box.hidden));
        if (!box.hidden && !box.isConnected) btn.closest(".help-anchor, div")?.append(box);
    } }, "?");
    btn.helpBox = box; // field() places it under the label
    return btn;
}
