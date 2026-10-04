// Built-in help: short, searchable answers to "how do I…" and "why doesn't…", with links to where it's done.
// Topics can be linked to directly: /help#reach.

import { h, icon, clear, debounce } from "../dom.js";
import { setCrumbs } from "../shell.js";

/** [id, title, icon, keywords, paragraphs, links] — a paragraph starting with "• " is a list item. */
const TOPICS = [
    ["start", "Getting started", "rocket", "install new first server begin", [
        "Install a server picks free ports, downloads the game (Steam games with DepotDownloader) and sets it up. Follow it in Activity; when it's done, press Start.",
        "Each server has tabs: Overview (performance, details, health check), Console, Logs, Players, Game config (the game's own settings files), Files, Backups, Add-ons, Schedules and Settings.",
        "Server name, ports, max players and start parameters are in Settings. Most games also keep settings in their own files — those are in Game config.",
    ], [["/install", "Install a server"]]],

    ["overview", "The server list", "grid", "overview list dashboard bulk select tag filter search status", [
        "Overview shows every server on every machine with its status, players, CPU and memory. Click one to open it.",
        "• Tick several servers to start, stop, restart, update or back them up together.",
        "• Tags (a server's ⋯ menu → Tags…) group servers — EU, friends, modded — and the filter shows just one group.",
        "• Ctrl K (⌘K on a Mac) jumps to any server, page or action by typing a few letters.",
        "• Your account → Appearance has a compact list, light/dark and accent colours.",
    ], [["/", "Overview"]]],

    ["network", "Using it from your phone or another PC", "globe", "phone mobile remote network lan https certificate fingerprint internet port 8971 access app desktop control another pc", [
        "The panel runs on this PC (port 8971). To reach it from elsewhere:",
        "• Same network (your phone on Wi-Fi): Agent settings → Network → turn on \"Reachable from other computers\", then open http://<this PC's address>:8971.",
        "• From the internet: turn on HTTPS first (Let's Encrypt gives a free trusted certificate if you have a domain), then forward port 8971 on your router.",
        "Two-factor or a passkey (Your account) is strongly advised once the panel is reachable from outside.",
        "The panel works as an app on your phone too: in the browser menu choose \"Add to Home screen\".",
        "On another Windows PC, the WindowsGSM app does it in its own window: install it there and choose \"Control game servers on another PC\" — or, on a PC that runs servers too, tray icon → PC → Connect to another PC. With a self-signed certificate it shows the fingerprint once; compare it with the one under Agent settings → HTTPS here.",
    ], [["/settings", "Agent settings"], ["/account", "Your account"]]],

    ["files", "Files and game config", "folder", "files edit upload download config ini cfg properties history restore text editor", [
        "Files browses the server's folder: open text files to edit them, upload or download, make folders, rename or delete.",
        "Game config shows the game's own settings files (server.cfg, server.properties, Game.ini…) as a form — search a setting, change it, save.",
        "• Every save keeps the version it replaced: History compares it with now or puts it back.",
        "• Most games read their config at start: restart the server after changing it.",
        "• Stop the server before replacing files it has open (maps, mods, the game itself).",
    ], []],

    ["players", "Players and player counts", "players", "players online who history kick ban count query a2s", [
        "The Players tab lists who's online and who played recently, and for how long.",
        "Counts come from the game's query port. If they stay empty while people are on, the tab says which port actually answers — set that as the query port in Settings.",
        "Kick or ban through the Console (or RCON) with the game's own commands.",
    ], []],

    ["addons", "Game plugins and add-ons", "puzzle", "plugin plugins add-ons addons mods community game support .cs install roll back", [
        "Game plugins (Manage → Game plugins) add games WindowsGSM doesn't know out of the box. Install from the community list, or add your own .cs file. Updating keeps the previous version, so you can roll back.",
        "A server's Add-ons tab installs mods its game's plugin knows about (e.g. Oxide for Rust). They're tracked, so updates never overwrite add-ons you built yourself.",
    ], [["/plugins", "Game plugins"]]],

    ["workshop", "Steam Workshop mods", "steam", "workshop mods steam collection subscribe dayz arma conan", [
        "A server's Workshop tab installs Steam Workshop items: paste an item link or a whole collection link.",
        "• \"Update before each start\" keeps mods current automatically.",
        "• Some games only let owners download their Workshop items: set up the Steam account (Agent settings) and turn on \"Use the Steam account\".",
        "• DayZ and Arma get their @Mod folders, keys and -mod= list set up; Conan Exiles its modlist.txt.",
    ], []],

    ["shortcuts", "Keyboard shortcuts", "zap", "keyboard shortcut keys ctrl k palette quick jump", [
        "• Ctrl K (⌘K): search and jump anywhere, or run an action (\"restart rust\").",
        "• ? : the full list of shortcuts.",
        "• In the Console: ↑ / ↓ go through earlier commands, Enter sends.",
        "• Esc closes dialogs and menus.",
    ], []],

    ["drives", "Game files on another drive", "disk", "drive disk space ssd hdd another drive move location folder storage spread", [
        "Each server's game files can live on any drive in this PC — handy to spread big servers over two drives, or keep them on a fast SSD. Admins can choose:",
        "• When installing: \"Where to put the game files\" → pick a drive and a folder (e.g. E:\GameServers). Each server gets a folder of its own inside it.",
        "• Later: the server's ⋯ menu → Move files to another drive… (the server must be stopped). The files are copied and checked first; only then does it switch over and remove the old copy. The same menu moves them back.",
        "The server's settings, logs and backups list stay with WindowsGSM; only the game files move. Backups, restores, updates, the file manager and plugins all work as usual.",
        "• Use drives inside this PC (or a USB drive that stays connected) — network shares can't be used.",
        "• If that drive isn't connected, the server says so and won't start until it's back.",
        "• Deleting the server deletes its files on that drive too.",
    ], [["/storage", "Storage"]]],

    ["reach", "Players can't connect", "globe", "port forward firewall router upnp nat join connect lan internet public ip", [
        "Open the server's Overview and press \"Can players reach it?\" — it checks that the game is listening, the address, Windows Firewall, your router and (for Steam games) Steam's public server list, and says what to fix.",
        "• Windows Firewall: \"Allow through firewall\" on the Overview adds a rule for the game (one Windows prompt on the machine).",
        "• Router: players outside your network need the game and query ports forwarded to this PC. Turn on \"Forward ports automatically\" in the same dialog if your router has UPnP; otherwise forward them by hand in the router's settings.",
        "• Address: 127.0.0.1 only works on this PC. Use the PC's network address (or 0.0.0.0).",
        "Friends on the same network join with this PC's local address; everyone else with your public address.",
    ], []],

    ["players", "Player counts", "players", "a2s query players count browser steam zero", [
        "WindowsGSM asks the game for players on its query port. If counts stay empty, the Players tab says why — often the game answers on a different port than its settings say; WindowsGSM looks for it and tells you which.",
        "Who played and for how long is on the Players tab too.",
    ], []],

    ["updates", "Updates", "update", "update auto-update steam build version verify validate depotdownloader steamcmd branch", [
        "Update downloads the newest version (the server must be stopped). Verify files checks every file against Steam and repairs damaged ones.",
        "• Auto-update: checks every 30 minutes while the server runs; when a new build is out it stops, updates and starts the server.",
        "• Update on start: updates every time the server starts.",
        "Steam games download with DepotDownloader. A beta or older branch can be picked in Settings → Branch.",
    ], []],

    ["rollback", "Rolling back a bad update", "restore", "rollback roll back downgrade previous build manifest hold broken update", [
        "If an update breaks the game (or your mods), More → \"Roll back game update…\" lists the builds this server has had and puts an earlier one back. It uses DepotDownloader's own record of each build — not backups — so worlds and configs aren't touched.",
        "Afterwards updates are on hold (the server shows \"Updates on hold\") so auto-update doesn't undo it. Update by hand, or Resume updates in the same dialog, once the game is fixed.",
        "Only for Steam games installed or updated with DepotDownloader. Steam occasionally stops offering very old builds.",
    ], []],

    ["steam", "Steam account & Steam Guard", "steam", "steam login account password steam guard 2fa code authenticator email anonymous", [
        "Most dedicated servers download anonymously. Some games and Workshop items need a Steam account that owns them: Agent settings → Steam account → Sign in.",
        "If Steam Guard asks for a code (email or the Steam Mobile App), type it in the panel while it waits. The sign-in is remembered for installs and updates. Consider a separate Steam account just for servers.",
        "The password is stored encrypted on this machine. An old WindowsGSM's plain-text copy (userData.txt) can be removed from the same screen.",
    ], [["/settings", "Agent settings"]]],

    ["backups", "Backups", "backup", "backup restore copy zip verify retention", [
        "Back up now (More menu or the Backups tab) zips the server's files. In the Backups tab you can set how many to keep, copy each backup to another drive or a network share, and verify a backup's files.",
        "Restore puts a backup back (the server must be stopped). Backups survive deleting a server.",
    ], []],

    ["schedules", "Schedules & automations", "calendar", "schedule restart cron automation rule empty cpu memory crash disk notify", [
        "Schedules run things at set times: restarts, backups, updates or console commands (per server, or all of them from Schedules).",
        "Automations react to what happens: no players for a while, high CPU or memory, a crash, someone joining, or a drive running low on space — and notify you, stop, restart, back up or send a command.",
    ], [["/schedules", "Schedules"], ["/automations", "Automations"]]],

    ["stopping", "Stopping safely (saving the world)", "save", "stop save world shutdown saveworld timeout kill force", [
        "Before stopping, restarting or updating, WindowsGSM sends the game's save command and waits — for Rust, ARK, 7 Days to Die, Palworld, Project Zomboid, Terraria, Unturned and Minecraft it knows the command. Others can set one in Settings → Stopping safely.",
        "Then it asks the game to stop and waits (30 s by default) before ending it. Force stop ends it at once, without saving.",
        "Some games' plugins stop them by just ending the process (ARK: Survival Evolved, DayZ, The Forest…) — Settings says so. For those, set a save command or turn on \"Send Ctrl+C before the game's own stop\": most servers save and shut down properly on Ctrl+C.",
    ], []],

    ["offsite", "Off-site backups (B2, R2, S3…)", "upload", "offsite off-site cloud backblaze b2 r2 cloudflare wasabi s3 amazon bucket upload", [
        "Agent settings → Off-site backups: a bucket on any S3-compatible service (Backblaze B2, Cloudflare R2, Wasabi, Amazon S3, MinIO…), its key, and how many backups to keep there. \"Test connection\" writes, reads and removes a small file. The secret key is stored encrypted.",
        "Then on a server's Backups tab, turn on \"Also upload each backup off-site\" (admins). Every new backup is uploaded in the background — the server isn't held up — and only the newest few are kept there. Any backup can also be uploaded with its upload button.",
        "If this PC loses its backups, \"Bring back\" in the Off-site list downloads one into the Backups list, to restore as usual.",
    ], []],

    ["rustplugins", "Rust plugins (uMod)", "puzzle", "rust umod oxide carbon plugin plugins update install kits", [
        "Rust servers with Oxide or Carbon (install one in the Add-ons tab) have a Plugins tab: search uMod, install (plugins a plugin requires come along), update all, remove. Oxide and Carbon load changes by themselves — no restart needed.",
        "Plugins you added by hand are left alone; \"Keep up to date\" on one makes WindowsGSM update it from uMod from then on. A plugin you've edited since it was installed is never overwritten by updates. \"Update them before every start\" keeps them current automatically.",
    ], []],

    ["scripts", "Scripts before start & after stop", "file", "script batch bat ps1 powershell rotate logs clean up before start after stop", [
        "Settings → Scripts runs your own .bat or .ps1 file before every start (including restarts and automatic restarts after a crash) and after every stop — e.g. rotate logs or clear out old files. Only those two kinds of file, and only admins can choose them, since they run a program on the PC.",
        "The script runs hidden in the server's game files folder, with WGSM_SERVER_ID, WGSM_SERVER_NAME, WGSM_SERVER_GAME, WGSM_SERVER_FILES and WGSM_SCRIPT_WHEN (start or stop). What it prints shows in the server's Logs tab. A script still running after the time limit (60 s unless you set another) is stopped.",
        "If a before-start script fails, the server starts anyway — unless you turn on \"Don't start if the before-start script fails\". Force stop doesn't run the after-stop script.",
    ], []],

    ["console", "Console, RCON & performance", "console", "console rcon window hidden command fps tps lag performance", [
        "Captured consoles show in the Console tab. Games that need their own window run hidden; Console → Show window brings it up on the machine's screen.",
        "RCON is the game's remote console. Set the same port and password in Settings and in the game's config; WindowsGSM then uses it for commands — and, for Rust, Minecraft and Source games, asks for the server's FPS or TPS every 5 minutes (Overview → Game performance; it can be turned off per server in Settings), so lag shows even when CPU looks fine.",
        "Never forward the RCON port on your router.",
    ], []],

    ["minecraft", "Minecraft: Paper, Fabric, plugins & mods", "box", "minecraft paper purpur fabric spigot plugin mod modrinth version java", [
        "Java Edition servers have a Minecraft tab: choose Vanilla, Paper, Purpur or Fabric and a Minecraft version. Updates then fetch the newest build of that software for the same version (pick \"Newest version\" to follow new releases).",
        "With Paper/Purpur (plugins) or Fabric (mods), search Modrinth in the same tab and install — required dependencies come along. \"Update all\" moves them to the newest version for your Minecraft version; jars you added yourself are never touched.",
    ], []],

    ["ark", "ARK: mods & clusters", "puzzle", "ark ascended asa ase survival mods curseforge cluster transfer obelisk", [
        "ARK servers have a Mods & cluster tab. ARK: Survival Ascended mods are CurseForge project ids (the Project ID on the mod's page), loaded top to bottom; the server downloads them itself when it starts.",
        "A cluster lets players carry characters, dinos and items between maps: give the servers the same cluster id and they share one folder. Restart them to apply.",
    ], []],

    ["templates", "Templates & copies", "copy", "template copy clone duplicate move another machine setup", [
        "More → Save as template keeps a server's settings and game config files (not its name, ports, Settings passwords or worlds — config files are copied whole). Pick the template when installing another server of that game, or apply it to an existing one.",
        "More → Copy server makes a full copy — worlds included — on the next free ports. With several machines, Move to another machine sends it through the hub.",
    ], []],

    ["users", "Users, roles & sign-in", "users", "user role permission admin owner operator viewer two-factor 2fa passkey password", [
        "Users & access: Owners can do everything; Admins manage servers and users; Operators run servers; Viewers look. Access can be given per server.",
        "Your account: two-factor codes and passkeys (Windows Hello, a phone or a security key) make sign-in safer.",
        "Locked out? On the machine, run WindowsGSM with --reset-password <user> (see the install guide).",
    ], [["/users", "Users & access"], ["/account", "Your account"]]],

    ["notify", "Notifications & Discord", "bell", "notification discord webhook bot alert crash", [
        "The bell shows crashes, updates, automations and more. Notification channels (Discord webhooks and others) are set up in Notifications; the Discord bot lets your community start, stop and check servers from Discord.",
    ], [["/notifications", "Notifications"]]],

    ["machines", "Several machines", "machine", "hub machine multi remote agent connect", [
        "Each PC runs the agent. One of them can be the hub: the others connect to it, and you manage every server from one panel. A machine that goes offline keeps running its servers; the hub shows what it last knew.",
    ], [["/machines", "Machines"]]],

    ["agent", "The agent (keeping it running)", "activity", "agent start stop restart windows startup service desktop tray signed in", [
        "The agent runs your servers. Agent settings → \"Start the agent when I sign in to Windows\" brings everything back after a reboot (pair it with Windows auto sign-in on a dedicated machine).",
        "The Start menu's WindowsGSM folder and the desktop app's tray icon can start, stop and restart the agent. Restarting it leaves game servers running.",
        "The desktop app can stay signed in on a machine you keep it open on.",
    ], [["/settings", "Agent settings"]]],

    ["trouble", "When something goes wrong", "warn", "error crash log problem broken fail troubleshoot health", [
        "• A server's Logs tab and the Logs page show what happened, newest first.",
        "• A server that keeps crashing stops being restarted automatically (\"Auto-restart paused\") — check its log, fix it, then start it.",
        "• Health checks look for missing tools, low disk space and misconfigured servers.",
        "• Storage shows what's using disk space.",
    ], [["/logs", "Logs"], ["/health", "Health checks"], ["/storage", "Storage"]]],
];

