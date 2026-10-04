# Installing and running WindowsGSM

## Install

1. Download `WindowsGSM-<version>.zip` from the releases page and unzip it anywhere (your Downloads
   folder is fine).
2. Run **WindowsGSM.exe** from the unzipped folder. Setup opens:
   - **Install the app to** — defaults to `%LOCALAPPDATA%\Programs\WindowsGSM`. It installs for you only;
     no administrator rights are needed.
   - **Your game servers** — either *use your existing WindowsGSM folder* (the one with `servers`,
     `backups` and `configs`), or *start fresh* in a new folder. Keep this separate from the app folder.
   - Start menu / desktop shortcuts, and *Start with Windows*.
3. Press **Install**. WindowsGSM opens; on first run, create the owner account (or sign in with your old
   web dashboard account — they carry over).

Nothing else to install: the app brings its own .NET runtime. The panel uses the Microsoft Edge WebView2
Runtime, which is part of Windows 11 and current Windows 10.

## Switching from the current WindowsGSM

When you choose your existing folder, setup first runs a **dry run** and shows what switching means —
without changing anything:

- servers found, and any whose game isn't available (a plugin that doesn't compile, for example);
- ports two servers share, servers that start automatically;
- web dashboard accounts (same passwords and 2FA), the Discord bot's token and admins, backups, Crontab
  schedules — all carry over;
- what doesn't: the old app's own options (its window, start with Windows, the old dashboard on 8970).

The same report from a command line:

```powershell
"%LOCALAPPDATA%\Programs\WindowsGSM\versions\<version>\wgsm-agent.exe" --check-data "D:\WindowsGSM"
```

