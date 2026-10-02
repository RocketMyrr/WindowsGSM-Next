// Discord bot (owners): /panel, /list and /stats in your Discord server, for every machine this panel controls.
// Who may use it is a list of Discord users, each with the servers they can control from the panel's buttons.

import { h, icon, clear, append } from "../dom.js";
import { get, put } from "../api.js";
import { store } from "../store.js";
import { setCrumbs } from "../shell.js";
import { field, input, toggle, toast, busy, modal, confirm, segmented, empty, loading } from "../ui.js";

export default async function discordBot(host, { scope }) {
    setCrumbs({ label: "Discord bot" });
    if (!store.me.isOwner) {
        host.append(h("div", { class: "callout warn" }, icon("lock"), h("span", { text: "Only owners can manage the Discord bot." })));
        return;
    }
    host.append(loading());
    let cfg;
    try { cfg = await get("/discord-bot"); }
    catch (e) { clear(host).append(empty("warn", "Couldn't load the bot's settings", e.message)); return; }
    clear(host);

    let admins = cfg.admins.map(a => ({ ...a }));
    const statusBox = h("div");
    const enabled = toggle("Bot is on", cfg.enabled, { hint: "Connects to Discord and answers /panel, /list and /stats.", help: "Needs a bot of your own: create an application at discord.com/developers, add a Bot, copy its token below, and invite it to your Discord with the applications.commands and bot scopes." });
    const token = input({ type: "password", class: "input mono", autocomplete: "off", placeholder: cfg.hasToken ? `Saved (${cfg.tokenHint || "hidden"}) — paste a new one to replace it` : "Paste the bot token" });
    const guild = input({ class: "input mono", value: cfg.guildId || "", placeholder: "Optional — e.g. 123456789012345678" });
    const botName = input({ value: cfg.botName || "", maxlength: 32, placeholder: "Optional — e.g. WindowsGSM" });
    const post = toggle("Post actions in the channel", cfg.postActions, { hint: "When someone starts or stops a server from /panel, say so in that channel so others know.", help: "Keeps everyone in the loop, so two people don't restart the same server at once." });
    const adminList = h("div", { class: "panel-body flush" });

    append(host, [
        h("div", { class: "page-head" },
            h("div", {}, h("h1", { text: "Discord bot" }), h("p", { text: "Control your servers from Discord with /panel, check them with /list and /stats." }))),
        cfg.importedFromLegacy ? h("div", { class: "callout info" }, icon("info"), h("span", {},
            h("b", { text: "Your old bot's setup was brought over " }), `(token and ${cfg.admins.length} admin${cfg.admins.length === 1 ? "" : "s"}). It's switched off so it doesn't answer twice while the old WindowsGSM's bot is still running — turn that one off first, then switch this on.`)) : null,
        h("div", { class: "discord-layout" },
            h("div", { class: "stack loose" },
                h("section", { class: "panel" },
                    h("div", { class: "panel-head" }, h("h3", { text: "Connection" })),
                    h("div", { class: "panel-body stack" },
                        statusBox,
                        enabled,
                        h("div", { class: "form-grid" },
                            field("Bot token", token, { span: true, help: "Like a password for the bot — anyone with it controls the bot. It's stored encrypted; if it ever leaks, press Reset Token in the Developer Portal and paste the new one.", hint: "Discord Developer Portal → your application → Bot → Reset Token. Keep it secret — it controls the bot." }),
                            field("Discord server ID", guild, { help: "Right-click your Discord server's icon → Copy Server ID (with Developer Mode on in Discord's Advanced settings).", hint: "Commands appear instantly there. Blank: the only server the bot is in (or all, which takes up to an hour)." }),
                            field("Bot name", botName, { hint: "Renames the bot in Discord (Discord allows this twice an hour)." })),
                        post)),
                h("section", { class: "panel" },
                    h("div", { class: "panel-head" }, h("h3", { class: "grow", text: "Who can use it" }),
                        h("button", { class: "btn sm", onclick: () => editAdmin(null) }, icon("plus"), "Add someone")),
                    adminList),
                h("div", { class: "row" }, h("span", { class: "spacer" }),
                    h("button", { class: "btn primary", onclick: e => save(e.currentTarget) }, icon("save"), "Save"))),
            howTo())]);

    function paintStatus(s) {
        const tone = { Online: "good", Connecting: "info", Error: "bad", Off: "" }[s.state] ?? "";
        const text = s.state === "Online" ? `Online as ${s.botUser || "the bot"} in ${s.guilds.length} Discord server${s.guilds.length === 1 ? "" : "s"}${s.guilds.length ? ": " + s.guilds.join(", ") : ""}.`
            : s.state === "Connecting" ? "Connecting to Discord…"
                : s.state === "Error" ? (s.error || "Couldn't connect.")
                    : "Off.";
        clear(statusBox).append(h("div", { class: ["callout", tone] }, icon(s.state === "Online" ? "checkCircle" : s.state === "Error" ? "warn" : "info"),
            h("div", { class: "grow" }, h("span", { text }),
                s.state === "Online" && s.guilds.length === 0 ? h("div", { class: "small", text: "It isn't in any Discord server yet — invite it with the button." }) : null),
            s.inviteUrl ? h("a", { class: "btn sm", href: s.inviteUrl, target: "_blank", rel: "noopener" }, icon("link"), "Invite to a server") : null));
    }
    paintStatus(cfg.status);

    function paintAdmins() {
        clear(adminList);
        if (!admins.length) {
            adminList.append(h("div", { class: "empty compact" }, icon("users"),
                h("p", { text: "Nobody yet — the bot only answers people on this list." })));
            return;
        }
        for (const a of admins) {
            adminList.append(h("div", { class: "list-item" },
                h("span", { class: "avatar sm", text: (a.name || "?").slice(0, 1) }),
                h("div", { class: "grow channel-main" }, h("b", { text: a.name || "(no name)" }), h("span", { class: "small muted mono", text: a.discordId })),
                h("span", { class: "small muted", text: scopeSummary(a.servers) }),
                h("button", { class: "btn ghost sm icon-only", "aria-label": `Edit ${a.name}`, onclick: () => editAdmin(a) }, icon("pencil")),
                h("button", { class: "btn ghost sm icon-only", "aria-label": `Remove ${a.name}`, onclick: async () => {
                    if (!(await confirm({ title: `Remove ${a.name || a.discordId}?`, message: "They won't be able to use the bot (after you save).", confirmLabel: "Remove", danger: true }))) return;
                    admins = admins.filter(x => x !== a);
                    paintAdmins();
                } }, icon("trash"))));
        }
    }
    paintAdmins();

    function scopeSummary(servers) {
        if (servers.includes("*")) return "All servers";
        const names = servers.map(s => {
            const [m, id] = s.split("/");
            return id === "*" ? `Everything on ${store.machineName(m)}` : store.server(m, id)?.name || `#${id}`;
        });
        return names.length <= 2 ? names.join(", ") : `${names.length} servers`;
    }

    function editAdmin(existing) {
        const name = input({ value: existing?.name || "", placeholder: "Their name, for you" });
        const id = input({ class: "input mono", value: existing?.discordId || "", placeholder: "e.g. 123456789012345678", inputmode: "numeric" });
        const picked = new Set(existing ? existing.servers.filter(s => s !== "*") : []);
        let mode = !existing || existing.servers.includes("*") ? "all" : "some";
        const list = h("div", { class: "scope-list" });
        const seg = segmented([{ value: "all", label: "All servers" }, { value: "some", label: "Only some" }], mode, v => { mode = v; paint(); });
        function paint() {
            clear(list);
            list.hidden = mode === "all";
            if (list.hidden) return;
            for (const m of store.machines.values()) {
                const whole = `${m.id}/*`;
                const mBox = h("input", { type: "checkbox", checked: picked.has(whole) });
                mBox.addEventListener("change", () => { if (mBox.checked) picked.add(whole); else picked.delete(whole); paint(); });
                list.append(h("label", { class: "row small checkline scope-machine" }, mBox, icon("machine"), h("b", { text: store.multiMachine ? `Everything on ${m.name}` : "Every server" }), h("span", { class: "faint", text: "incl. new ones" })));
                if (picked.has(whole)) continue;
                for (const s of store.sortedServers(m.id)) {
                    const k = `${m.id}/${s.id}`;
                    const box = h("input", { type: "checkbox", checked: picked.has(k) });
                    box.addEventListener("change", () => { if (box.checked) picked.add(k); else picked.delete(k); });
                    list.append(h("label", { class: "row small checkline scope-server" }, box, h("span", { text: `#${s.id} ${s.name}` })));
                }
            }
        }
        paint();
        const err = h("div", { class: "callout bad", hidden: true }, icon("warn"), h("span"));
        modal({
            title: existing ? `Edit ${existing.name || existing.discordId}` : "Add someone", iconName: "user",
            body: h("div", { class: "stack" }, err,
                h("div", { class: "form-grid" }, field("Name", name), field("Discord user ID", id, { hint: "Discord → Settings → Advanced → Developer Mode on. Then right-click them → Copy User ID." })),
                h("div", { class: "stack tight" }, h("div", { class: "row between" }, h("span", { class: "upper", text: "Can control" }), seg), list),
                h("p", { class: "tiny faint", text: "From /panel they can start, stop, restart, force-stop and update these servers, and see them in /list and /stats." })),
            footer: close => [
                h("button", { class: "btn", onclick: () => close(false) }, "Cancel"),
                h("button", { class: "btn primary", onclick: () => {
                    const discordId = id.value.trim();
                    const servers = mode === "all" ? ["*"] : [...picked];
                    const problem = !/^\d{15,21}$/.test(discordId) ? "That isn't a Discord user ID — it's a long number."
                        : admins.some(a => a !== existing && a.discordId === discordId) ? "They're already on the list."
                            : !servers.length ? "Pick at least one server, or choose “All servers”." : null;
                    if (problem) { err.lastChild.textContent = problem; err.hidden = false; return; }
                    const entry = { discordId, name: name.value.trim(), servers };
                    if (existing) Object.assign(existing, entry); else admins.push(entry);
                    paintAdmins();
                    close(true);
                    toast("Remember to save", { type: "info", timeout: 2500 });
                } }, icon("check"), existing ? "Done" : "Add")],
        });
        requestAnimationFrame(() => (existing ? name : id).focus());
    }

    async function save(button) {
        await busy(button, async () => {
            const res = await put("/discord-bot", {
                enabled: enabled.input.checked, token: token.value.trim() ? token.value.trim() : null, guildId: guild.value.trim() || null,
                botName: botName.value.trim() || null, postActions: post.input.checked, admins,
            });
            token.value = "";
            token.placeholder = res.hasToken ? `Saved (${res.tokenHint || "hidden"}) — paste a new one to replace it` : "Paste the bot token";
            paintStatus(res.status);
            toast(res.status.state === "Error" ? "Saved, but the bot couldn't connect" : "Saved", { type: res.status.state === "Error" ? "bad" : "good", text: res.status.state === "Error" ? res.status.error : "" });
        }, "Couldn't save");
    }

    // Status can change after saving (connecting → online); check back while the page is open.
    scope.every(5000, async () => { try { paintStatus((await get("/discord-bot")).status); } catch { /* keep */ } }, { now: false });

    function howTo() {
        const step = (n, title, text) => h("li", {}, h("b", { text: title }), h("span", { class: "small muted", text }));
        return h("section", { class: "panel subtle" },
            h("div", { class: "panel-head" }, h("h3", { text: "Setting it up" })),
            h("div", { class: "panel-body" },
                h("ol", { class: "pair-steps" },
                    step(1, "Create a bot", "At discord.com/developers → New Application → Bot. Press Reset Token and paste it here."),
                    step(2, "Add the people", "Add each person's Discord user ID and pick what they can control."),
                    step(3, "Switch it on and save", "Then use Invite to a server to add it to your Discord."),
                    step(4, "Use it", "Type /panel for a private control panel, /list for the servers, /stats for CPU, memory, disk and players.")),
                h("p", { class: "tiny faint", text: "It runs inside WindowsGSM on this machine and covers every machine this panel controls. Only one copy should use a token — if the old WindowsGSM's bot is on, turn it off first." })));
    }
}
