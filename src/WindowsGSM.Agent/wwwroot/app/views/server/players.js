// Who's online (live, from the game's own query), who plays most, and who came by recently.

import { h, icon, clear, fmtDuration, timeAgo, fmtDateTime } from "../../dom.js";
import { get, post, srv } from "../../api.js";
import { store } from "../../store.js";
import { empty, isRunning, promptText, toast, toastError, showMenu, segmented, input, busy, confirm } from "../../ui.js";
import { can } from "../../perms.js";

export default async function playersTab(host, { id, machine, key, server, scope }) {
    const body = h("div");
    const count = h("span", { class: "count-badge accent", hidden: true });
    const queryLine = h("div", { class: "query-line small" });

    // Where player numbers come from, and — when they don't come — why, in plain words.
    async function loadQuery() {
        if (!isRunning(server())) { clear(queryLine); return; }
        let q;
        try { q = await get(srv(machine, id, "/players/query")); } catch { return; }
        clear(queryLine);
        if (!q.protocol && !q.note) return;
        const ok = q.answering;
        queryLine.append(h("div", { class: ["callout", ok ? (q.note ? "info" : "") : "warn"] }, icon(ok ? (q.note ? "info" : "checkCircle") : "warn"),
            h("div", { class: "grow" },
                h("div", { text: ok ? `Players come from the game's ${q.protocol} query on ${q.endpoint}.` : q.protocol ? `The game's ${q.protocol} query isn't answering${q.endpoint ? " on " + q.endpoint : ""}.` : "" }),
                q.note ? h("div", { class: "muted", text: q.note }) : null)));
    }
    host.append(h("section", { class: "panel" },
        h("div", { class: "panel-head" }, h("h2", { text: "Players online" }), count, h("span", { class: "spacer" }),
            can(server(), "Console") ? h("button", { class: "btn sm", onclick: () => broadcast() }, icon("send"), "Message everyone") : null),
        h("div", { class: "panel-body flush" }, body),
        queryLine));

    // ── History ──
    let days = 30;
    const regulars = h("div", { class: "panel-body flush" });
    const recent = h("div", { class: "panel-body flush" });
    const daysBar = segmented([{ value: 7, label: "7 days" }, { value: 30, label: "30 days" }, { value: 365, label: "Year" }], days, v => { days = v; loadHistory(); });
    daysBar.classList.add("sm");
    host.append(h("div", { class: "players-history" },
        h("section", { class: "panel" }, h("div", { class: "panel-head wrap" }, h("h2", { class: "grow", text: "Regulars" }), daysBar), regulars),
        h("section", { class: "panel" }, h("div", { class: "panel-head" }, h("h2", { text: "Recent visits" })), recent)));

    async function loadHistory() {
        let data;
        try { data = await get(srv(machine, id, `/players/history?days=${days}`)); }
        catch (e) { clear(regulars).append(empty("warn", "Couldn't load player history", e.message)); clear(recent); return; }
        clear(regulars);
        if (!data.top.length) {
            regulars.append(empty("players", "No one yet", "Once the game shares player names, time played is tallied here."));
        } else {
            const most = data.top[0].seconds || 1;
            regulars.append(...data.top.map((p, i) => {
                const bar = h("span");
                bar.style.setProperty("width", Math.max(2, Math.round(p.seconds / most * 100)) + "%");
                return h("div", { class: "list-item regular" },
                    h("span", { class: "rank num", text: i + 1 }),
                    h("span", { class: "avatar sm", text: p.name.slice(0, 1) }),
                    h("div", { class: "grow regular-main" },
                        h("div", { class: "row" }, h("b", { class: "truncate", text: p.name }), p.online ? h("span", { class: "tag green" }, h("span", { class: "dot st-running" }), "Online") : null),
                        h("span", { class: "score-bar" }, bar)),
                    h("div", { class: "regular-stats" },
                        h("b", { class: "num", text: fmtDuration(p.seconds) }),
                        h("span", { class: "tiny faint", text: `${p.sessions} visit${p.sessions === 1 ? "" : "s"} · ${p.online ? "now" : timeAgo(p.lastSeen)}` })));
            }));
        }
        clear(recent);
        if (!data.recent.length) { recent.append(h("div", { class: "small faint pad", text: "No visits recorded yet." })); return; }
        recent.append(...data.recent.map(v => h("div", { class: "list-item visit" },
            h("span", { class: "avatar sm", text: v.name.slice(0, 1) }),
            h("b", { class: "grow truncate", text: v.name }),
            h("span", { class: "small muted", title: fmtDateTime(v.joinedAt), text: timeAgo(v.joinedAt) }),
            h("span", { class: "num small visit-length", text: v.leftAt ? fmtDuration((Date.parse(v.leftAt) - Date.parse(v.joinedAt)) / 1000) : "playing" }))));
    }

    function paint(players) {
        const s = server();
        clear(body);
        count.hidden = !players.length;
        count.textContent = players.length;
        if (!isRunning(s)) { body.append(empty("players", "Server isn't running", "Players appear here while the server is up.")); return; }
        if (!players.length) { body.append(empty("players", "Nobody's online", s.players ? "The game reports players but doesn't share their names." : "When players join, they show up here within a few seconds.")); return; }
        const max = Math.max(...players.map(p => p.score || 0), 1);
        body.append(h("table", { class: "table" },
            h("thead", {}, h("tr", {}, h("th", { text: "Player" }), h("th", { text: "Score" }), h("th", { text: "Connected" }), h("th", { class: "actions" }))),
            h("tbody", {}, ...players.map(p => {
                const bar = h("span");
                bar.style.setProperty("width", Math.round((p.score || 0) / max * 100) + "%");
                return h("tr", {},
                    h("td", {}, h("span", { class: "row" }, h("span", { class: "avatar sm", text: (p.name || "?").slice(0, 1) }), h("b", { text: p.name || "(unnamed)" }))),
                    h("td", {}, h("div", { class: "score" }, h("span", { class: "num", text: p.score ?? 0 }), h("span", { class: "score-bar" }, bar))),
                    h("td", { class: "num muted", text: p.connectedSeconds != null ? fmtDuration(p.connectedSeconds) : "—" }),
                    h("td", { class: "actions" }, can(server(), "Console") ? h("button", { class: "btn ghost sm icon-only", "aria-label": `Actions for ${p.name}`, onclick: e => showMenu(e.currentTarget, [
                        { label: "Copy name", icon: "copy", onClick: () => navigator.clipboard.writeText(p.name) },
                        { label: "Kick… (sends 'kick <name>')", icon: "logout", onClick: () => sendCommand(`kick "${p.name}"`) },
                    ]) }, icon("more")) : null));
            }))));
    }

    async function broadcast() {
        const text = await promptText({ title: "Message everyone", message: "Sends a 'say' command to the server.", label: "Message", confirmLabel: "Send", iconName: "send" });
        if (text) await sendCommand("say " + text);
    }
    async function sendCommand(command) {
        try {
            const res = await post(srv(machine, id, "/console"), { command });
            if (res.sent) toast("Sent", { type: "good", text: command, timeout: 2500 });
            else toast("Not sent", { type: "bad", text: res.error });
        } catch (e) { toastError(e); }
    }

    scope.add(store.on("players:" + key, paint));
    scope.add(store.on("server:" + key, () => load()));
    scope.every(60000, loadHistory);
    scope.every(15000, loadQuery);
    // Someone joined or left: the history changes too (debounced — the game is queried every few seconds).
    let historyTimer = null;
    scope.add(store.on("players:" + key, () => { clearTimeout(historyTimer); historyTimer = setTimeout(loadHistory, 1500); }));
    scope.add(() => clearTimeout(historyTimer));
    async function load() { try { paint(await get(srv(machine, id, "/players"))); } catch { /* keep */ } }

    // ── Whitelist / operators (Minecraft Java) and allowlist (Bedrock) ──
    const access = h("div", { class: "stack loose" });
    host.append(access);
    async function loadAccess() {
        let lists;
        try { lists = await get(srv(machine, id, "/players/access")); } catch { clear(access); return; } // a game without them
        const edit = can(server(), "EditConfig");
        clear(access).append(...lists.map(l => {
            const name = input({ placeholder: "Player name", maxlength: 16, "aria-label": `Add a player to the ${l.label.toLowerCase()}` });
            const add = h("button", { class: "btn sm primary", onclick: () => change(l, name.value, true, add) }, icon("plus"), "Add");
            name.addEventListener("keydown", e => { if (e.key === "Enter") add.click(); });
            return h("section", { class: "panel" },
                h("div", { class: "panel-head wrap" }, h("h2", { text: l.label }),
                    h("span", { class: "count-badge", text: l.names.length }),
                    l.enforced === false ? h("span", { class: "tag amber", title: "Turn it on under Configs → server.properties." }, "Off") : l.enforced ? h("span", { class: "tag green" }, "On") : null,
                    h("span", { class: "sub", text: l.hint })),
                h("div", { class: "panel-body flush" },
                    l.names.length ? h("div", {}, ...l.names.map(n => h("div", { class: "list-item" },
                        h("span", { class: "avatar sm", text: n.slice(0, 1) }), h("b", { class: "grow truncate", text: n }),
                        edit ? h("button", { class: "btn ghost sm icon-only", "aria-label": `Remove ${n}`, title: "Remove", onclick: e => change(l, n, false, e.currentTarget) }, icon("trash")) : null)))
                        : empty("players", `No one on the ${l.label.toLowerCase()}`, l.hint)),
                edit ? h("div", { class: "panel-foot row" }, name, add,
                    h("span", { class: "tiny faint", text: isRunning(server()) ? "Sent to the running server as a command." : "Saved to the file; the server reads it when it starts." })) : null);
        }));
    }
    async function change(list, name, adding, button) {
        name = (name || "").trim();
        if (!name) return;
        if (!adding && !(await confirm({ title: `Remove ${name}?`, message: `From the ${list.label.toLowerCase()}.`, confirmLabel: "Remove", danger: true }))) return;
        await busy(button, async () => {
            await post(srv(machine, id, `/players/access/${encodeURIComponent(list.key)}`), { name, add: adding });
            toast(adding ? `Added ${name}` : `Removed ${name}`, { type: "good", timeout: 2500 });
            // A running server writes the file a moment after the command.
            setTimeout(loadAccess, isRunning(server()) ? 1500 : 0);
        }, adding ? `Couldn't add ${name}` : `Couldn't remove ${name}`);
    }
    scope.every(60000, loadAccess, { now: false });
    loadAccess();
    await load();
}