**One manager per folder.** The old app and WindowsGSM Next must never run the same servers. Close the old
app before switching (setup refuses while it's running from that folder). Game server processes that are
already running are picked up by the new agent.

**Trying it side by side first:** copy your WindowsGSM folder and point setup at the copy. The old app keeps
using the original; the new panel is on port 8971 (the old dashboard is on 8970). If you use the Discord
bot, only run one bot at a time — the new one is imported switched off.

## Updates

**Agent settings → Updates** shows the running version and whether a newer one is out (checked every few
hours; a notification says so). **Update** downloads it, checks its SHA-256, unpacks it next to the current
version, and restarts the agent on it — **game servers keep running** and are picked up again. The desktop
app then offers *Restart to finish updating* from its tray icon.

- **Go back** returns to the previous version (it's kept on disk) the same way.
- On a hub, **Machines → Update all** updates every machine that has an update waiting.
- **Where updates come from**: by default the GitHub releases of the project repository. Owners can point
  it at another repository (`owner/name`) or at their own JSON feed (see *Publishing a release*), and
  choose whether to include pre-releases.

## Starting and stopping the agent

The agent is the part that runs your servers and the web panel; the desktop app is just a window onto it.

- **Start menu → WindowsGSM**: *Start WindowsGSM agent*, *Stop WindowsGSM agent*, *Restart WindowsGSM agent*.
- **Tray icon**: shows whether the agent is running, with Start / Stop / Restart.
- **Agent settings → Restart agent** (owners) — from anywhere, a phone included.

Stopping the agent **leaves game servers running**; they're picked up again when it starts. To stop the servers
too, use the tray's *Stop everything and quit*.

**Staying signed in on the server itself.** Sign-ins end after a while without activity (Agent settings, default
8 hours) — but the desktop app keeps its own sign-in alive while it's open, and if it ever ends (an agent
restart, say) it signs back in on its own as whoever last signed in through it on that computer. Browsers and
phones keep the normal timeout. Turn it off from the tray (*Stay signed in on this computer*); signing out in the
app also stops it.

## Player counts (the game's query)

Player counts come from the game's own query (A2S for most Steam games). Unlike the old app, WindowsGSM asks
this machine — not the public address in the server's IP setting, which most routers won't answer from inside —
and when the configured query port doesn't answer it tries the usual ones (the game port, the next ports, 27015)
and uses the one that does. The **Players** tab says where the numbers come from, or why there aren't any.

## Firewall

Players on other computers can only reach a game server if Windows Firewall lets its program in. The old app
added a rule on every start, which only works as administrator — it always ran elevated. WindowsGSM runs as
you, so:

- A server without a rule (or with a rule that *blocks* it — what pressing Cancel on Windows' own "allow
  access" prompt leaves behind) shows a warning on its **Overview**, in **Health checks** and in its log.
- **Allow through firewall** (on the overview, or **Allow all through firewall** in Health checks) adds the
  rules with **one Windows administrator prompt** — on that computer's screen, so someone has to be there.
- If the agent happens to run as administrator, rules are added automatically on start.
- Rules are named `WindowsGSM - <server> (#id)` and removed when a server is deleted.

The panel's own port (8971) gets its rule when you turn on *Reachable from other computers*.

## If something goes wrong

- **Forgot your password (or lost your 2FA phone):** stop WindowsGSM (tray icon → **Stop everything and
  quit**), then run `WindowsGSM.exe --reset-password <username>` from the app folder — add `--disable-2fa`
  if you lost the phone. It shows a new temporary password; sign in and change it under *Your account*.
  (Without the launcher: `wgsm-agent.exe --reset-password <username> --data <your data folder>`.)
- **Shutting the PC down / maintenance:** tray icon → **Stop everything and quit** stops every game server
  normally, then WindowsGSM. *Quit* alone leaves the servers running.
- **A plugin update broke a game:** Game plugins → **Previous version** on that plugin puts back the one you
  had (and can switch again).
- **A server won't start because a port is taken:** the reason names the other server or says another
  program uses the port — stop it or change the port in the server's Settings.
- **Backups:** set *Also copy each backup to* (another drive or a network share) for a second copy, and use
  the ✓ button next to a backup to test it — every file is read back and checked, nothing is restored.

## More tools

- **Automations** (sidebar): *if this, then that* — stop a server with no players for 2 hours, restart one stuck
  at full CPU, tell me (with the last lines) when one crashes, let me know when someone joins.
- **Storage** (Manage): what each server takes, and one-click clean-up of old logs, game crash dumps, unfinished
  restores, old app versions and picture caches.
- **History** (server Settings, Game config, the file editor): every change made in the panel keeps the version it
  replaced — compare it with now, or put it back.
- **Copy server…** / **Move to another machine…** (a server's ⋯ menu): a copy on the next free ports with
  auto-start off; a move packs the server, sends it through the hub and unpacks it on the other machine.
- **Workshop** (server tab): Steam Workshop mods by link or collection link, kept up to date (optionally before
  every start). DayZ / Arma 3: `@Mod` folders, keys and `-mod=`; Conan Exiles: `modlist.txt`. Many games only let
  owners download their Workshop items — turn on *Use the Steam account* (Agent settings → Steam account).
- **Steam account** (Agent settings): sign in once; if Steam Guard asks for a code (email or Steam Mobile App),
  type it in the panel while it waits. The password is stored encrypted (Windows data protection), as are the
  Discord bot token, webhook URLs and certificate passwords. An old plain-text `bin\steamcmd\userData.txt`
  password is imported, and can be blanked from the same screen.
- **Roll back game update…** (⋯ menu, Steam games updated with DepotDownloader): puts an earlier build back from
  DepotDownloader's own manifests — not backups, so worlds and configs stay. Updates then go on hold (no
  auto-update or update on start) until you update by hand or resume them.
- **Save the world before stopping** (Settings → Stopping safely): the game's save command is sent before every
  stop, restart and update (known for Rust, ARK, 7 Days to Die, Palworld, Project Zomboid, Terraria, Unturned and
  Minecraft), then WindowsGSM waits for a clean shutdown (30 s by default). Force stop skips it.
- **Forward ports automatically** (Overview → Can players reach it?): UPnP port forwarding on the router for the
  game and query ports, UDP and TCP — renewed each start and every 30 minutes, removed when turned off or the
  server is deleted. The router must have UPnP turned on. RCON is never forwarded.
- **Game performance** (Overview): server FPS (Rust, Source games) or TPS (Minecraft), asked over RCON every
  5 minutes and charted. Needs RCON set up; can be turned off per server (Settings → Console & RCON).
- **Templates** (⋯ menu → Save as template / Apply a template; and when installing): settings and game config
  files (not the name, ports or the passwords in Settings; config files are copied whole); port numbers inside the files become the new server's.
- **Minecraft** (Java Edition tab): Vanilla, Paper, Purpur or Fabric at the version you pick (updates keep that
  software and version); plugins and mods from Modrinth with their dependencies, update all, remove. Jars you add
  yourself are left alone.
- **Mods & cluster** (ARK tab): ARK: Survival Ascended CurseForge mods in load order (`-mods=`), and clusters —
  several ARK servers sharing `-clusterid` and a `-ClusterDirOverride` folder.
- **Low disk space** (Automations): notifies when a drive WindowsGSM uses has less than the space you set.
- **Help** (sidebar): short answers to common questions, searchable.
- **Game files on another drive** (admins): when installing, *Where to put the game files* picks a drive and folder
  (e.g. `E:\GameServers`; each server gets its own folder inside); later, a server's ⋯ menu → *Move files to
  another drive…* copies, checks, switches over and removes the old copy (and moves them back). Under the hood
  `servers\<id>\serverfiles` becomes a junction to that folder, so plugins, updates, backups and the file manager
  work unchanged; settings, logs and the backup list stay in the WindowsGSM folder. Local drives only (not network
  shares). A disconnected drive is named and the server won't start until it's back; deleting the server deletes
  its files there too.
- **Passkeys** (Your account): sign in with a fingerprint, face or phone. Browsers only allow them on the panel's
  HTTPS address (or localhost), and a passkey works on the address it was made on.
- **Uptime** (a server's Overview → Details): the share of the last 24 hours, 7 days and 30 days it was running,
  counted from when it was first seen (time the agent wasn't running counts as down). The overview's player card
  also shows today's peak.
- **Appearance** (Your account, per browser): dark/light, an accent colour, a compact server list, and the
  console's text size and line wrapping (also on the Console tab: A−, A+, Wrap). The browser tab shows
  **(⚠ n)** when something needs you (auto-restart gave up, a machine offline) or **(n)** for unread notifications.
- **Scripts** (Settings → Scripts, admins): your own `.bat` or `.ps1` run before every start (restarts and crash
  restarts too) and after every stop (not Force stop) — rotate logs, clean up files. Only those two kinds of file.
  The script runs hidden in the server's game files folder with `WGSM_SERVER_ID`, `WGSM_SERVER_NAME`,
  `WGSM_SERVER_GAME`, `WGSM_SERVER_FILES` and `WGSM_SCRIPT_WHEN` (`start`/`stop`); its output goes to the server's
  log; it's stopped after a time limit (60 s by default). A failing before-start script doesn't stop the start
  unless you say so. The old app's Rust "batch file" setting carries over as the before-start script, now for every
  game.
- **Off-site backups** (Agent settings → Off-site backups, admins): any S3-compatible bucket — Backblaze B2, Cloudflare R2,
  Wasabi, Amazon S3, MinIO — with *Test connection*; the secret key is stored encrypted. Each server opts in on its
  Backups tab (*Also upload each backup off-site*); every new backup is then uploaded as a job of its own (the server
  isn't held up; a failed upload notifies and the local backup is kept), and the newest N per server are kept off-site
  (`<folder>/<machine id>/server-<id>/`). *Bring back* downloads an off-site backup into the Backups list, to restore
  as usual.
- **Rust plugins** (Plugins tab on Rust servers with Oxide or Carbon): search umod.org (most downloaded first),
  install with the plugins it requires (`// Requires:`), update all, remove — checked against uMod's checksum, no
  restart needed (Oxide/Carbon reload plugin files themselves). Plugins added by hand are left alone unless you
  choose *Keep up to date*; a plugin edited since it was installed is never overwritten. Optionally *Update them
  before every start*. Tracked in `serverfiles\wgsm-umod.json`.
- **Stopping games whose plugin just ends the process** (ARK: Survival Evolved, BlackWake, DayZ, Outlaws of the Old
  West, Onset, Stormworks, The Forest — Settings flags these, and community plugins that do the same): set a save
  command, or turn on *Send Ctrl+C before the game's own stop* (Settings → Stopping safely). When a plugin "presses"
  Ctrl+C on a server that has no window of its own (captured, or found again after an agent restart), the Ctrl+C
  now reaches that server's console instead of going nowhere.
- **Console windows**: servers whose output isn't captured into the panel run in their own console window on the
  server's screen — **Show window / Hide window** on the Console tab, or **Show the console window on this machine**
  in Settings (applies as soon as you save). Commands typed in the Console tab go into that window, and the window
  is found again after the agent restarts. Its close button is greyed out (closing it would end the game without
  saving) — stop the server from the panel. Captured servers have no separate window.

## Uninstall

*Settings → Apps → Installed apps → WindowsGSM → Uninstall* (or `WindowsGSM.exe --uninstall`). This
removes the app, its shortcuts and startup entries. **Your game servers, backups and settings stay** in
their folder — install again later and point setup at it.

## Folder layout

```
%LOCALAPPDATA%\Programs\WindowsGSM\      the app
├─ WindowsGSM.exe                         launcher — starts the current version; setup; switching; rollback
├─ install.json                           current and previous version, and the data folder
└─ versions\<version>\                    WindowsGSM.exe (desktop), wgsm-agent.exe, everything they need

<your data folder>\                       game servers and everything about them (the legacy layout)
├─ servers\<id>\configs, serverfiles      as before
├─ backups\, plugins\, logs\, bin\        as before
└─ configs\next\                          accounts, machines, notifications, history.db, discord-bot.json…
```

Launcher options: `--minimized` (to the tray), `--agent` (just the agent — what *start at sign-in* runs), `--reset-password <user> [--disable-2fa]`,
`--rollback`, `--setup [--data <folder>]`, `--uninstall`.

## Where the logs are

**Logs** in the panel (Manage → Logs) shows all of these — Activity (who did what), the app log, crashes,
the Discord bot, plugins and diagnostics — with search, a *problems only* filter and downloads. On disk they're
in the data folder's `logs\`, with the same names the old app used:

| File | What's in it |
|---|---|
| `L<date>.log` | Everything that happened: servers (start, stop, crash, update…), the agent (`[Agent]`), the Discord bot's problems (`[Discord]`). The panel's Logs tab shows the server lines live. |
| `servers\<id>\crash_<time>.log` | A game server's crash: exit code, command line, last console output. |
| `CRASH_<date>.log` | The agent or the desktop app crashing. The next start also raises an *agent crashed* notification. |
| `L<date>-DiscordBot.log` | Every bot command and button press, who used it, and refusals. |
| `plugins\<Plugin>.cs.log`, `pluginsImportError.log` | Why a plugin didn't compile or load. |
| `Server_<id>_<program>_execLog.log` | Output of programs run from Crontab schedules. |
| `agent\agent-<date>.jsonl` | Detailed diagnostics (kept 14 days). |

Crashes, crash loops, failed jobs, machines going offline and agent crashes are also in the notification
centre (the bell), and can be sent to your own Discord channel or webhook (Notifications → Add). The old
app also posted crash logs to the original author's Discord; this version sends nothing anywhere you
haven't set up.

## Adding another machine

1. Install WindowsGSM on each machine.
2. On the one you'll use as the **hub**, turn on *Reachable from other computers* (Agent settings) and open
   **Machines → Add a machine**. It shows a one-time code and the addresses to use.
3. On the other machine, **Machines → Join a hub**: enter one address and the code.

Only the hub needs to be reachable; members connect out to it. Users and permissions are managed on the hub.

## Discord bot

**Manage → Discord bot**: paste the bot's token (Discord Developer Portal → your application → Bot → Reset
Token), add the people who may use it (their Discord user IDs) and which servers each can control, switch it
on, and invite it with the button. Commands: `/panel` (private control panel with buttons), `/list`,
`/stats`. It covers every machine the panel controls.

## Notes for plugin authors

Plugins are unchanged: the same `WindowsGSM.<Game>` repositories, the same `<Game>.cs\<Game>.cs` layout, the
same `Plugin` metadata and methods, compiled at run time against the same public namespaces (see
ARCHITECTURE §6.1). What's different:

- **No WPF.** There's no WindowsGSM window behind a plugin; don't show dialogs or touch `MainWindow`
  beyond its statics.
- **Questions** (`UI.CreateYesNoPromptV1`, e.g. "accept the EULA?") are answered up front in the install
  wizard, or live from the panel (a phone, even); unanswered means *no*.
- **Downloads**: Steam content goes through DepotDownloader via the existing `SteamCMD` API; nothing to
  change in plugins.
- Plugins are found and installed from the panel (**Game plugins**) by searching GitHub for repositories
  named `WindowsGSM.<Game>` — name yours that way to be found. Owners can also **Add from a GitHub link**
  (any repository) or **Add your own**: the `.cs` file (plus an optional logo PNG), or a `.zip` of the plugin
  folder. The logo is `<Game>.cs\<Game>.png`, as before; it's shown on the plugins page and the game's tiles.

## Publishing a release (maintainers)

GitHub does it: push a version tag and the **Release** workflow (`.github/workflows/release.yml`) runs the
tests, builds the package and publishes the release with both files.

```powershell
git tag v2.0.0-alpha.4
git push origin v2.0.0-alpha.4
```

The tag is the version — nothing to edit first. A version with a dash is published as a pre-release. Or run it
from GitHub: Actions → Release → Run workflow, and type the version. If a test fails, nothing is released.
Every push to `main` is also built and tested (`ci.yml`).

To build a package by hand instead:

```powershell
.\tools\publish.ps1 -Version 2.0.1          # or 2.1.0-beta.1 for a pre-release
```

This writes `dist\WindowsGSM-<version>.zip` and `dist\WindowsGSM-<version>.zip.sha256`. Create a GitHub
release in the update feed's repository (mark pre-releases as such) and attach **both** files — installed
copies look for assets named exactly that way and refuse a download without a matching checksum.

A self-hosted feed is a JSON array next to the files:

```json
[{ "version": "2.0.1", "zip": "WindowsGSM-2.0.1.zip", "sha256": "WindowsGSM-2.0.1.zip.sha256", "prerelease": false, "notes": "What's new" }]
```