export default async function help(host) {
    setCrumbs({ label: "Help" });
    const box = h("input", { class: "input", type: "search", placeholder: "Search help — e.g. \"port\", \"rollback\", \"Paper\"", "aria-label": "Search help", autofocus: true });
    const index = h("nav", { class: "help-index" });
    const list = h("div", { class: "help-list" });
    host.append(
        h("div", { class: "page-head" }, h("div", {}, h("h1", { text: "Help" }), h("p", { text: "Short answers to common questions. Fields with a ? in Settings explain themselves too." }))),
        h("div", { class: "search help-search" }, icon("search"), box),
        h("div", { class: "help-layout" }, index, list));

    function paint() {
        const q = box.value.trim().toLowerCase();
        const words = q.split(/\s+/).filter(Boolean);
        const shown = TOPICS.filter(([, title, , keys, paras]) => {
            const text = `${title} ${keys} ${paras.join(" ")}`.toLowerCase();
            return words.every(w => text.includes(w));
        });
        clear(index).append(...shown.map(([id, title, ico]) => h("a", { href: `#${id}` }, icon(ico), h("span", { text: title }))));
        clear(list);
        if (!shown.length) { list.append(h("div", { class: "empty-note muted", text: "Nothing matches. Try another word — or ask in WindowsGSM's Discord." })); return; }
        for (const [id, title, ico, , paras, links] of shown) {
            const items = [];
            let ul = null;
            for (const p of paras) {
                if (p.startsWith("• ")) { if (!ul) { ul = h("ul"); items.push(ul); } ul.append(h("li", { text: p.slice(2) })); }
                else { ul = null; items.push(h("p", { text: p })); }
            }
            list.append(h("section", { class: "panel help-topic", id },
                h("div", { class: "panel-head" }, icon(ico), h("h3", { text: title })),
                h("div", { class: "panel-body" }, ...items,
                    links.length ? h("div", { class: "row wrap help-links" }, ...links.map(([href, label]) => h("a", { class: "btn sm", href }, label, icon("chevronRight")))) : null)));
        }
    }
    box.addEventListener("input", debounce(paint, 120));
    paint();
    const target = location.hash.slice(1);
    // After the router's own scroll-to-top (setTimeout, not rAF: rAF waits while the window is in the background).
    if (target) setTimeout(() => document.getElementById(target)?.scrollIntoView({ block: "start" }), 0);
}
