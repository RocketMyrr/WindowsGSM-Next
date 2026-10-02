// Config history: earlier versions of a server's files (kept whenever the panel changes them), what changed
// since, and "Put back" — which keeps the current version too, so it can be undone the same way.

import { h, icon, clear, append, timeAgo, fmtDateTime, fmtBytes } from "../../dom.js";
import { get, post, srv } from "../../api.js";
import { modal, toast, toastError, confirm, empty, loading, busy } from "../../ui.js";

/**
 * Opens the history of one file (path: "settings" or a path inside serverfiles), or of every file (no path).
 * onRestored() runs after a version is put back (to reload what's on screen).
 */
export function historyDialog({ machine, id, path = null, title = "Config history", onRestored }) {
    const list = h("div", { class: "hist-list" });
    const view = h("div", { class: "hist-view" }, empty("clock", "Pick a version", "Its changes since then show here."));
    let versions = [];
    let close;

    async function load() {
        clear(list).append(loading());
        try { versions = await get(srv(machine, id, "/config-history" + (path ? "?path=" + encodeURIComponent(path) : ""))); }
        catch (e) { clear(list).append(empty("warn", "Couldn't load the history", e.message)); return; }
        clear(list);
        if (!versions.length) {
            list.append(empty("clock", "No earlier versions yet", "From now on, every change made here keeps the version it replaced."));
            return;
        }
        for (const v of versions) {
            const b = h("button", { type: "button", class: "hist-item", onclick: () => show(v, b) },
                h("b", { class: "truncate", text: path ? whenText(v) : label(v.path) }),
                h("span", { class: "tiny faint truncate", text: [path ? null : whenText(v), v.by, v.why].filter(Boolean).join(" · ") }));
            list.append(b);
        }
        list.querySelector(".hist-item")?.click();
    }

    async function show(v, button) {
        list.querySelectorAll(".hist-item").forEach(x => x.setAttribute("aria-current", String(x === button)));
        clear(view).append(loading());
        let r;
        try { r = await get(srv(machine, id, `/config-history/${encodeURIComponent(v.id)}`)); }
        catch (e) { clear(view).append(empty("warn", "Couldn't open that version", e.message)); return; }
        const put = h("button", { class: "btn primary sm", onclick: () => restore(v, put) }, icon("restore"), "Put this version back");
        const diff = r.current == null ? null : lineDiff(r.content, r.current);
        append(clear(view), [
            h("div", { class: "hist-head row wrap" },
                h("div", { class: "grow" }, h("b", { text: label(v.path) }),
                    h("div", { class: "small muted", text: `${fmtDateTime(v.at)} · ${v.by || "unknown"} · replaced by ${v.why.toLowerCase()} · ${fmtBytes(v.size)}` })),
                put),
            r.current == null ? h("div", { class: "callout warn" }, icon("warn"), h("span", { text: "The file isn't there any more — putting this version back creates it again." })) : null,
            diff && !diff.changed ? h("div", { class: "callout info" }, icon("info"), h("span", { text: "Same as the file now." })) : null,
            diff ? h("div", { class: "small muted hist-legend" }, h("span", { class: "diff-del", text: "− this version" }), h("span", { class: "diff-add", text: "+ now" })) : null,
            h("pre", { class: "hist-diff mono" }, ...(diff ? diff.lines : [h("span", { text: r.content })]))]);
    }

    async function restore(v, button) {
        if (!(await confirm({ title: `Put back ${label(v.path)} from ${whenText(v)}?`, message: "The current version is kept in this history too, so you can switch back. A running server may need a restart to pick it up.", confirmLabel: "Put it back", iconName: "restore" }))) return;
        await busy(button, async () => {
            await post(srv(machine, id, `/config-history/${encodeURIComponent(v.id)}/restore`));
            toast("Version put back", { type: "good", text: label(v.path) });
            onRestored?.();
            close?.(true);
        }, "Couldn't put it back");
    }

    const m = modal({
        title, subtitle: path ? label(path) : "Every file changed from this panel", iconName: "clock", wide: true,
        body: c => { close = c; return h("div", { class: "hist-layout" }, list, view); },
    });
    load();
    return m;
}

const label = p => p === "settings" ? "Server settings" : p;
const whenText = v => `${timeAgo(v.at)} (${new Date(v.at).toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" })})`;

/**
 * Line diff of two texts (longest common subsequence), as spans for a <pre>: unchanged lines plain, removed
 * ones "−", added ones "+". Long runs of unchanged lines fold to a few lines of context.
 */
export function lineDiff(before, after) {
    const a = before.replace(/\r\n/g, "\n").split("\n"), b = after.replace(/\r\n/g, "\n").split("\n");
    if (a.length * b.length > 4_000_000) {
        // Too big to compare line by line in the browser: show both halves.
        return { changed: before !== after, lines: [h("span", { class: "diff-del", text: before + "\n" }), h("span", { class: "diff-add", text: after })] };
    }
    const n = a.length, m = b.length;
    const lcs = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
    for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
    const ops = [];
    let i = 0, j = 0;
    while (i < n || j < m) {
        if (i < n && j < m && a[i] === b[j]) { ops.push([" ", a[i]]); i++; j++; }
        else if (j < m && (i >= n || lcs[i][j + 1] >= lcs[i + 1][j])) { ops.push(["+", b[j]]); j++; }
        else { ops.push(["-", a[i]]); i++; }
    }
    const changed = ops.some(o => o[0] !== " ");
    const lines = [];
    const CONTEXT = 3;
    for (let k = 0; k < ops.length; k++) {
        const [op, text] = ops[k];
        if (op === " " && changed) {
            const near = ops.slice(Math.max(0, k - CONTEXT), k + CONTEXT + 1).some(o => o[0] !== " ");
            if (!near) {
                let end = k;
                while (end < ops.length && ops[end][0] === " " && !ops.slice(end + 1, end + CONTEXT + 1).some(o => o[0] !== " ")) end++;
                if (end - k > 1) { lines.push(h("span", { class: "diff-fold", text: `… ${end - k} unchanged line${end - k === 1 ? "" : "s"} …\n` })); k = end - 1; continue; }
            }
        }
        lines.push(h("span", { class: op === "+" ? "diff-add" : op === "-" ? "diff-del" : "", text: `${op === " " ? " " : op === "+" ? "+" : "−"} ${text}\n` }));
    }
    return { changed, lines };
}
