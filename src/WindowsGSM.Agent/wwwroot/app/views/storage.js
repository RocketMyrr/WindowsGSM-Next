// Storage: how full each drive is, what each server takes (files, backups, logs), and leftovers that can go
// safely — old logs, game crash dumps, unfinished restores, old app versions, picture caches.

import { h, icon, clear, append, fmtBytes, timeAgo } from "../dom.js";
import { get, post } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { serverPath } from "../router.js";
import { empty, loading, busy, confirm, toast } from "../ui.js";
import { machinePicker } from "./machines.js";

export default async function storage(host, { query, scope }) {
    setCrumbs({ label: "Storage" });
    if (!store.me.canManageUsers) {
        host.append(h("div", { class: "callout warn" }, icon("lock"), h("span", { text: "Only admins and owners can see storage." })));
        return;
    }
    let machine = store.machines.has(query.get("machine")) && store.isOnline(query.get("machine")) ? query.get("machine") : store.localId;
    const base = () => `/machines/${encodeURIComponent(machine)}/disk`;
    const drives = h("div", { class: "drive-list" });
    const servers = h("div", { class: "panel-body flush table-wrap" });
    const cleanup = h("div", { class: "panel-body flush" });
    const scanned = h("span", { class: "small faint" });
    const rescan = h("button", { class: "btn", onclick: () => busy(rescan, () => load(true)) }, icon("refresh"), "Scan again");
    const picker = machinePicker(machine, m => { machine = m; history.replaceState({}, "", "/storage?machine=" + encodeURIComponent(m)); load(false); }, { onlineOnly: true });

    append(host, [
        h("div", { class: "page-head" },
            h("div", {}, h("h1", { text: "Storage" }), h("p", { text: "What takes space on this machine, and what can safely go." })),
            h("div", { class: "actions" }, picker, rescan)),
        drives,
        h("div", { class: "storage-layout" },
            h("section", { class: "panel" }, h("div", { class: "panel-head" }, h("h3", { text: "Clean up" }), h("span", { class: "spacer" }), scanned), cleanup),
            h("section", { class: "panel" }, h("div", { class: "panel-head" }, h("h3", { text: "By server" })), servers))]);

    async function load(fresh) {
        clear(cleanup).append(loading("Measuring… (big servers take a few seconds)"));
        let r;
        try { r = await get(base() + (fresh ? "?fresh=true" : "")); }
        catch (e) { clear(cleanup).append(empty("warn", "Couldn't measure", e.message)); return; }
        if (!scope.alive) return;
        scanned.textContent = "measured " + timeAgo(r.at);

        clear(drives).append(...r.drives.map(d => {
            const used = d.total - d.free, pct = d.total ? Math.round(used / d.total * 100) : 0;
            const bar = h("span");
            bar.style.setProperty("width", pct + "%");
            return h("div", { class: ["drive", pct >= 90 ? "full" : pct >= 80 ? "tight" : ""] },
                icon("disk"), h("div", { class: "grow" },
                    h("div", { class: "row between" }, h("b", { text: d.name }), h("span", { class: "small muted", text: `${fmtBytes(d.free)} free of ${fmtBytes(d.total)}` })),
                    h("div", { class: "drive-bar" }, bar)));
        }));

        // Servers, biggest first.
        clear(servers);
        if (!r.servers.length) servers.append(empty("servers", "No servers", ""));
        else servers.append(h("table", { class: "table" },
            h("thead", {}, h("tr", {}, h("th", { text: "Server" }), h("th", { class: "num", text: "Files" }), h("th", { class: "num", text: "Backups" }), h("th", { class: "num hide-sm", text: "Logs" }))),
            h("tbody", {}, ...r.servers.map(s => h("tr", {},
                h("td", {}, h("a", { href: serverPath(machine, s.id), text: s.name })),
                h("td", { class: "num", text: fmtBytes(s.files) }),
                h("td", { class: "num", text: fmtBytes(s.backups) }),
                h("td", { class: "num muted hide-sm", text: fmtBytes(s.logs) }))))));

        // Clean-up: suggested ones ticked; the button says how much goes.
        clear(cleanup);
        const chosen = new Set(r.cleanup.filter(c => c.suggested).map(c => c.key));
        const go = h("button", { class: "btn primary" });
        const paintGo = () => {
            const bytes = r.cleanup.filter(c => chosen.has(c.key)).reduce((a, c) => a + c.bytes, 0);
            go.disabled = bytes === 0;
            clear(go).append(icon("trash"), bytes ? `Free ${fmtBytes(bytes)}` : "Nothing chosen");
        };
        go.onclick = () => clean([...chosen], go);
        for (const c of r.cleanup) {
            const box = h("input", { type: "checkbox", checked: chosen.has(c.key), disabled: c.count === 0 });
            box.addEventListener("change", () => { if (box.checked) chosen.add(c.key); else chosen.delete(c.key); paintGo(); });
            cleanup.append(h("label", { class: ["list-item cleanup-row", c.count === 0 && "disabled"] }, box,
                h("div", { class: "grow" }, h("b", { text: c.label }), h("div", { class: "small muted", text: c.description })),
                h("div", { class: "cleanup-size" }, h("b", { class: "num", text: c.count ? fmtBytes(c.bytes) : "—" }), h("span", { class: "tiny faint", text: c.count ? `${c.count} item${c.count === 1 ? "" : "s"}` : "nothing" }))));
        }
        cleanup.append(h("div", { class: "row cleanup-foot" }, h("span", { class: "small muted grow", text: "Backups you keep, server files and anything a busy server is using are never touched." }), go));
        paintGo();
    }

    async function clean(keys, button) {
        if (!(await confirm({ title: "Clean up?", message: "The chosen files are deleted for good.", confirmLabel: "Delete them", danger: true, iconName: "trash" }))) return;
        await busy(button, async () => {
            const r = await post(base() + "/cleanup", { keys });
            toast(`Freed ${fmtBytes(r.freed)}`, { type: r.failed.length ? "warn" : "good", text: r.failed.length ? `${r.failed.length} item(s) were in use and stayed.` : `${r.files} item(s) removed.` });
            await load(true);
        }, "Couldn't clean up");
    }

    await load(false);
}
