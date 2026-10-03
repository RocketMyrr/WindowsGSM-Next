# WindowsGSM Next

**A game server manager for Windows: install, run, update and back up dedicated game servers, and control them
from a browser, a phone, the desktop app or Discord — on one PC or many.**

WindowsGSM Next is the rebuild of [WindowsGSM](https://github.com/WindowsGSM/WindowsGSM). Your servers keep running
whether or not any window is open, every existing game plugin keeps working, and your current WindowsGSM folder —
servers, backups, accounts, schedules, Discord bot — carries straight over.

> **Status: 2.0.0-alpha.4 (pre-release).** Everything below is built and tested, but it's an alpha: keep backups,
> and try it on a copy of your WindowsGSM folder first if you're switching from the old app.

---

## Contents

- [Highlights](#highlights)
- [What's new in 2.0.0-alpha.4](#whats-new-in-200-alpha4)
- [Requirements](#requirements)
- [Install](#install)
- [Coming from WindowsGSM](#coming-from-windowsgsm)
- [First steps](#first-steps)
- [Features](#features)
- [Several machines, one panel](#several-machines-one-panel)
- [The Discord bot](#the-discord-bot)
- [Updates](#updates)
- [Security](#security)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)
- [For developers](#for-developers)

---

## Highlights

- **Servers that don't depend on a window.** A background agent runs your servers. Close the panel, restart the
  agent, or update WindowsGSM itself: game servers keep running and are picked up again.
- **One panel everywhere.** The same modern panel in the desktop app, any browser and on your phone, in dark or
  light.
- **Many PCs, one panel.** Pair your other machines with a hub and control every server from one place, with
  the same accounts and permissions.
- **Every plugin still works.** All 46 built-in games, plus community plugins found and installed from the panel
  (Enshrouded, Palworld, Valheim and many more). Compatibility is enforced by tests.
- **Fast, reliable Steam downloads.** DepotDownloader is the standard for installing and updating Steam servers,
  including roll back to an earlier game build.
- **Safe by default.** Accounts with roles and per-server permissions, two-factor sign-in and passkeys, an audit
  log of who did what, and secrets stored encrypted.
- **Help built in.** A ? next to settings, page tips and a searchable Help page.

## What's new in 2.0.0-alpha.4

**Game files on other drives**
- Choose a drive and folder for a server's game files when installing, or move an existing server's files later
  (⋯ menu → *Move files to another drive…*). Spread big servers across drives.
- A server's Overview shows where its files are, with **Open folder** when you're using the panel on that PC.

**Console windows that actually work**
- Servers whose console isn't captured into the panel now get a real console window of their own. **Show the
  console window on this machine** shows or hides it the moment you save — no restart needed — and the Console
  tab's *Show window* button works too.
- Commands typed in the Console tab go into that window, and the window is found again after the agent restarts.
- Its close button is greyed out: closing a console window ends the game without saving. Stop it from the panel.

**Graceful stops for every kind of game**
- Plugins that stop a game by typing `quit` or `stop` into its window now reach it (before, the stop silently
  failed and the game was killed when the timeout ran out).
- Plugins that "press" Ctrl+C or keys (Conan Exiles, 7 Days to Die, Space Engineers, Minecraft Bedrock) now send
  them to the game's own console — never into whatever app you happen to be using.
- If a plugin's own way doesn't stop the game in time, it gets a real Ctrl+C and 10 more seconds before it's
  ended.

**Discord bot with several machines**
- `/panel` pages through servers 25 at a time — before, servers past the first 25 (usually every other machine's)
  couldn't be picked.
- `/stats` and `/list` stay within Discord's limits for big fleets (`/stats` used to fail from 7 machines on).
- Machines are asked in parallel; one slow machine no longer holds up the panel, and the panel says which one
  didn't answer.
- Machines that report to a hub leave the bot to the hub, and a second copy answering with the same token is
  detected and shown.

**Also**
- Sign-in cookie keys are now stored encrypted (fixes the *No XML encryptor configured* warning in Diagnostics).
  Everyone signs in once more after updating.
- Server **Settings** has a tidier layout: one column of sections on a shared grid, so fields, switches and their
  ? buttons line up.
- The performance chart no longer shows "1 pl." on servers nobody has joined.

## Requirements

- **Windows 10 or 11, 64-bit.** Windows Server works too.
- **No .NET install needed**: WindowsGSM brings its own runtime.
- The desktop app uses the Microsoft Edge **WebView2 Runtime**, part of Windows 11 and current Windows 10.
- **No administrator rights** to install or run. Windows asks once, if you want, to let a game through the
  firewall.

## Install

1. Download `WindowsGSM-<version>.zip` from the
   [releases page](https://github.com/RocketMyrr/WindowsGSM-Next/releases) and unzip it anywhere.
2. Run **WindowsGSM.exe**. Setup walks you through:
   - **where the app goes** — `%LOCALAPPDATA%\Programs\WindowsGSM` by default (for you only, no admin needed);
   - **where your game servers live** — your existing WindowsGSM folder, or a new one;
   - shortcuts and *Start with Windows*.
3. Press **Install**. The panel opens. Create the owner account, or sign in with your old web dashboard account.

The panel is at **http://localhost:8971** on that PC. Turn on *Reachable from other computers* (Agent settings)
to use it from your phone or another PC.

Full details: **[docs/INSTALL.md](docs/INSTALL.md)**.

## Coming from WindowsGSM

Point setup at your current WindowsGSM folder. It first runs a **dry run** and shows what switching means,
changing nothing until you press Install:

- **Carries over:** servers, backups, web dashboard accounts (same passwords and 2FA), Crontab schedules, the
  Discord bot's token and admins, and the Steam login.
- **Flagged for you:** games whose plugin doesn't load, ports two servers share, servers set to auto-start.
- **Left behind:** the old app's own window options and its dashboard on port 8970.

**Only one manager per folder.** Close the old WindowsGSM before switching; game servers that are already running
are picked up by the new agent. To try it side by side first, copy your folder and point setup at the copy. The
new panel is on port 8971 and the old dashboard stays on 8970. The imported Discord bot starts switched off, so
two bots don't answer at once.

## First steps

1. **Install a server**: *Install a server* in the sidebar, pick a game, and choose its name, ports and (if you
   like) a template or another drive for its files.
2. **Start it** and watch the **Console** tab.
3. **Check it's reachable**: Overview → *Can players reach it?* checks the port, Windows Firewall, your router
   (UPnP can forward ports for you) and Steam's server list.
4. **Make it look after itself**: Settings → *Restart after a crash*, *Update automatically*, *Start with the
   agent*; Backups → a schedule; Settings → *Stopping safely* to save the world before every stop.
5. **Invite people**: Users → add accounts with just the servers and actions they need.

## Features

### Running servers
- Start, stop, restart, force stop, with the world saved first (known save commands for Rust, ARK, 7 Days to Die,
  Palworld, Project Zomboid, Terraria, Unturned and Minecraft, or your own).
- **Auto-restart** after a crash, with back-off. It pauses itself if a server keeps crashing, rather than looping.
- **Memory guard**: a clean restart when a server's memory stays too high.
- CPU priority and core affinity, per server.
- **Console**: live output in the panel, commands with history, or **RCON**. Non-captured servers get their own
  console window you can show and hide.
- **Logs** per server, plus crash reports with the last console lines.

### Installing and updating
- Install wizard for every built-in game and installed plugin, with templates and a choice of drive.
- **Steam updates via DepotDownloader**: faster, with the Steam account signed in once (Steam Guard codes typed
  right in the panel). SteamCMD stays available per server for games that need it.
- **Roll back a game update** to an earlier build, from DepotDownloader's own manifests. Worlds and configs
  aren't touched, and updates go on hold until you resume them.
- Update automatically, update before every start, and **Update available** badges.
- Steam branches (betas), with passwords.

### Game-specific tools
- **Minecraft Java**: Vanilla, Paper, Purpur or Fabric at the version you choose. Plugins and mods from
  **Modrinth** with dependencies, update all, remove.
- **ARK: Survival Ascended**: CurseForge mods in load order, and clusters.
- **Steam Workshop**: mods by link or collection, kept up to date (DayZ / Arma 3 `@Mod` folders and keys,
  Conan Exiles `modlist.txt`).
- **Add-ons** such as Oxide/uMod, tracked so updates never overwrite your custom builds.
- **Game config** editor for each game's own config files, with history.

### Files and backups
- **File manager**: browse, edit, upload, download, rename and delete, with **Open in Explorer** on that PC.
- **Backups**: on demand or on a schedule, kept by count or age, optionally copied to another drive or network
  share, and **tested** without restoring (every file read back and checked). Restore with one click.
- **Game files on another drive**, moved any time.
- **Storage** page: what each server takes, and one-click clean-up of old logs, crash dumps and caches.
- **History** for settings and config files: compare with now, or put an earlier version back.
- **Copy a server**, or **move it to another machine**.

### Watching and automating
- **Overview** with live server cards, a *Needs attention* panel, and per-machine health.
- **Charts** for CPU, memory, players and game performance (server FPS/TPS over RCON), from the last hour to a
  year, plus uptime, peak players and who plays most.
- **Players** tab: who's on, recent visitors, and why counts are missing if they are.
- **Schedules**: restarts, updates, backups and commands on a timetable, with warnings in game chat first.
  Add one schedule to many servers.
- **Automations**: *if this, then that*. Stop an empty server after 2 hours, restart one stuck at full CPU, tell
  me when someone joins, warn when a drive is low on space.
- **Notifications**: a bell for everything that happened (crashes, restarts, updates, failed jobs, machines going
  offline), plus Windows notifications from the desktop app and your own Discord or webhook channels.

### Getting around
- Command palette (**Ctrl+K**), keyboard shortcuts, server tags, saved views, and fleet actions (start, stop,
  restart, update or back up everything shown).
- Phone layout, dark/light themes, accent colours, compact list.
- **Help** page with searchable answers, tips on each page, and a ? beside settings.

## Several machines, one panel

Run WindowsGSM on every PC that hosts servers, then pair them:

1. On the PC you'll use as the **hub**, turn on *Reachable from other computers* (Agent settings) and open
   **Machines → Add a machine** for a one-time code.
2. On each other PC, **Machines → Join a hub** with the hub's address and the code.

Only the hub needs to be reachable; the others connect out to it. From then on:

- The overview groups servers by machine, each with a health card.
- Everything works on any machine from the hub: console, files, config, backups, installs and updates. Users and
  permissions are set on the hub, and each machine still checks them.
- **Machines → Update all** updates every machine.
- If a machine drops off, its servers keep running. The hub shows their last known state until it reconnects.
- Move a server from one machine to another from its ⋯ menu.

## The Discord bot

Control your servers from Discord with **`/panel`** (a private control panel with buttons), **`/list`** and
**`/stats`**.

1. Create a bot at [discord.com/developers](https://discord.com/developers) → New Application → Bot → *Reset
   Token*, and copy the token.
2. In WindowsGSM: **Manage → Discord bot**. Paste the token, add the people who may use it (their Discord user
   IDs), and choose which servers each one controls: all, everything on one machine, or specific servers.
3. Switch it on, save, and use **Invite to a server**.

**With several machines, run the bot on the hub.** It covers every machine, with each person's permissions
checked on the machine itself, and every action lands in that machine's audit log as *Name (Discord)*. Machines
that report to a hub don't run their own bot. Use one token in one place only: two copies race to answer, and
WindowsGSM warns you if it sees that happening.

## Updates

**Agent settings → Updates** shows your version and whether a newer one is out (checked every few hours).
**Update** downloads it, verifies its SHA-256 checksum, and restarts the agent on the new version. **Game servers
keep running throughout.** The previous version is kept: **Go back** returns to it the same way. Pre-releases are
optional, and owners can point updates at another repository or their own feed.

## Security

- **Accounts and roles**: owner, admin, operator, member and viewer, with per-server permissions on top.
- **Two-factor sign-in** (authenticator app) and **passkeys** (fingerprint, face or phone).
- **Audit log** of every action: who, from where, and through what (panel, hub, Discord).
- **Encrypted secrets**: the Steam password, Discord bot token, webhook URLs, certificate passwords, the hub link
  and sign-in cookie keys are protected with Windows data protection for your account.
- **HTTPS** with your own certificate or free **Let's Encrypt** certificates. Only owners can install plugins,
  since plugins run as code.
- Nothing is sent anywhere you haven't set up. The old app posted crash logs to its author's Discord; this one
  doesn't.

## Troubleshooting

| Problem | What to do |
|---|---|
| **Forgot your password or lost your 2FA phone** | Tray icon → *Stop everything and quit*, then run `WindowsGSM.exe --reset-password <user>` (add `--disable-2fa` for a lost phone) and sign in with the temporary password. |
| **Players can't connect** | Overview → *Can players reach it?* checks the port, firewall, router and Steam list, and offers fixes. |
| **No player counts** | The Players tab says which query port answers, or why none does. |
| **Server won't start: port in use** | The message names the server or program using it. Change the port in Settings. |
| **A plugin update broke a game** | Game plugins → *Previous version* on that plugin. |
| **A game update broke the server** | ⋯ menu → *Roll back game update…* |
| **No console window appears** | Turn off *Capture the console here* in Settings (captured servers show output in the Console tab instead), then restart the server once. |
| **Something else** | **Logs** (Manage → Logs) has the activity log, app log, crashes, Discord bot log and diagnostics, with a *problems only* filter. |

Stopping the agent (or quitting the desktop app) leaves game servers running. To shut everything down, use the
tray's **Stop everything and quit**.

## FAQ

**Do my game servers stop when I update WindowsGSM or restart the agent?**
No. They keep running and are picked up again. Servers whose output is captured into the Console tab show new
output again after their next restart; RCON works meanwhile.

**Does it need to run as administrator?**
No. It runs as you. Windows Firewall rules for games need one admin prompt, offered from the panel.

**Will my plugins work?**
Yes. The same plugin files and repositories, compiled the same way. Search and install community plugins from
**Game plugins**.

**Can I use it from my phone?**
Yes. Turn on *Reachable from other computers*, and ideally HTTPS, then open the panel's address.

**Where is my data?**
In the data folder you chose (the familiar `servers`, `backups`, `configs`, `logs` layout). Uninstalling the app
never deletes it.

---

## For developers

### Build and test

Requires the **.NET 10 SDK** on Windows.

```powershell
dotnet build WindowsGSM.Next.sln
dotnet test WindowsGSM.Next.sln
```

The plugin-compatibility tests compile every community plugin in the legacy repo's
`WindowsGSM-Remaster/WindowsGSM-Plugin-Development/Plugins` through the real plugin loader. They find it when that
folder sits next to (or above) this repository, or wherever `WGSM_PLUGIN_KIT` points; without it they're skipped.
Every push to `main` is built and tested by GitHub Actions (`.github/workflows/ci.yml`).

### Try the agent

Point it at a **copy** of a WindowsGSM data folder, never the live one: two managers must not drive the same
servers, and it refuses a folder a running WindowsGSM is using.

```powershell
dotnet run --project src/WindowsGSM.Agent -- --data "D:\wgsm-copy" --no-autostart
```

It listens on `http://localhost:8971`. Sign in with the copy's dashboard accounts, or create the owner on first
visit. `--no-autostart` keeps servers set to auto-start from starting.

### Try the desktop app

```powershell
dotnet run --project src/WindowsGSM.Desktop -- --data "D:\wgsm-copy"
```

It opens the panel for that folder's agent, starting `wgsm-agent.exe` from its own folder if nothing is running.

### Drive the engine directly

```powershell
dotnet run --project tools/WindowsGSM.Harness -- "D:\wgsm-copy" --background
```

Type `help` for the commands (`list`, `start 1`, `console 1`, `cmd 1 say hi`, `update 1`, `backup 1`, …).

### Releases

Push a version tag; the **Release** workflow tests, builds and publishes the zip and its `.sha256`:

```powershell
git tag v2.0.0-alpha.4
git push origin v2.0.0-alpha.4
```

A version with a dash is published as a pre-release. To build a package by hand:
`.\tools\publish.ps1 -Version 2.0.1` → `dist\WindowsGSM-<version>.zip` and `.zip.sha256`. Installed copies
check this repository's GitHub releases for updates, so they can only see releases they can reach: a private
repository's releases aren't visible to them.

### Layout

```
src/WindowsGSM.Core/       the engine: servers, installs, updates, backups, schedules
                           (Functions/, GameServer/, Installer/ keep their legacy paths for plugins)
src/WindowsGSM.Contracts/  API shapes and permissions
src/WindowsGSM.Agent/      wgsm-agent: v2 API, live events, auth, hub, Discord bot, web panel (wwwroot/)
src/WindowsGSM.Desktop/    WindowsGSM.exe desktop app: tray icon and window around the panel
src/WindowsGSM.Launcher/   setup, updates, version switching and rollback
tests/                     engine and API tests (real processes, real hub links)
tools/                     publish.ps1, the engine harness, legacy-fix porting helper
docs/                      install guide, architecture, roadmap, porting notes
```

### Docs

- [Install and run](docs/INSTALL.md): the full user guide
- [Architecture](docs/ARCHITECTURE.md): the design and the reasoning behind it
- [Roadmap](docs/ROADMAP.md): the phases and each one's exit test
- [Porting fixes from the legacy app](docs/PORTING.md)
- [Bugs found in the legacy app](docs/LEGACY-BUGS.md)

### Credits

Built on [WindowsGSM](https://github.com/WindowsGSM/WindowsGSM) by its original author and contributors, and the
community's game plugins.
