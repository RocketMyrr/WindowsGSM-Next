// Server settings, grouped into sections with plain-language labels. Only what you change is sent; the
// agent validates everything and saves nothing if any value is wrong.

import { h, icon, clear } from "../../dom.js";
import { get, patch, srv } from "../../api.js";
import { store } from "../../store.js";
import { toast, toastError, field, input, select, toggle, busy, confirm } from "../../ui.js";
import { setLeaveGuard } from "../../router.js";
import { historyDialog } from "./history.js";

const PRIORITIES = [
    { value: "0", label: "Low" }, { value: "1", label: "Below normal" }, { value: "2", label: "Normal" },
    { value: "3", label: "Above normal" }, { value: "4", label: "High" }, { value: "5", label: "Realtime (not recommended)" },
];

export default async function settingsTab(host, { id, machine, key, server, scope }) {
    const settings = await get(srv(machine, id, "/settings"));
    const values = { ...settings.values };
    const changes = new Map();
    const controls = new Map(); // key → field wrapper (for error display)

    const saveBar = h("div", { class: "save-bar", hidden: true },
        icon("info"), h("span", { class: "grow", text: "You have unsaved changes." }),
        h("button", { class: "btn", onclick: () => discard() }, "Discard"),
        h("button", { class: "btn primary", onclick: e => save(e.currentTarget) }, icon("save"), "Save changes"));

    function set(key, value) {
        if (String(value) === String(values[key] ?? settings.custom.find(c => c.key === key)?.value ?? "")) changes.delete(key);
        else changes.set(key, String(value));
        controls.get(key)?.setError?.("");
        saveBar.hidden = changes.size === 0;
        setLeaveGuard(changes.size ? () => confirm({ title: "Discard unsaved settings?", message: "Your changes to this server's settings haven't been saved.", confirmLabel: "Discard", danger: true }) : null);
    }

    const text = (key, label, opts = {}) => {
        const el = input({ value: values[key] ?? "", type: opts.type || "text", class: opts.mono ? "input mono" : "input", placeholder: opts.placeholder || "", autocomplete: "off" });
        el.addEventListener("input", () => set(key, el.value));
        const f = field(label, el, { hint: opts.hint, span: opts.span, help: opts.help });
        controls.set(key, f);
        return f;
    };
    // Start parameters can be long: a wrapping box that grows with them. Saved as one line (line breaks become
    // spaces), which is what the command line and the old app expect.
    const params = () => {
        const key = "serverparam";
        const el = h("textarea", { class: "input mono params-box", rows: 3, spellcheck: "false", autocomplete: "off", placeholder: "-log -nosteam +maxplayers 20" });
        el.value = values[key] ?? "";
        const grow = () => { el.style.height = "auto"; el.style.height = Math.min(el.scrollHeight + 2, 320) + "px"; };
        el.addEventListener("input", () => { set(key, el.value.replace(/\s*\r?\n\s*/g, " ").trim()); grow(); });
        requestAnimationFrame(grow);
        const f = field("Extra start parameters", el, { hint: "Added to the server's command line. Line breaks are only for reading — they're saved as spaces.", span: true,
            help: ["Options passed to the game when it starts — e.g. -log, +maxplayers 20, -mods=… or ?ServerPassword=… for ARK. Each game has its own; its wiki or the plugin's page lists them.",
                "WindowsGSM already adds the IP, ports, map and max players from the fields above, so don't repeat those here."] });
        controls.set(key, f);
        return f;
    };
    const secret = (key, label, opts = {}) => text(key, label, { ...opts, type: "password" });
    const onOff = (key, label, hint, help) => toggle(label, values[key] === "1", { hint, help, onChange: on => set(key, on ? "1" : "0") });
    const choice = (key, label, options, hint, help) => {
        const el = select(options, values[key] || options[0].value);
        el.addEventListener("change", () => set(key, el.value));
        const f = field(label, el, { hint, help });
        controls.set(key, f);
        return f;
    };
    const section = (title, sub, ...content) => h("section", { class: "panel settings-section" },
        h("div", { class: "panel-head" }, h("h3", { text: title }), sub ? h("span", { class: "sub", text: sub }) : null),
        h("div", { class: "panel-body stack" }, ...content));

    // ── Sections ──
    const general = section("General", null, h("div", { class: "form-grid" },
        settings.customReplacesBuiltIns ? null : text("servername", "Server name", { span: true, help: "Shown in the panel and, for most games, in the in-game server browser. Some games read it from their own config instead — see Game config." }),
        text("serverip", "IP address", { mono: true, hint: "The address players connect to.", help: ["The address shown to players and used for the join address. On a home connection that is usually this PC's LAN address (players outside your network use your public address and a forwarded port).", "WindowsGSM itself asks the game on this machine, so player counts work whatever you put here."] }),
        text("serverport", "Game port", { type: "number", mono: true, help: ["The port players connect to. Two servers can't share one. Most games use UDP.", "Players outside your network need it forwarded on your router — Overview → Can players reach it?"] }),
        text("serverqueryport", "Query port", { type: "number", mono: true, hint: "Used for the server browser and player counts.", help: ["Where the game answers \"how many players, which map\" — Steam's server browser and WindowsGSM's player counts use it.", "Many games use the game port + 1. If counts don't show, the Players tab says which port actually answers."] }),
        text("servermaxplayer", "Max players", { type: "number", help: "Passed to the game when it starts. Some games read their own config file instead — check Game config too." }),
        settings.customReplacesBuiltIns ? null : text("servermap", "Map", { help: "The map or world the server starts on, e.g. TheIsland_WP, de_dust2 or Procedural Map. Leave empty for the game's default." }),
        settings.customReplacesBuiltIns ? null : secret("servergslt", "Game server login token", { hint: "Steam GSLT, if the game needs one.", help: ["Some Steam games (CS2, Garry's Mod, Team Fortress 2…) need a Game Server Login Token to show up publicly. Make one at steamcommunity.com/dev/managegameservers with the game's app id — one token per server.", "Kept secret: it's never shown again after saving."] }),
        params()));

    const game = settings.custom.length ? section(`${settings.game.replace(/\s*\[[^\]]*\]\s*$/, "")} settings`, "Settings this game provides.", h("div", { class: "form-grid" },
        ...settings.custom.map(c => {
            values[c.key] = c.value;
            if (c.options && c.options.length) {
                const el = select(c.options, c.value);
                el.addEventListener("change", () => set(c.key, el.value));
                const f = field(c.label, el);
                controls.set(c.key, f);
                return f;
            }
            return text(c.key, c.label);
        }))) : null;

    const branchSelect = select([{ value: values.steambranch || "", label: values.steambranch || "public (default)" }], values.steambranch || "");
    branchSelect.addEventListener("change", () => set("steambranch", branchSelect.value));
    const branchField = field("Branch", branchSelect, { help: ["Steam branches are versions of the game: \"public\" is the normal one; others are betas or older versions some games keep.", "Changing it downloads that version on the next update."], hint: settings.steamBranchLastInstalled ? `Installed: ${settings.steamBranchLastInstalled}. Changing it installs the new branch on the next update.` : "Beta branches get new game versions early." });
    controls.set("steambranch", branchField);
    const steam = settings.isSteam ? section("Steam", "Updates use DepotDownloader.", h("div", { class: "form-grid" },
        branchField,
        secret("steambeta_password", "Branch password", { hint: "Only for password-protected branches.", help: "Some games keep test or old versions behind a password (e.g. for modders). The game's developers publish it; most branches don't need one." }))) : null;

    const automation = section("Automation", null,
        h("div", { class: "toggles" },
            onOff("autostart", "Start with the agent", "When this machine starts WindowsGSM.", "Starts this server whenever the agent starts — after a reboot too, if the agent starts with Windows (Agent settings)."),
            onOff("autorestart", "Restart after a crash", "With back-off if it keeps crashing.", "If the game stops on its own, it's started again — waiting a little longer each time. After repeated crashes in a short time it gives up (\"Auto-restart paused\") so a broken server doesn't loop; check its log, then start it yourself."),
            onOff("autoupdate", "Update automatically", "Checks every 30 minutes; restarts to install.", "When a new version of the game is out, the server saves, stops, updates and starts again by itself. Paused while updates are on hold after a roll back."),
            onOff("updateonstart", "Update before starting", null, "Every start (and restart) updates the game first if there's something new. Starts take longer, but the server is never out of date."),
            onOff("updateaddonsonstart", "Update add-ons before starting", null, "Updates the add-ons in the Add-ons tab (e.g. Oxide) before each start, so they keep up with game updates.")));

    const alerts = section("Discord alerts", "This server only. For alerts about every server and machine in one place, use Notifications in the sidebar.",
        onOff("discordalert", "Send alerts to Discord", null, "This server's own Discord webhook, for the events ticked below. For alerts about every server in one place, use Notifications → channels instead."),
        h("div", { class: "form-grid" },
            secret("discordwebhook", "Webhook URL", { span: true, mono: true, hint: "Discord → Server settings → Integrations → Webhooks.", help: "In Discord: Server settings → Integrations → Webhooks → New webhook, pick the channel, Copy webhook URL, and paste it here. Anyone with the URL can post to that channel, so it's kept secret." }),
            text("discordmessage", "Mention", { placeholder: "@here or <@&role id>", help: "Who gets pinged with each alert: @here, @everyone, or a role as <@&role id> (in Discord, turn on Developer Mode, then right-click the role → Copy role ID)." })),
        h("div", { class: "toggles" },
            onOff("crashalert", "Crashes"), onOff("autorestartalert", "Automatic restarts"), onOff("autostartalert", "Auto-starts"),
            onOff("autoupdatealert", "Automatic updates"), onOff("restartcrontabalert", "Scheduled restarts"), onOff("autoipupdatealert", "IP changes")));

    const consoleSec = section("Console & RCON", "Captured: the game's output shows in the Console tab and commands go in there. Not captured: the game runs in its own window on this PC (Console tab → Show window).",
        h("div", { class: "toggles" },
            onOff("embedconsole", "Capture the console here", "Shows the game's output in the Console tab.", ["On: the game's output appears in the Console tab and you type commands there, from anywhere.", "Off: the game runs in its own window on this PC's screen (Console tab → Show window). Some games only work properly this way — if a game misbehaves with it on, turn it off. Takes effect on the next start."]),
            onOff("showconsole", "Show the console window on this machine", null, "For servers that aren't captured: whether their window is visible on this PC's screen when they start. You can still show or hide it any time from the Console tab."),
            // On unless turned off ("0"): absent means on.
            toggle("Measure game performance over RCON", values.perfsample !== "0", { hint: "Server FPS / TPS every 5 minutes, for the Overview. The game logs each RCON visit — turn off if that clutters its console.", onChange: on => set("perfsample", on ? "1" : "0") })),
        h("div", { class: "form-grid" },
            text("rconip", "RCON address", { mono: true, placeholder: "127.0.0.1", help: "Where WindowsGSM reaches the game's RCON — this PC (127.0.0.1) unless the game only listens on its server IP." }),
            text("rconport", "RCON port", { type: "number", mono: true, help: ["RCON is the game's remote console: WindowsGSM sends commands over it and gets replies (the Console tab's RCON switch, schedules, save before stop).", "Set the same port and password in the game's own config. Don't forward this port on your router."] }),
            secret("rconpassword", "RCON password", { help: "The same password as in the game's own config (e.g. rcon_password, RCONPassword, rcon.password). Use a long random one — RCON can run any command on the server." })));

    const cores = Math.max((values.cpuaffinity || "").length, store.machineOf(machine)?.metrics?.cores || 0, 1);
    const affinity = h("div", { class: "cores" });
    let bits = (values.cpuaffinity && values.cpuaffinity.length >= cores ? values.cpuaffinity : "1".repeat(cores)).split("");
    function paintCores() {
        clear(affinity).append(...bits.map((b, i) => h("button", { type: "button", class: ["core", b === "1" && "on"], "aria-pressed": String(b === "1"), title: `Core ${i}`, onclick: () => {
            bits[i] = bits[i] === "1" ? "0" : "1";
            if (!bits.includes("1")) bits[i] = "1"; // at least one core
            set("cpuaffinity", bits.join(""));
            paintCores();
        } }, String(i))));
    }
    paintCores();
    const performance = section("Performance", null,
        h("div", { class: "form-grid" }, choice("cpupriority", "Process priority", PRIORITIES, "Higher can smooth out lag on a busy machine.", "How Windows shares the CPU when it's busy. Above normal or High gives this server first pick — good for your main server on a shared PC. Realtime can freeze the machine; avoid it.")),
        h("div", { class: "field" }, h("span", { class: "label", text: "CPU cores it may use" }), affinity, h("div", { class: "hint", text: "Pin busy servers to different cores. All on = no restriction." })),
        onOff("memoryguard", "Restart if memory stays too high", "Stops runaway memory use from taking the machine down.", "Some servers slowly use more and more memory (a leak) until the whole PC struggles. With this on, the server is restarted cleanly — world saved first — once it stays above the limit below for the time below."),
        h("div", { class: "form-grid" },
            text("memoryguardthresholdmb", "Memory limit (MB)", { type: "number", help: "Look at the server's usual memory on its Overview chart and set this comfortably above it — e.g. 12000 for a server that normally uses 8 GB." }),
            text("memoryguardsustainminutes", "For at least (minutes)", { type: "number", help: "Short spikes (a big save, many players joining) are normal; this waits until memory has stayed high this long before restarting." })));

    // Stopping safely: save the world first, and give the game time to shut down cleanly.
    const known = settings.knownSaveCommand;
    const stopping = section("Stopping safely", "Before a stop, restart or update — not Force stop.",
        h("div", { class: "form-grid" },
            text("savecommand", "Save command", { mono: true, help: ["Before a stop, restart or update, WindowsGSM tells the game to save its world, then waits a moment — so you don't lose what happened since the game's last autosave.", "Known for Rust, ARK, 7 Days to Die, Palworld, Project Zomboid, Terraria, Unturned and Minecraft. Force stop never saves."], placeholder: known ? `${known} (this game's)` : "none known — type one, e.g. save",
                hint: known ? `Leave empty to use “${known}”. “-” turns saving off.` : "Sent to the console (or RCON) before stopping. “-” turns it off." }),
            text("savewait", "Wait after saving (seconds)", { type: "number", placeholder: "10", help: "How long to give the game to finish writing its save before asking it to stop. Big worlds may need 30 or more." }),
            text("stoptimeout", "Wait for a clean shutdown (seconds)", { type: "number", placeholder: "30", hint: "Then the process is ended. Big worlds save on shutdown — give them time." })));

    const advanced = section("Advanced", null,
        onOff("steamcmd_override", "Use SteamCMD instead of DepotDownloader", "Only if this game won't update with DepotDownloader.", "DepotDownloader is faster and supports roll back. A few games install extra things only SteamCMD does — if updates fail or the server misses files, try this. Roll back isn't available with SteamCMD."));

    // Every save keeps the settings it replaced: compare, or put an earlier set back.
    const tools = h("div", { class: "row settings-tools" }, h("span", { class: "spacer" }),
        h("button", { class: "btn sm", title: "Earlier versions of these settings — compare or put one back", onclick: () => historyDialog({ machine, id, path: "settings", title: "Settings history", onRestored: () => location.reload() }) }, icon("clock"), "History"));
    host.append(tools, h("div", { class: "settings-grid" }, general, game, steam, automation, stopping, alerts, consoleSec, performance, advanced), saveBar);

    // Steam branches load in the background (asks Steam directly).
    if (settings.isSteam) {
        get(srv(machine, id, "/steam/branches")).then(list => {
            const current = values.steambranch || "";
            clear(branchSelect);
            for (const b of list) {
                const val = b.name === "public" ? "" : b.name;
                branchSelect.append(h("option", { value: val, selected: val === current }, b.name + (b.passwordRequired ? " (password)" : "") + (b.description ? ` — ${b.description}` : "")));
            }
            if (current && !list.some(b => b.name === current)) branchSelect.append(h("option", { value: current, selected: true }, current));
        }).catch(() => { /* keep the saved value */ });
    }

    async function save(button) {
        await busy(button, async () => {
            try {
                await patch(srv(machine, id, "/settings"), { values: Object.fromEntries(changes) });
            } catch (e) {
                // Point at the fields the agent rejected.
                for (const d of e.details || []) {
                    const key = [...controls.keys()].find(k => d.toLowerCase().startsWith(k.toLowerCase() + ":") || d.includes(`'${k}'`));
                    if (key) controls.get(key).setError(d.replace(/^[^:]+:\s*/, ""));
                }
                throw e;
            }
            for (const [k, v] of changes) values[k] = v;
            changes.clear();
            saveBar.hidden = true;
            setLeaveGuard(null);
            toast("Settings saved", { type: "good", text: server().state === "Running" ? "Some changes apply after a restart." : "", timeout: 3500 });
            store.refreshServer(machine, id);
        }, "Settings not saved");
    }

    async function discard() {
        changes.clear();
        setLeaveGuard(null);
        const { default: again } = await import("./settings.js");
        clear(host);
        await again(host, { id, server, scope });
    }

    scope.add(() => setLeaveGuard(null));
}
