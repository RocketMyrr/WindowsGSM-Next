// Machines: every computer this panel controls. On a hub: add machines with a one-time pairing code, rename
// or remove them. On a machine that reports to a hub: where it reports, and leaving. A machine is one or the
// other — a hub, or a member of one hub.

import { h, icon, clear, append, timeAgo, copyText, timeUntil } from "../dom.js";
import { get, post, patch, del } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { navigate } from "../router.js";
import { modal, field, input, toast, toastError, busy, confirm, promptText, showMenu, meter, loading, metricsChart, segmented } from "../ui.js";
import { updatesPanel, updateAll } from "./updates.js";

/**
 * A machine dropdown for pages that work on one machine at a time (health, audit, install). Nothing when
 * there's only one machine. onlineOnly hides offline ones; only limits to a list of ids.
 */
export function machinePicker(value, onChange, { onlineOnly = false, only = null, label = "Machine" } = {}) {
    const list = [...store.machines.values()].filter(m => (!onlineOnly || m.online !== false || m.id === value) && (!only || only.includes(m.id)));
    if (list.length < 2) return null;
    const sel = h("select", { class: "input machine-picker", "aria-label": label },
        ...list.map(m => h("option", { value: m.id, selected: m.id === value }, m.name + (m.online === false ? " (offline)" : ""))));
    sel.addEventListener("change", () => onChange(sel.value));
    return h("label", { class: "machine-pick" }, icon("machine"), sel);
}

/** A machine's CPU, memory and disk over time, from its own history. */
export function machineHistoryDialog(m) {
    let range = "24h";
    const box = h("div", { class: "chart-box" }, loading());
    const legend = h("div", { class: "chart-legend" });
    const bar = segmented([{ value: "24h", label: "24h" }, { value: "7d", label: "7 days" }, { value: "30d", label: "30 days" }, { value: "1y", label: "Year" }], range, v => { range = v; load(); });
    bar.classList.add("sm");
    async function load() {
        clear(box).append(loading());
        const asked = range;
        let list;
        try { list = await get(`/machines/${encodeURIComponent(m.id)}/history?range=${asked}`); }
        catch (e) { clear(box).append(h("div", { class: "small faint", text: e.message })); return; }
        if (asked !== range) return;
        const points = list.map(p => ({ at: Date.parse(p.at), cpu: p.cpu, ram: p.ram, disk: p.disk }));
        clear(box).append(metricsChart(points, { ramPercent: true, disk: true, height: 220, empty: points.length ? "Recording started — more shows up every few minutes" : "No history for this period yet" }));
        const avg = k => { const v = points.map(p => p[k]).filter(n => n != null); return v.length ? Math.round(v.reduce((a, b) => a + b, 0) / v.length) + "%" : "—"; };
        const peak = k => { const v = points.map(p => p[k]).filter(n => n != null); return v.length ? Math.round(Math.max(...v)) + "%" : "—"; };
        clear(legend).append(
            h("span", { class: "legend cpu" }, h("i"), `CPU avg ${avg("cpu")} · peak ${peak("cpu")}`),
            h("span", { class: "legend ram" }, h("i"), `Memory avg ${avg("ram")}`),
            h("span", { class: "legend disk" }, h("i"), `Disk ${points.length ? peak("disk") : "—"}`));
    }
    modal({
        title: `${m.name} — performance`, iconName: "activity", wide: true,
        body: h("div", { class: "stack" }, h("div", { class: "row wrap" }, legend, h("span", { class: "spacer" }), bar), box),
        footer: close => [h("button", { class: "btn primary", onclick: () => close(true) }, "Close")],
    });
    load();
}

