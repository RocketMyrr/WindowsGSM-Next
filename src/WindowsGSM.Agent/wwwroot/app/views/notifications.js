// Notifications: everything worth knowing that happened (crashes, restarts, updates, failed jobs, machines
// dropping off), and — for owners — where else to send it: Discord channels or any webhook.

import { h, icon, clear, append, timeAgo } from "../dom.js";
import { get, post, put, del } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { modal, field, input, toggle, toast, toastError, busy, confirm, segmented, empty, loading, showMenu } from "../ui.js";
import { notificationItem, desktopSupported, desktopEnabled, setDesktop, inDesktopApp } from "../notify.js";
import { machinePicker } from "./machines.js";

const PRESETS = {
    problems: ["crashed", "crashLoop", "memoryGuard", "jobFailed", "appCrash", "machineOffline", "machineOnline"],
};

export default async function notifications(host, { scope }) {
    setCrumbs({ label: "Notifications" });
    const admin = !!store.me.canManageUsers;
    const owner = !!store.me.isOwner;
    let filter = "all";
    let machine = "";
    let items = [];
    const readAtOpen = store.readUpTo;

    const list = h("div", { class: "notif-list page" });
    const filterBar = segmented([{ value: "all", label: "All" }, { value: "problems", label: "Problems" }, { value: "unread", label: "New" }], filter, v => { filter = v; paintList(); });
    const picker = machinePicker("", m => { machine = m; paintList(); }, { label: "Machine" });
    if (picker) picker.querySelector("select").prepend(h("option", { value: "", selected: true }, "All machines"));
    const channelsBody = h("div", { class: "panel-body flush" });

    append(host, [
        h("div", { class: "page-head" },
            h("div", {}, h("h1", { text: "Notifications" }), h("p", { text: "What happened while you weren't looking — and where else to send it." }))),
        h("div", { class: "notif-layout" },
            h("section", { class: "panel" },
                h("div", { class: "panel-head wrap" }, h("h3", { class: "grow", text: "History" }), picker, filterBar),
                list),
            h("div", { class: "stack loose" },
                devicePanel(),
                admin ? h("section", { class: "panel" },
                    h("div", { class: "panel-head" }, h("h3", { class: "grow", text: "Send to Discord or a webhook" }),
                        owner ? h("button", { class: "btn sm primary", onclick: () => editChannel(null) }, icon("plus"), "Add") : null),
                    channelsBody) : null))]);

    // ── History ──

    async function loadList() {
        clear(list).append(loading());
        try { items = (await get("/notifications?limit=500")).items; }
        catch (e) { clear(list).append(empty("warn", "Couldn't load notifications", e.message)); return; }
        paintList();
        store.markNotificationsRead();
    }

    function paintList() {
        const shown = items.filter(n => (filter !== "problems" || n.severity === "bad" || n.severity === "warn")
            && (filter !== "unread" || n.id > readAtOpen) && (!machine || n.machine === machine));
        clear(list);
        if (!shown.length) {
            list.append(items.length
                ? empty("filter", "Nothing matches", "Try another filter.")
                : empty("bell", "All quiet", "Crashes, restarts, updates, failed jobs and machines going offline show up here."));
            return;
        }
        let day = null;
        for (const n of shown) {
            const d = new Date(n.at).toDateString();
            if (d !== day) { day = d; list.append(h("div", { class: "notif-day", text: dayLabel(n.at) })); }
            list.append(notificationItem(n, { unread: n.id > readAtOpen }));
        }
    }

    // New ones arrive live at the top.
    scope.add(store.on("notificationAdded", n => {
        if (items.some(x => x.id === n.id)) return;
        items.unshift(n);
        paintList();
        store.markNotificationsRead();
    }));

    // ── This device ──

    function devicePanel() {
        if (inDesktopApp) {
            return h("section", { class: "panel" }, h("div", { class: "panel-head" }, h("h3", { text: "This computer" })),
                h("div", { class: "panel-body small muted", text: "The WindowsGSM app shows these as Windows notifications while its window is hidden. Turn them on or off from its tray icon's menu." }));
        }
        const supported = desktopSupported();
        const t = toggle("Pop up on this computer", desktopEnabled(), {
            hint: supported ? "When the panel is open in a background tab. Asks your browser's permission." : "This browser can't show desktop notifications.",
            disabled: !supported,
            onChange: async on => {
                const now = await setDesktop(on);
                t.input.checked = now;
                if (on && !now) toast("Notifications are blocked", { type: "warn", text: "Allow them for this site in your browser's settings, then try again." });
            },
        });
        return h("section", { class: "panel" }, h("div", { class: "panel-head" }, h("h3", { text: "This device" })), h("div", { class: "panel-body" }, t));
    }

    // ── Channels ──

    let kinds = [];
    let channels = [];

    async function loadChannels() {
        if (!admin) return;
        clear(channelsBody).append(loading());
        try { [kinds, channels] = await Promise.all([get("/notifications/kinds"), get("/notifications/channels")]); }
        catch (e) { clear(channelsBody).append(empty("warn", "Couldn't load channels", e.message)); return; }
        paintChannels();
    }

    function paintChannels() {
        clear(channelsBody);
        if (!channels.length) {
            channelsBody.append(h("div", { class: "empty compact" }, icon("message"),
                h("p", { text: owner ? "Get crashes and outages in a Discord channel, or post them to any service that takes a webhook." : "No channels yet. Owners can add one." })));
            return;
        }
        for (const c of channels) channelsBody.append(channelRow(c));
    }

    function channelRow(c) {
        const status = !c.enabled ? h("span", { class: "tag", text: "Paused" })
            : c.lastError ? h("span", { class: "tag rose", title: c.lastError }, icon("warn"), "Failing")
                : c.lastSentAt ? h("span", { class: "tiny faint", text: "Last sent " + timeAgo(c.lastSentAt) }) : h("span", { class: "tiny faint", text: "Nothing sent yet" });
        return h("div", { class: ["list-item channel-row", !c.enabled && "paused"] },
            h("span", { class: ["channel-icon", c.type] }, icon(c.type === "discord" ? "message" : "send")),
            h("div", { class: "grow channel-main" },
                h("div", { class: "row wrap" }, h("b", { class: "truncate", text: c.name }), status),
                h("div", { class: "small muted truncate", text: `${eventsSummary(c.events)} · ${scopeSummary(c.scope)}` }),
                c.lastError && c.enabled ? h("div", { class: "tiny rose-text truncate", text: c.lastError }) : null),
            owner ? h("button", { class: "btn ghost sm icon-only", "aria-label": `Actions for ${c.name}`, "aria-haspopup": "menu", onclick: e => showMenu(e.currentTarget, [
                { label: "Send a test", icon: "send", onClick: () => test(c) },
                { label: "Edit…", icon: "pencil", onClick: () => editChannel(c) },
                { label: c.enabled ? "Pause" : "Resume", icon: c.enabled ? "stop" : "play", onClick: () => setEnabled(c, !c.enabled) },
                "-",
                { label: "Remove…", icon: "trash", danger: true, onClick: () => remove(c) },
            ]) }, icon("more")) : null);
    }

    const eventsSummary = ev => {
        if (kinds.length && ev.length === kinds.length) return "Everything";
        if (ev.length === PRESETS.problems.length && PRESETS.problems.every(k => ev.includes(k))) return "Problems and outages";
        return ev.length === 1 ? (kinds.find(k => k.id === ev[0])?.label || ev[0]) : `${ev.length} kinds of event`;
    };
    const scopeSummary = sc => {
        if (!sc.length) return store.multiMachine ? "all machines" : "all servers";
        const names = sc.map(s => {
            const [m, id] = s.split("/");
            return id === "*" ? store.machineName(m) : store.server(m, id)?.name || `#${id}`;
        });
        return names.length <= 2 ? names.join(", ") : `${names.length} selected`;
    };

    async function test(c) {
        try { await post(`/notifications/channels/${c.id}/test`); toast("Test sent", { type: "good", text: `Check ${c.name}.` }); }
        catch (e) { toastError(e, "The test didn't arrive"); }
        loadChannels();
    }

    async function setEnabled(c, enabled) {
        try { await put(`/notifications/channels/${c.id}`, { ...c, url: "", enabled }); loadChannels(); }
        catch (e) { toastError(e); }
    }

    async function remove(c) {
        if (!(await confirm({ title: `Remove ${c.name}?`, message: "Nothing more is sent there. You can add it again any time.", confirmLabel: "Remove", danger: true, iconName: "trash" }))) return;
        try { await del(`/notifications/channels/${c.id}`); loadChannels(); } catch (e) { toastError(e); }
    }

    function editChannel(c) {
        const isNew = !c;
        let type = c?.type || "discord";
        const name = input({ value: c?.name || "", maxlength: 60, placeholder: "#server-alerts" });
        const url = input({ class: "input mono", value: "", autocomplete: "off", spellcheck: "false", placeholder: isNew ? "https://discord.com/api/webhooks/…" : `Unchanged (${c.url})` });
        const mention = input({ value: c?.mention || "", placeholder: "@here or <@&role id>" });
        const enabled = toggle("On", c ? c.enabled : true);
        const nameField = field("Name", name, { help: "Just for you — e.g. #server-alerts or Phone." });
        const urlField = field("Webhook URL", url, { span: true, help: ["Discord: Server settings → Integrations → Webhooks → New webhook → Copy webhook URL. Slack, Teams and other services that accept a webhook work too.", "It's a secret (anyone with it can post), so it's stored encrypted and only shown shortened. Leave it empty when editing to keep the current one."] });
        const mentionField = field("Mention", mention, { hint: "Optional. Put before each message so the right people get pinged.", help: "For Discord: @here, @everyone, or a role as <@&role id> (Developer Mode on, right-click the role → Copy role ID)." });
        const typeSeg = segmented([{ value: "discord", label: "Discord", icon: "message" }, { value: "webhook", label: "Webhook", icon: "send" }], type, v => { type = v; paintType(); });
        const urlHint = h("div", { class: "hint" });
        urlField.append(urlHint);
        function paintType() {
            mentionField.hidden = type !== "discord";
            url.placeholder = isNew ? (type === "discord" ? "https://discord.com/api/webhooks/…" : "https://example.com/hooks/windowsgsm") : `Unchanged (${c.url})`;
            urlHint.textContent = type === "discord"
                ? "In Discord: Server settings → Integrations → Webhooks → New webhook → Copy webhook URL."
                : "Gets a JSON POST: kind, severity, title, text, machine, server and time.";
        }
        paintType();

        // What to send
        const chosen = new Set(c?.events || PRESETS.problems);
        const eventBoxes = h("div", { class: "event-groups" });
        function paintEvents() {
            clear(eventBoxes);
            for (const group of [...new Set(kinds.map(k => k.group))]) {
                if (group === "Machines" && !store.multiMachine && !chosen.has("machineOffline")) continue;
                eventBoxes.append(h("div", { class: "event-group" }, h("span", { class: "upper", text: group }),
                    ...kinds.filter(k => k.group === group).map(k => {
                        const box = h("input", { type: "checkbox", checked: chosen.has(k.id) });
                        box.addEventListener("change", () => { if (box.checked) chosen.add(k.id); else chosen.delete(k.id); });
                        return h("label", { class: "row small checkline" }, box, h("span", { class: ["sev-dot", k.severity] }), k.label);
                    })));
            }
        }
        paintEvents();
        const presetRow = h("div", { class: "row small" }, h("span", { class: "muted", text: "Quick pick:" }),
            h("button", { type: "button", class: "btn ghost sm", onclick: () => { chosen.clear(); PRESETS.problems.forEach(k => chosen.add(k)); paintEvents(); } }, "Problems"),
            h("button", { type: "button", class: "btn ghost sm", onclick: () => { chosen.clear(); kinds.forEach(k => chosen.add(k.id)); paintEvents(); } }, "Everything"));

        // Which servers
        const picked = new Set(c?.scope || []);
        let scopeMode = picked.size ? "some" : "all";
        const scopeList = h("div", { class: "scope-list" });
        const scopeSeg = segmented([{ value: "all", label: store.multiMachine ? "All machines" : "All servers" }, { value: "some", label: "Only some" }], scopeMode, v => { scopeMode = v; paintScope(); });
        function paintScope() {
            clear(scopeList);
            scopeList.hidden = scopeMode === "all";
            if (scopeList.hidden) return;
            for (const m of store.machines.values()) {
                const whole = `${m.id}/*`;
                const mBox = h("input", { type: "checkbox", checked: picked.has(whole) });
                mBox.addEventListener("change", () => { if (mBox.checked) picked.add(whole); else picked.delete(whole); paintScope(); });
                scopeList.append(h("label", { class: "row small checkline scope-machine" }, mBox, icon("machine"), h("b", { text: store.multiMachine ? `Everything on ${m.name}` : "Every server" }), h("span", { class: "faint", text: "incl. new ones" })));
                if (picked.has(whole)) continue;
                for (const s of store.sortedServers(m.id)) {
                    const k = `${m.id}/${s.id}`;
                    const box = h("input", { type: "checkbox", checked: picked.has(k) });
                    box.addEventListener("change", () => { if (box.checked) picked.add(k); else picked.delete(k); });
                    scopeList.append(h("label", { class: "row small checkline scope-server" }, box, h("span", { text: `#${s.id} ${s.name}` })));
                }
            }
        }
        paintScope();

        const err = h("div", { class: "callout bad", role: "alert", hidden: true }, icon("warn"), h("span"));
        modal({
            title: isNew ? "Add a notification channel" : `Edit ${c.name}`, iconName: "bell", wide: true,
            body: h("div", { class: "stack" },
                err,
                h("div", { class: "row wrap" }, typeSeg, h("span", { class: "spacer" }), enabled),
                h("div", { class: "form-grid" }, nameField, mentionField, urlField),
                h("div", { class: "stack tight" }, h("div", { class: "row between" }, h("span", { class: "upper", text: "Send" }), presetRow), eventBoxes),
                h("div", { class: "stack tight" }, h("div", { class: "row between" }, h("span", { class: "upper", text: "About" }), scopeSeg), scopeList)),
            footer: close => {
                const save = h("button", { class: "btn primary", onclick: () => busy(save, async () => {
                    err.hidden = true;
                    const body = {
                        name: name.value.trim(), type, url: url.value.trim(), mention: type === "discord" ? mention.value.trim() : null,
                        enabled: enabled.input.checked, events: [...chosen], scope: scopeMode === "all" ? [] : [...picked],
                    };
                    if (scopeMode === "some" && !body.scope.length) { err.lastChild.textContent = "Pick at least one machine or server, or choose “All”."; err.hidden = false; return; }
                    try {
                        const saved = isNew ? await post("/notifications/channels", body) : await put(`/notifications/channels/${c.id}`, body);
                        close(true);
                        await loadChannels();
                        toast(isNew ? "Channel added" : "Saved", { type: "good", text: saved.name, action: { label: "Send a test", onClick: () => test(saved) } });
                    } catch (e) { err.lastChild.textContent = e.message; err.hidden = false; }
                }) }, icon("save"), isNew ? "Add channel" : "Save");
                return [h("button", { class: "btn", onclick: () => close(false) }, "Cancel"), save];
            },
        });
        requestAnimationFrame(() => (isNew ? name : url).focus());
    }

    await Promise.all([loadList(), loadChannels()]);
}

function dayLabel(at) {
    const d = new Date(at);
    const today = new Date();
    const yesterday = new Date(Date.now() - 86400000);
    if (d.toDateString() === today.toDateString()) return "Today";
    if (d.toDateString() === yesterday.toDateString()) return "Yesterday";
    return d.toLocaleDateString(undefined, { weekday: "long", month: "short", day: "numeric" });
}
