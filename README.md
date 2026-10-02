# WindowsGSM Next

The next generation of WindowsGSM, built beside the current app so that one keeps running (and keeps
getting fixes) until this is ready.

- **Engine without a window** — servers keep running whether or not any UI is open.
- **One UI everywhere** — desktop window, browser and phone share the same polished interface.
- **Many machines, one panel** — every machine runs an agent; any agent can be the hub.
- **Every existing plugin keeps working** — enforced by tests.
- **DepotDownloader** is the standard for installing and updating Steam servers.

Status: **Phase 2 — Engine, built.** With no UI at all, the engine can do all of this, proven by tests
that use real processes:

- install, import and delete servers;
- start, stop, restart and kill them, auto-restart on a crash, and reattach after a restart;
- update them;
- back them up and restore them;
- manage add-ons;
- send console and RCON commands;
- run scheduled tasks;
- monitor players, health and memory;
- send Discord alerts.

**Phase 3 — Agent & API, built.** `wgsm-agent` runs the engine headless and serves the new v2 API: REST
for everything above, a live WebSocket event stream, sign-in with roles, per-server permissions, 2FA,
sessions and an audit log, HTTPS (including Let's Encrypt), and start-at-logon. Existing web-dashboard
accounts carry over. See [docs/ROADMAP.md](docs/ROADMAP.md).

**Phase 4 — Web UI, built.** The agent serves the new panel:
- an overview with live server cards and a *Needs attention* panel;
- a page for each server with a live console, logs, players, a file editor, backups, add-ons, schedules
  and settings;
- an install wizard;
- users and per-server access, 2FA with a QR code, a Logs page (audit log, app log, crashes, Discord bot, plugins), health checks and agent settings;
- dark, light and match-the-system themes, and a phone layout.

**Phase 5 — Multi-machine, built.** One panel controls several computers:
- On the machine you want as the hub, open *Machines → Add a machine* to get a one-time code.
- On the other machine, open *Machines → Join a hub* and enter the hub's address and the code.
- The other machine connects out to the hub, so only the hub needs to be reachable (turn on
  *Reachable from other computers* in its Agent settings).
- The overview then groups servers by machine, with a health card for each.
- Everything else (console, files, game config, backups, installs) works on any machine from the hub,
  with the permissions set on the hub.
- If a machine drops off, its servers keep running. The hub shows their last known state until the
  machine reconnects.

**Phase 6 — Rich features & desktop, built.**
- **Notifications:** a bell with everything that happened (crashes, restarts, updates, failed jobs,
  machines going offline) and Discord or webhook channels for any of it.
- **History:** charts for 24 hours up to a year, per server and per machine, plus who plays most and
  who came by recently.
- **Game plugins:** find community plugins on GitHub and install, update or remove them. Owners only,
  since plugins run as code on the machine.
- **Schedules across every server:** what runs next everywhere, and one schedule added to many servers.
- **Getting around:** a command palette (Ctrl+K), keyboard shortcuts, server tags, saved views, and
  fleet actions (start, stop, restart, update or back up everything shown).
- **The desktop app** (`WindowsGSM.exe`): a tray icon and a native window around the panel, with
  Windows notifications.
- **Discord bot:** `/panel`, `/list` and `/stats` for every machine, set up under Manage → Discord bot.
  Your old bot's token and admins are brought over.
- **Can players reach it?** A check on each server covering the port, firewall, router and Steam's server
  list.
- **Update available** badges, and warnings in game chat before scheduled restarts.

**Phase 7 — Install & release, built.**
- **A real install:** unzip, run `WindowsGSM.exe`, and choose where the app and your game servers go. It
  installs for you only, needs no admin rights, and needs no .NET install.
- **Keep your servers:** point it at your current WindowsGSM folder. It first shows a dry-run report of
  what carries over, and changes nothing until you press Install.
- **One-click updates** from GitHub releases, with a checksum check. Game servers keep running while the
  agent restarts, you can roll back instantly, and the hub can update every machine.
- See **[docs/INSTALL.md](docs/INSTALL.md)**.

## Build a release

```powershell
.\tools\publish.ps1            # dist\WindowsGSM-<version>.zip + .sha256
```

## Try the agent

Point it at a **copy** of a WindowsGSM data folder, not the live one: two managers must never drive the
same servers, and it refuses a folder a running WindowsGSM is using. It listens on
`http://localhost:8971` (the legacy dashboard is 8970, so both can run).

```powershell
dotnet run --project src/WindowsGSM.Agent -- --data "D:\wgsm-copy" --no-autostart
```

If the copy has legacy dashboard accounts, sign in with those. Otherwise create the owner with
`POST /api/v2/setup` from the same machine (the agent prints a one-time code for doing it from elsewhere).
`--no-autostart` keeps it from starting servers that are set to auto-start; drop it when running for real.

## Try the desktop app

It opens the panel for a data folder's agent, starting `wgsm-agent.exe` (from its own folder) if
nothing is running there. Close the window and it stays in the tray; quit from the tray menu.

```powershell
dotnet run --project src/WindowsGSM.Desktop -- --data "D:\wgsm-copy"
```

## Drive the engine directly (harness)

Point it at a **copy** of a WindowsGSM data folder, not the live one. It refuses to open a folder
that a running WindowsGSM is already managing.

```powershell
dotnet run --project tools/WindowsGSM.Harness -- "D:\wgsm-copy" --background
```

Type `help` for the commands (`list`, `start 1`, `console 1`, `cmd 1 say hi`, `update 1`,
`backup 1`, `restore 1 <name>`, `jobs`, …).

## Docs

- [Architecture](docs/ARCHITECTURE.md) — the design and the reasoning behind every decision
- [Roadmap](docs/ROADMAP.md) — phases, order, and the exit test for each
- [Porting fixes from the legacy app](docs/PORTING.md)
- [Bugs found in the legacy app](docs/LEGACY-BUGS.md) — several affect the running app today

## Build and test

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build WindowsGSM.Next.sln
dotnet test WindowsGSM.Next.sln
```

The plugin-compatibility tests compile every community plugin in the legacy repo's
`WindowsGSM-Remaster/WindowsGSM-Plugin-Development/Plugins` through the real plugin loader. They find it
when that folder sits next to (or above) this repository, or wherever `WGSM_PLUGIN_KIT` points; without it
they're skipped and everything else still runs.

## Layout

```
src/WindowsGSM.Core/      the engine (Functions/, GameServer/, Installer/ keep their legacy paths)
src/WindowsGSM.Contracts/ API shapes and permissions
src/WindowsGSM.Agent/     wgsm-agent — v2 API, event stream, auth, web UI host
tests/                    engine tests + API tests
docs/                    architecture, roadmap, porting guide
tools/                   Show-LegacyChanges.ps1 — carry legacy fixes across
tools/WindowsGSM.Harness wgsm-harness — drive the engine from a console
```