export default async function machines(host, { scope }) {
    setCrumbs({ label: "Machines" });
    if (!store.me.canManageUsers) {
        host.append(h("div", { class: "callout warn" }, icon("lock"), h("span", { text: "Only admins and owners can manage machines." })));
        return;
    }
    const owner = store.me.isOwner;
    const addButton = h("button", { class: "btn primary", onclick: () => addMachine() }, icon("plus"), "Add a machine");
    const linkPanel = h("div");
    const list = h("div", { class: "panel-body flush" });
    append(host, [
        h("div", { class: "page-head" },
            h("div", {}, h("h1", { text: "Machines" }), h("p", { text: "Run game servers on several computers and control them all from here." })),
            h("div", { class: "actions" }, owner ? h("button", { class: "btn", onclick: e => busy(e.currentTarget, updateAll, "Couldn't check for updates") }, icon("update"), "Update all") : null, owner ? addButton : null)),
        h("div", { class: "stack loose" },
            linkPanel,
            h("section", { class: "panel" },
                h("div", { class: "panel-head" }, h("h3", { text: "Controlled from this panel" })),
                list),
            howItWorks())]);

    let link = null; // this machine's own hub link (when it reports to a hub)
    let linkPainted = null; // what the link panel shows now — repainted only when that changes, so typing isn't lost

    async function loadLink() {
        try { link = await get("/link"); } catch { link = null; }
        paintLink();
    }

    function paintLink() {
        const member = link && link.joined;
        const mode = member ? `member:${link.state}:${link.lastError}` : owner && store.machines.size === 1 ? "join" : "none";
        if (mode === linkPainted) return;
        linkPainted = mode;
        clear(linkPanel);
        addButton.hidden = !!member;
        if (member) {
            const state = { Connected: ["good", "checkCircle", "Connected"], Connecting: ["info", "refresh", "Connecting…"], Offline: ["warn", "warn", "Can't reach the hub — retrying"] }[link.state] || ["warn", "warn", link.state];
            linkPanel.append(h("section", { class: "panel member-panel" },
                h("div", { class: "panel-head" }, h("h3", { text: "This machine reports to a hub" })),
                h("div", { class: "panel-body stack" },
                    h("div", { class: ["callout", state[0]] }, icon(state[1]), h("div", { class: "grow" },
                        h("b", { text: `${state[2]} · ${link.hubName || "Hub"}` }),
                        h("div", { class: "small muted mono", text: link.hubUrl }),
                        link.state === "Connected" && link.connectedSince ? h("div", { class: "tiny faint", text: "Since " + timeAgo(link.connectedSince) }) : null,
                        link.state !== "Connected" && link.lastError ? h("div", { class: "small", text: link.lastError }) : null)),
                    h("p", { class: "small muted", text: "People signed in to the hub can see and control this machine's servers, with the permissions the hub gives them. You can still sign in here directly." }),
                    link.pinnedCertificate ? h("p", { class: "tiny faint", text: "The hub's certificate is pinned: " + link.pinnedCertificate.slice(0, 16) + "…" }) : null,
                    owner ? h("div", { class: "row" }, h("span", { class: "spacer" }), h("button", { class: "btn danger", onclick: e => leave(e.currentTarget) }, icon("logout"), "Leave the hub")) : null)));
        } else if (owner && store.machines.size === 1) {
            linkPanel.append(joinPanel());
        }
        linkPanel.hidden = !linkPanel.firstChild;
    }

    function paintList() {
        clear(list);
        const all = [...store.machines.values()];
        for (const m of all) list.append(machineRow(m));
        if (all.length === 1) list.append(h("div", { class: "list-item small faint" }, icon("info"), h("span", { text: link?.joined ? "Other machines are added on the hub." : "Only this machine so far. Add another to see its servers here too." })));
    }

    function machineRow(m) {
        const on = m.online !== false;
        const mm = on ? m.metrics : null;
        const status = m.isLocal ? h("span", { class: "tag accent", text: "This machine" })
            : on ? h("span", { class: "tag green" }, h("span", { class: "dot st-running" }), "Online")
                : h("span", { class: "tag rose" }, h("span", { class: "dot st-bad" }), "Offline");
        return h("div", { class: ["list-item machine-row", !on && "offline"] },
            h("span", { class: "machine-icon" }, icon("machine")),
            h("div", { class: "grow machine-row-main" },
                h("div", { class: "row wrap" }, h("b", { class: "truncate", text: m.name }), status),
                h("div", { class: "small muted" }, [
                    `${m.serverCount} server${m.serverCount === 1 ? "" : "s"}`,
                    m.version ? "v" + m.version.replace(/^v/i, "") : null,
                    !on && m.lastSeen ? "last seen " + timeAgo(m.lastSeen) : null,
                    m.isLocal ? null : m.id,
                ].filter(Boolean).join(" · "))),
            mm ? h("div", { class: "machine-row-meters" },
                meter("CPU", mm.cpuPercent, `${Math.round(mm.cpuPercent)}%`, "cpu"),
                meter("Memory", mm.ramPercent, `${Math.round(mm.ramPercent)}%`, "ram"),
                meter("Disk", mm.diskPercent, `${Math.round(mm.diskPercent)}%`, "disk")) : null,
            h("button", { class: "btn ghost sm icon-only", "aria-label": `Actions for ${m.name}`, "aria-haspopup": "menu", onclick: e => showMenu(e.currentTarget, [
                { label: "Show its servers", icon: "grid", onClick: () => navigate(store.multiMachine ? "/?machine=" + encodeURIComponent(m.id) : "/") },
                { label: "Performance history", icon: "activity", disabled: !on, onClick: () => machineHistoryDialog(m) },
                { label: "Updates…", icon: "update", disabled: !on, onClick: () => modal({ title: `${m.name} — updates`, iconName: "update", wide: true, body: updatesPanel(m.id, null), footer: close => [h("button", { class: "btn primary", onclick: () => close(true) }, "Close")] }) },
                { label: "Health checks", icon: "checkCircle", disabled: !on, onClick: () => navigate("/health?machine=" + encodeURIComponent(m.id)) },
                { label: "Logs", icon: "logs", disabled: !on, onClick: () => navigate("/logs?machine=" + encodeURIComponent(m.id)) },
                ...(m.isLocal || !owner ? [] : ["-",
                    { label: "Rename…", icon: "pencil", onClick: () => rename(m) },
                    { label: "Remove…", icon: "trash", danger: true, onClick: () => remove(m) }]),
            ]) }, icon("more")));
    }

    async function rename(m) {
        const name = await promptText({ title: `Rename ${m.name}`, label: "Name", value: m.name, confirmLabel: "Rename",
            validate: v => !v.trim() ? "Give it a name." : v.trim().length > 60 ? "Up to 60 characters." : null });
        if (!name) return;
        try { await patch(`/hub/machines/${encodeURIComponent(m.id)}`, { name: name.trim() }); await store.refreshMachines(); toast("Renamed", { type: "good", text: name.trim() }); }
        catch (e) { toastError(e, "Couldn't rename it"); }
    }

    async function remove(m) {
        const ok = await confirm({
            title: `Remove ${m.name}?`,
            message: "It stops reporting to this panel and forgets it (if it's online now; otherwise the next time it connects). Its game servers keep running and nothing is deleted from it. To add it back, pair it again.",
            confirmLabel: "Remove machine", danger: true, iconName: "trash",
        });
        if (!ok) return;
        try {
            await del(`/hub/machines/${encodeURIComponent(m.id)}`);
            for (const k of [...store.servers.keys()]) if (k.startsWith(m.id + "/")) store.servers.delete(k);
            await store.refreshMachines();
            store.emit("servers");
            toast("Machine removed", { type: "good", text: m.name });
        } catch (e) { toastError(e, "Couldn't remove it"); }
    }

    async function leave(button) {
        const ok = await confirm({
            title: `Leave ${link.hubName || "the hub"}?`,
            message: "The hub stops showing this machine and its servers. Nothing here changes — the servers keep running and you can still sign in here. To join again you'll need a new pairing code.",
            confirmLabel: "Leave hub", danger: true, iconName: "logout",
        });
        if (!ok) return;
        await busy(button, async () => { await del("/link"); toast("Left the hub", { type: "good" }); await loadLink(); }, "Couldn't leave the hub");
    }

    // ── Adding a machine (hub side): a one-time code the other machine enters ──

    async function addMachine() {
        let res;
        try { res = await post("/hub/pairing-codes"); } catch (e) { toastError(e, "Couldn't create a pairing code"); return; }
        const known = new Set(store.machines.keys());
        const countdown = h("span");
        const status = h("div", { class: "pair-wait" }, h("span", { class: "spinner" }), h("span", { text: "Waiting for the other machine…" }));
        const tick = () => { countdown.textContent = Date.parse(res.expiresAt) > Date.now() ? `Expires ${timeUntil(res.expiresAt)}` : "Expired — close this and create a new one"; };
        tick();
        const timer = setInterval(tick, 15000);
        const off = store.on("machines", () => {
            const added = [...store.machines.values()].find(m => !known.has(m.id));
            if (!added) return;
            clear(status).append(icon("checkCircle"), h("b", { text: `${added.name} joined.` }), h("span", { class: "muted", text: " Its servers now show on the overview." }));
            status.classList.add("done");
        });
        const addresses = res.hubUrls.map(u => h("div", { class: "pair-address" }, h("code", { class: "grow", text: u }),
            h("button", { class: "btn ghost sm icon-only", "aria-label": "Copy address", onclick: async () => { if (await copyText(u)) toast("Copied", { type: "good", text: u, timeout: 2000 }); } }, icon("copy"))));
        modal({
            title: "Add a machine", iconName: "machine", wide: true,
            onClose: () => { clearInterval(timer); off(); },
            body: h("div", { class: "stack" },
                res.reachable ? null : h("div", { class: "callout warn" }, icon("warn"), h("div", {},
                    h("b", { text: "Other computers can't reach this one yet." }),
                    h("div", { class: "small" }, "The agent only listens on this computer. Turn on ", h("a", { href: "/settings" }, "Reachable from other computers"), " in Agent settings, then restart the agent."))),
                h("ol", { class: "pair-steps" },
                    h("li", {}, h("b", { text: "Install WindowsGSM on the other machine" }), h("span", { class: "small muted", text: "Set it up and sign in to its panel." })),
                    h("li", {}, h("b", { text: "Open Machines → Join a hub" }), h("span", { class: "small muted", text: "Enter one of this machine's addresses:" }), h("div", { class: "pair-addresses" }, ...addresses)),
                    h("li", {}, h("b", { text: "Enter this code" }),
                        h("div", { class: "pair-code" }, h("span", { class: "mono", text: res.code }),
                            h("button", { class: "btn ghost sm", onclick: async () => { if (await copyText(res.code)) toast("Code copied", { type: "good", timeout: 2000 }); } }, icon("copy"), "Copy")),
                        h("span", { class: "tiny faint" }, "Works once. ", countdown))),
                status,
                h("p", { class: "tiny faint", text: "The other machine connects out to this one, so only this machine needs to be reachable. Game servers keep running on their own machine." })),
            footer: close => [h("button", { class: "btn primary", onclick: () => close(true) }, "Done")],
        });
    }

    // ── Joining a hub (member side) ──

    function joinPanel() {
        const url = input({ class: "input mono", placeholder: "http://game-box:8971", autocomplete: "off", spellcheck: "false" });
        const code = input({ class: "input mono pair-code-input", placeholder: "ABCD-EFGH", autocomplete: "off", spellcheck: "false", maxlength: 9 });
        const urlField = field("Hub address", url, { help: "The address of the PC that's the hub, as this machine can reach it — e.g. https://192.168.1.20:8971. On the hub: Machines → Add a machine shows it with a pairing code.", hint: "Shown on the hub when you add a machine there." });
        const codeField = field("Pairing code", code, { help: "A one-time code from the hub (Machines → Add a machine). It works for 15 minutes; make a new one if it runs out." });
        const join = h("button", { class: "btn primary", onclick: () => busy(join, async () => {
            urlField.setError(""); codeField.setError("");
            if (!/^https?:\/\/\S+$/i.test(url.value.trim())) { urlField.setError("Enter the hub's address, starting with http:// or https://"); return; }
            if (code.value.replace(/[^a-z0-9]/gi, "").length !== 8) { codeField.setError("The code has 8 letters and numbers."); return; }
            try { await post("/link", { hubUrl: url.value.trim(), code: code.value.trim() }); }
            catch (e) { codeField.setError(e.message); return; }
            toast("Joined the hub", { type: "good", text: "This machine's servers now show there too." });
            await loadLink();
        }) }, icon("link"), "Join");
        code.addEventListener("input", () => {
            const raw = code.value.toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 8);
            code.value = raw.length > 4 ? raw.slice(0, 4) + "-" + raw.slice(4) : raw;
        });
        code.addEventListener("keydown", e => { if (e.key === "Enter") join.click(); });
        const body = h("div", { class: "panel-body stack", hidden: true },
            h("p", { class: "small muted", text: "Let another WindowsGSM machine (the hub) control this one. On the hub, go to Machines → Add a machine to get an address and a code." }),
            h("div", { class: "form-grid" }, urlField, codeField),
            h("div", { class: "row" }, h("span", { class: "spacer" }), join));
        const toggleButton = h("button", { class: "btn ghost sm", "aria-expanded": "false", onclick: () => {
            body.hidden = !body.hidden;
            toggleButton.setAttribute("aria-expanded", String(!body.hidden));
            toggleButton.lastChild.textContent = body.hidden ? "Join a hub" : "Hide";
            if (!body.hidden) url.focus();
        } }, icon("link"), h("span", { text: "Join a hub" }));
        return h("section", { class: "panel" },
            h("div", { class: "panel-head" }, h("h3", { text: "Control this machine from another one" }), h("span", { class: "spacer" }), toggleButton),
            body);
    }

    function howItWorks() {
        return h("section", { class: "panel subtle" },
            h("div", { class: "panel-head" }, h("h3", { text: "How it works" })),
            h("div", { class: "panel-body how-grid" },
                how("machine", "One panel, many machines", "Pick one machine as the hub. Others join it and show up here with their servers."),
                how("shield", "Your permissions follow you", "What people can do on each machine's servers is set here, on the hub, under Users & access."),
                how("globe", "Only the hub needs to be reachable", "Members connect out to the hub, so they need no open ports of their own."),
                how("zap", "Servers never depend on the hub", "If a machine goes offline, its servers keep running. The hub shows the last known state until it's back.")));
    }

    list.append(loading());
    scope.add(store.on("machines", () => { paintList(); paintLink(); }));
    await Promise.all([loadLink(), store.refreshMachines().catch(() => { })]);
    paintList();
    scope.every(5000, async () => {
        await store.refreshMachines();
        if (link?.joined) { const before = link.state; await loadLink(); if (link.state !== before) paintList(); }
    }, { now: false });
}

function how(ic, title, text) {
    return h("div", { class: "how" }, icon(ic), h("div", {}, h("b", { text: title }), h("div", { class: "small muted", text })));
}
