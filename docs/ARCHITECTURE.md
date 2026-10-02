# WindowsGSM Next — Architecture

This is the design for the next generation of WindowsGSM: an engine that no longer lives inside a
window, one UI that works everywhere, and one panel for many machines — while every existing game
plugin keeps working unchanged.

It lives beside the current app (`../WindowsGSM-Remaster/`), which stays fully usable for fixes until
this is ready to replace it. See [ROADMAP.md](ROADMAP.md) for the phase plan and [PORTING.md](PORTING.md)
for moving fixes across.

---

## 1. Why a new layout

The current app works, but one design choice limits everything: **the WPF window *is* the program.**

| Finding (measured in the legacy code) | Consequence |
|---|---|
| `MainWindow.xaml.cs` is 9,305 lines / ~300 methods: engine, UI, web bridge and Discord bridge together | Nothing can be tested in isolation; every change risks unrelated breakage |
| The on-screen server grid is the source of truth (`ServerGrid` referenced 132×, `_serverMetadata` 140×) | The web dashboard and Discord bot must borrow the UI thread to read anything (61 dispatcher hops) |
| Servers are managed by the window process | Close or crash the window → your servers lose their manager |
| Even choosing a free port reads the *displayed* grid | Core logic depends on what the UI happens to show |
| Single-file publish extracts web assets to `%TEMP%` | Temp cleanup silently broke the dashboard (the 404 incident) |

The fix is structural, not cosmetic: pull the engine out, and make every UI a client of it.

## 2. Goals

1. **Engine independent of any UI.** Servers keep running when the window closes or crashes.
2. **One polished UI everywhere** — desktop window, any browser, phone — built once.
3. **Many machines, one panel.** Every machine runs an agent; any agent can act as the hub.
4. **100% plugin compatibility.** Built-in and community `.cs` plugins work unchanged.
5. **Zero-migration adoption.** Point it at an existing WindowsGSM data folder and go.
6. **Live, not polled.** Status, console, progress and metrics are pushed as they happen.
7. **DepotDownloader is the standard** for installing and updating Steam servers.

## 3. The big picture

```
   Browser / phone / desktop window (same web UI everywhere)
                       │  HTTPS + WebSocket
                       ▼
 ┌──────────────── HUB (an agent with "hub" turned on) ────────────────┐
 │  users · roles · 2FA · audit · machine registry · command routing   │
 └───────▲──────────────────────────▲──────────────────────────▲──────┘
         │ outbound wss             │ outbound wss             │ (its own servers too)
 ┌───────┴────────┐        ┌────────┴───────┐
 │ AGENT  Box A   │        │ AGENT  Box B   │   …more machines
 │ Core + API     │        │ Core + API     │
 │ └ game servers │        │ └ game servers │
 └────────────────┘        └────────────────┘
```

- **Agent** — the engine plus its API, running headless on each machine.
- **Hub** — not a separate product: an agent with hub mode enabled. With one machine, your agent is
  simply a hub of one. Add a second machine and it joins the first.
- **Desktop** — a small tray app that opens the web UI in a native window (WebView2) and makes sure
  the local agent is running. Optional; the browser works just as well.

### Why agents dial *out* to the hub

Game machines never need an open inbound port or router forwarding for management — only the hub is
reachable from outside (already solved: Let's Encrypt support from the legacy app carries over). It
also works behind NAT and changing IPs. The hub sends commands down the agent's existing connection.

### Why one web UI instead of WPF + web

Today there are two UIs, and every feature is either built twice or missing from one (the web had no
live console until last week; the WPF app has no remote access). A single web UI means every feature
lands everywhere at once, and "good looking and feature rich" is one job instead of two. The desktop
experience stays native where it matters (tray icon, notifications, auto-start, its own window).
This choice is revisitable until Phase 6, because the desktop shell is deliberately thin.

## 4. Process model — and a Windows constraint

The agent runs as a **background process in the logged-in user's session**, started at logon by a
scheduled task (with a watchdog task that restarts it if it dies). It is **not a Windows service**:

> Services run in "Session 0", which has no desktop. Several game servers are driven by typing into
> their own console window (7 Days to Die, Conan Exiles, DayZ, Space Engineers, Minecraft Bedrock, and
> scheduled commands). That requires an interactive desktop, so a pure service would break them.

For always-on machines this pairs with Windows auto-logon (the first-run wizard will explain and offer
it). Servers using embedded consoles (stdin) have no such limit — if a future version wants a service
mode, it can be offered for machines that only run those.

## 5. Solution layout

```
WindowsGSM-Next/
├─ WindowsGSM.Next.sln
├─ Directory.Build.props          shared build settings (net10.0-windows, versioning)
├─ Directory.Packages.props       ONE place for every package version
├─ docs/                          this document, the roadmap, porting guide
├─ src/
│  ├─ WindowsGSM.Core/            THE ENGINE — no UI, no web
│  │  ├─ Functions/ GameServer/ Installer/   imported legacy engine, SAME paths as legacy
│  │  ├─ Compat/                  shims so imported files stay identical (MainWindow statics…)
│  │  ├─ Hosting/                 WgsmEnvironment (data root, version, limits), DataRootLock
│  │  └─ Engine/                  new code, namespaces WindowsGSM.Engine.*
│  │     ├─ WgsmEngine.cs         the facade a host creates — wires everything below
│  │     ├─ Events/               in-process event bus + event types
│  │     ├─ Servers/              ServerRegistry, ServerInstance, ServerState
│  │     ├─ Operations/           OperationGate (per-server locks), jobs (progress, cancel, history)
│  │     ├─ Watchdog/             crash-loop policy
│  │     ├─ Backups/              backup settings (backup.json) + backup/restore service
│  │     └─ Services/             lifecycle, update, provisioning, add-ons, console, monitor,
│  │                              scheduler, notifications, server log, plugin catalog, crash report
│  ├─ WindowsGSM.Contracts/       API shapes + permissions, shared by agent, hub and clients (not Windows-only)
│  ├─ WindowsGSM.Agent/           wgsm-agent: the host — v2 API, event stream, auth, serves the UI
│  │  ├─ Api/                     endpoints by area; route-group filters do auth, machine and permission checks
│  │  ├─ Realtime/                the WebSocket event stream
│  │  ├─ Security/                users, sessions, audit log, TOTP, password hashing
│  │  ├─ Hosting/                 certificates (incl. Let's Encrypt), firewall, start-at-logon task, logging
│  │  └─ wwwroot/                 the web UI (ES modules + CSS), embedded in the exe — see §9
│  └─ WindowsGSM.Desktop/    (Phase 6)  tray + WebView2 window
├─ tests/
│  ├─ WindowsGSM.Core.Tests/      plugin compatibility, policy and engine tests (real processes)
│  └─ WindowsGSM.Agent.Tests/     API tests: real engine + agent on an in-memory server
└─ tools/
   ├─ WindowsGSM.Harness/         wgsm-harness — drive the engine from a console (dev only)
   └─ Show-LegacyChanges.ps1      carry legacy fixes across
```

**Imported code keeps its legacy relative path** (`Functions/ServerConfig.cs` is
`Functions/ServerConfig.cs` in both). A fix made in the legacy app ports by path — no mapping table.
Every deliberate difference is marked with a `// NEXT:` comment explaining why.

## 6. The engine (WindowsGSM.Core)

### 6.1 Plugin compatibility contract

Community plugins are C# source compiled at run time against `WindowsGSM.Core` and called through
`dynamic`. These public namespaces are therefore **frozen API**:

- `WindowsGSM.Functions` — ServerConfig, ServerPath, ServerConsole, Github, Plugin, UI (prompt API)…
- `WindowsGSM.GameServer.Engine` — SteamCMDAgent and friends
- `WindowsGSM.GameServer.Query` — A2S, UT3, EOS, FIVEM…
- `WindowsGSM.Installer` — SteamCMD, DepotDownloader
- `WindowsGSM.MainWindow` — only the statics `WGSM_PATH`, `WGSM_VERSION`, `MAX_SERVER`, `ServerStatus`

`PluginCompatibilityTests` compiles every plugin in the plugin kit through the real loader and
constructs every built-in game server, headless. A change that breaks the contract fails the build.

### 6.2 Questions plugins ask (EULA, "install Java?")

The legacy app popped a dialog on the server's desktop. The engine can't — it may be controlled from a
phone. `Functions.UserPrompt` replaces that:

1. **Up front:** the install wizard shows the consents a game needs (e.g. "I accept the EULA"); the
   operation runs with those granted.
2. **Live (Phase 3+):** an unexpected question pauses the job and appears in the UI's job panel with
   Agree / Decline.
3. **Otherwise: "no".** An unattended install can never accept a licence on someone's behalf.

The legacy plugin API `UI.CreateYesNoPromptV1` routes through the same mechanism.

### 6.3 State and concurrency (Phase 2)

- `ServerRegistry` is the single source of truth: for each server, its config, runtime state
  (process, console, metrics) and status. Thread-safe; UIs only read snapshots and subscribe to events.
- One **operation gate** per server — a server can't be started while it's updating, etc. — plus a
  global gate for bulk operations. Unlike legacy (where any running operation blocked *every* server),
  servers don't block each other; a shared download limit (2 by default) and a SteamCMD lock protect
  the genuinely shared resources. Kill always wins, so it can rescue a stuck server.
- The console buffer is internally locked (it used to rely on the UI thread for safety) and exposes a
  sequence number so any client can resume from where it left off.

### 6.4 Jobs (Phase 2)

Anything that takes time — install, update, backup, restore, add-on install — becomes a **job** with
an id, stage text, percentage (DepotDownloader already reports it), live log lines, a cancel button,
and a result. Jobs stream to every UI and are kept in history. Today installs are fire-and-forget with
no progress on the web at all.

### 6.5 Events (Phase 2)

An in-process event bus (synchronous, in order, one failing subscriber can't break the others) carries
`ServerStateChanged`, `ServerListChanged`, `ConsoleLineAdded`, `JobChanged`, `ServerMetricsSampled`,
`PlayersChanged`, `ServerLogged` and `ServerAlert` (crashed, auto-restarted, crash-loop suspended,
memory guard, join code, scheduled restart, auto-updated…). The agent forwards them over WebSocket;
the hub relays them from every agent, tagged with the machine id. This replaces all polling.

### 6.6 Services (Phase 2)

All reached through the `WgsmEngine` facade. Anything slow returns an `OperationRequest` holding a job.

| Service (`WgsmEngine.…`) | Replaces (legacy MainWindow) |
|---|---|
| `Lifecycle` — start/stop/restart/kill, reattach, crash handling | `Server_BeginStart/Stop`, `GameServer_Start/Stop/Kill/Restart`, `OnGameServerExited`, crash-loop suspend |
| `Provisioning` — install, import, delete | `Button_Install_Click` / import dialog / web install |
| `Updates` — update, validate, update-on-start | `GameServer_Update` |
| `Addons` — built-in + custom, update-on-start | `GameServer_UpdateAddons`, Tools menu installers, web add-ons |
| `Backups` — backup, restore, retention, before-start | `GameServer_Backup/RestoreBackup` + the web dashboard's separate backup code |
| `Console` — embedded stdin, window typing, RCON | `SendCommand`, web console tab |
| `Monitor` — CPU/RAM/players, health probe, memory guard | `CaptureServerResourceSamples`, `RunServerHealthChecks`, `RunMemoryGuardChecks` |
| `Scheduler` — cron (legacy CSV + managed), auto-update, auto-start | `StartRestartCrontabCheck`, `StartAutoUpdateCheck`, CrontabManager, auto-start loop |
| `Notifications` — Discord, per server per kind | Discord webhook calls scattered through the code |
| Readiness checks *(Phase 3)* | `RunReadinessChecks` (logic only; the UI renders results) |

Before-start work (backup → update → add-ons) is a list of pre-start steps the lifecycle runs in order,
so a new one never has to touch the start code.

### 6.7 Steam content: DepotDownloader is the standard

`Installer.SteamContentPolicy` is the one place that decides the tool:

- **DepotDownloader for every install and every update**, for every server.
- **SteamCMD only** for Workshop downloads, or when a server sets `steamcmd_override="1"` (an escape
  hatch for a game that misbehaves under DepotDownloader).

The legacy app decided this in two places with two defaults: installs used a global setting (default
DepotDownloader), but updates used a per-server flag that defaults to *off* for servers created before
it existed — so older servers kept updating through SteamCMD. That flag is now ignored.

Two related fixes are in: a DepotDownloader *install* now records its build number (only updates did),
and a server installed by SteamCMD but updated by DepotDownloader no longer reports "local build not
found".

**Checking for updates** (`GetRemoteBuild`) no longer launches `steamcmd.exe` (which needed a flaky
cache-deleting workaround). `Installer.SteamAppInfo` asks Steam directly — anonymous login and a
product-info query via SteamKit2, the library DepotDownloader is built on — with a 60-second cache.
SteamCMD remains only as the fallback. With this, SteamCMD is off the normal install / update / check
path entirely.

**Progress without changing the plugin API:** plugins call `SteamCMD.UpdateEx(...)` with no progress
callback. The engine sets an `Installer.DownloadContext` around the plugin call; DepotDownloader reports
every output line and percentage into it, so jobs get live progress bars and logs. This also guarantees
the downloader's output is always drained (an undrained pipe would block it forever).

### 6.8 Storage

- The **legacy on-disk layout is kept** (`servers/{id}/configs/WindowsGSM.cfg`, `servers/{id}/serverfiles`,
  `backups/`, `logs/`, `plugins/`, `bin/`), so an existing data folder is adopted as-is.
- New data goes under `configs/next/`: users, machines, schedules (JSON — readable and hand-editable).
- Backup settings live in `servers/{id}/configs/backup.json`, migrated automatically from the two
  legacy formats (desktop `BackupConfig.cfg`, web `webbackup.json`); backups made by either legacy path
  are still listed and restorable.
- Time-series history goes into `configs/next/history.db` (SQLite, WAL): per-minute CPU/RAM/players
  for each server and CPU/RAM/disk for the machine, rolled up to hourly after 14 days and kept 400 days,
  plus player sessions. Each machine keeps its own; a hub asks the machine (the request is forwarded like
  any other), so nothing is lost while the hub is down and nothing is stored twice. The last hour comes
  from the engine's in-memory samples, live.
- Agent-level data that isn't the legacy app's business also lives in `configs/next/`: notifications
  (`notifications.json`, newest 500, with each person's read marker), notification channels
  (`notify-channels.json`), server tags (`tags.json` — not in `WindowsGSM.cfg`, which legacy also reads).
- `DataRootLock` stops two managers controlling the same servers: a `wgsm.lock` file held exclusively
  (Next vs Next), plus detection of a running legacy `WindowsGSM.exe` from that folder (legacy never
  takes the lock, but always uses its install folder as its data folder).

## 7. API (Phase 3)

The agent (`src/WindowsGSM.Agent`, `wgsm-agent.exe`) hosts the engine and serves the API and the web UI.
Settings live in `configs/next/agent.json`: machine id and name, port (default **8971**, one above the
legacy dashboard so both can run during the move), network exposure (off by default), HTTPS.

- **REST under `/api/v2/…`.** Every server route is machine-scoped:
  `/api/v2/machines/{machine}/servers/{id}/…`. `local` is an alias for the agent's own machine id, so a
  single-machine UI never needs to know it — and nothing changes when a hub relays many machines.
  Errors are always `{ error, code, details? }`; slow operations answer **202 with a job**.
- **One WebSocket, `/api/v2/events`.** The client subscribes to topics (`servers`, `jobs`, `metrics`,
  `alerts`, `logs`, `console:{machine}/{id}`). Every event carries a sequence number; reconnecting with
  the last one replays what was missed from a 10,000-event buffer, or sends `reset` (refetch over REST)
  if the gap is bigger. Viewers only receive events for servers they may see; console lines need the
  Console right. A viewer that falls far behind is reset rather than buffered without limit. Only pages
  from the agent's own origin may connect; revoked sessions are dropped within 30 seconds.
- **Auth** carries over from the legacy dashboard and tightens it: cookie sessions tied to a revocable
  server-side session (SameSite=Strict), TOTP 2FA with replay protection, per-IP login rate limit plus
  per-account lockout, a required `X-WGSM-CSRF` header on every change, security headers, and an
  append-only monthly audit log (`configs/next/audit/`). Settings changes are audited by key, never by
  value. **Legacy dashboard accounts are adopted on first start** (same PBKDF2 hashes and TOTP secrets):
  legacy admins become owners, members keep their per-server permissions as grants.
- **First run:** the owner account is created from the machine itself, or from elsewhere with a one-time
  code the agent prints — an agent exposed to the network can't be claimed by a stranger.
- **Roles:** Owner, Admin, Operator, Viewer, Member (grants only), plus grants scoped to a server
  (`machine/id`), a machine (`machine/*`) or everything (`*/*`). Capabilities: View, Start, Stop, Restart,
  Kill, Update, Backup, Restore, Console, EditConfig, Files, Addons, Schedules, Delete, Install. A server
  you can't see answers 404, not 403. Owners alone manage admins, owners and the agent's settings.
  Changing backup locations or importing folders from elsewhere on the disk is admin-only, because either
  would expose files outside the servers.
- **Plugin questions** ("Accept the EULA?") asked mid-job are pushed to viewers as `prompt` events and
  wait up to 10 minutes for someone allowed to run that job to answer; no answer means "no".
- **Certificates:** a supplied PFX/PEM, Let's Encrypt (port-80 challenge listener, self-checked first,
  renewed in the background and swapped in live), or a persisted self-signed certificate. HSTS is sent
  only for a CA-trusted certificate.
- **Running unattended:** `wgsm-agent --register-startup` adds a Task Scheduler task that starts the agent
  at sign-in and restarts it if it stops (a task, not a service — see §4). `/api/v2/health` for
  monitoring; the agent's own diagnostics go to `logs/agent/agent-yyyyMMdd.jsonl`.
- Machine-to-machine calls (Phase 5) will use per-machine credentials, never user cookies.

## 8. Multi-machine (Phase 5)

Any agent can be the hub: it becomes one when the first machine joins it. A machine is a hub *or* a
member of one hub, never both, so there are no chains.

**Pairing** — on the hub, an owner opens *Machines → Add a machine*. The hub shows a one-time code
(8 characters, valid 15 minutes, single use) and the addresses it can be reached on. It also warns
when the agent only listens on localhost. On the new machine, an owner opens *Machines → Join a hub*
and enters one address and the code. The member calls `POST /api/v2/hub/pair` and receives a
long-lived credential; the hub stores only its SHA-256. For an https hub, the member pins the
certificate's thumbprint on first use (so a self-signed hub works). Nothing else needs configuring,
and no ports are opened on the game machine.

**The link** — the member dials `wss://hub/api/v2/hub/link` with `Authorization: WGSM-Machine
{id}:{credential}` and reconnects with backoff. JSON frames carry `hello`, `snapshot` (servers and
metrics: every 30 s, and a second after any server change), `event`, `watch` (which consoles the hub
is showing), and the request protocol: `req`/`reqend`/`res`/`resend`/`cancel`. Binary frames carry
bodies in 64 KB chunks. Close code 4403 means "you were removed" (the member forgets the hub);
4409 means a newer link replaced this one.

**Routing** — anything under `/api/v2/machines/{member}/…` is forwarded down that member's link by
middleware on the hub, after authentication. The member replays it against its own API over an
internal loopback listener. It rewrites the machine segment to `local` and adds three headers: a
per-process secret, the hub user as a *delegate* (name, role, and grants re-scoped from
`{member}/x` to `*/x`), and the client IP. The member's own route filters then decide. Permissions
are therefore enforced where the servers live, with the hub's grants, and the member's audit log
records "alice (via Hub)". Servers stay owned by their agent.

**Events** — member events are numbered into the hub's own stream (`PublishRemote`), with the same
per-user visibility rules as local ones. Console lines cross the link only while someone on the hub
is watching that console. Machines coming and going publish a `machine` event.

**Resilience** — if the hub is down, each agent still serves its own UI. When a member is offline,
the hub answers its server list from the last snapshot (`X-WGSM-Stale: 1`), returns empty jobs and
questions, and fails everything else at once with `503 machine_offline`. The UI dims that machine's
servers, hides their actions and shows why.

**Features it enables** — one login for everything, machine health cards, per-machine permissions
("my friend can manage the Valheim box only"), fleet actions (update all agents, "stop everything on
Box B"), and later: move or clone a server to another machine.

## 9. The UI (Phases 4 and 6)

**Technology:** plain JavaScript ES modules and CSS — no build step, consistent with the current
dashboard — split into small files (one per view/component) instead of one 2,000-line file. It lives in
`src/WindowsGSM.Agent/wwwroot/` and is embedded in the agent (never served from a temp folder), with a
strict CSP (no inline scripts or styles, no third-party hosts — even the 2FA QR code is generated
locally). Every UI file is sent `Cache-Control: no-cache`, so an upgrade never leaves anyone running
old code. For UI work, set `WGSM_WEB_ROOT` to the wwwroot folder and edits show on refresh.

```
wwwroot/
├─ index.html, manifest.json, img/ (logo, game icons)
├─ css/   tokens · base (components) · layout (shell) · views · server · pages
└─ app/
   ├─ main.js      routes + the sign-in gate          router.js  URLs, per-page Scope cleanup, leave guard
   ├─ api.js       REST client (CSRF, errors)         live.js    WebSocket: subscribe, resume, reconnect
   ├─ store.js     live model (servers, jobs, prompts, metrics) kept current from events
   ├─ shell.js     sidebar, top bar (bell, search), activity drawer, toasts
   ├─ palette.js   command palette + keyboard shortcuts   notify.js  notification rows, desktop pop-ups/bridge
   ├─ ui.js        toasts, dialogs, menus, pills, meters, charts, form fields
   ├─ dom.js · perms.js · actions.js · session.js · theme.js · qr.js
   └─ views/       overview, install, machines, notifications, schedules, plugins, account, users, audit,
                   health, settings, auth
                   server.js + server/ (overview, console, logs, players, files, backups, addons, schedules, settings)
```

**Look:** confident but not loud — blue-tinted neutrals, a vivid blue accent for actions and focus,
violet as the second accent, and clear status colours only where they mean something (green running,
amber attention, red problems). Dark, light and match-the-system themes; responsive from phone to
ultrawide.

**Game art:** the agent fetches each game's Steam store art once (portrait cover for tiles, wide header
for banners) into `cache/art/` and serves it itself, so the page's CSP never allows a third-party host.
A dedicated server's Steam app isn't the game's store app, so built-in games use a curated list of store
ids (verified against Steam by an opt-in network test) and plugin games are matched by name in Steam's
store search, accepted only on a clear name match. Games with no art (Minecraft) keep a generated tile:
a stable hue from the name, the initials and the small icon.

**Game config** (`Engine/GameConfig/`): finds a server's own config files — known locations per game
first (server.properties, &lt;mod&gt;/cfg/server.cfg, serverconfig.xml, Saved/Config/WindowsServer/*.ini…),
then a ranked scan that skips logs, mods and engine folders — and exposes them as settings with the
file's own comments as help text. Line formats (cfg, properties, INI, YAML) remember what surrounds each
value so saving rewrites only changed values; XML is edited in the original text by line/column, so the
file is byte-for-byte identical apart from the new values. Saves are refused if the game rewrote the file
since it was read, and keep the file's encoding (BOM included).

**Notifications:** everything worth telling someone about — server alerts (crash, crash loop, memory
guard, auto-restart/start/update, scheduled restart, join codes), failed jobs, machines going offline
(after a minute's grace) and coming back — is recorded by the agent from its event stream, local and
relayed, and published as a `notification` event. Who sees an entry follows the same visibility rules
as the event it came from. The top bar's bell shows each person's unread count (read markers are kept
server-side, so they follow you between devices). Owners add **channels**: a Discord webhook (embed,
optional mention) or any URL taking a JSON POST, subscribed to chosen kinds for chosen machines/servers;
repeats are throttled per channel, kind and server, and the URL is never sent back to the browser in
full. Per-server Discord alerts from the legacy settings keep working on their own machine.

**Command palette & shortcuts:** Ctrl+K (⌘K, or `/`) searches pages, servers, per-server actions
("restart valheim") and tabs, and machines, scored by word starts. `g o/n/i/m` jump to pages, `a` opens
activity, `?` lists shortcuts.

**Desktop app** (`src/WindowsGSM.Desktop`, `WindowsGSM.exe`): a WinForms tray app hosting the panel in
WebView2. It reads the agent's address from the data folder's `agent.json`, starts `wgsm-agent.exe`
(shipped beside it) if nothing answers, and keeps one copy per data folder (a second launch brings the
first forward). Closing the window hides it to the tray; quitting leaves the agent — and the servers —
running. The web app detects `chrome.webview` and sends notifications over the WebView2 message bridge;
the app shows them as Windows notifications while its window isn't in front. Links to other sites open
in the default browser; a self-signed certificate is accepted only for this machine. Its own
preferences and WebView2 profile live in `%LOCALAPPDATA%\WindowsGSM`; "Start with Windows" is the
current user's Run key.

**Discord bot** (`Agent/Discord/`): Discord.Net over the gateway with only the Guilds intent, registering
exactly `/panel`, `/list` and `/stats` (to the configured Discord server, the only one it's in, or globally).
It answers only Discord users on its admin list, and acts for each through the agent's **own API** over the
internal loopback listener (`LocalApi`: the per-process secret plus a delegated user holding just that
admin's servers and the panel's actions). So a button press is an ordinary API call: the same permission
checks, forwarding to other machines on a hub, and an audit entry "Name (Discord)". Settings live in
`configs/next/discord-bot.json`; the legacy `configs/discordbot/*.txt` are imported once (switched off,
because two apps on one token would both answer). A token is checked with Discord before connecting, so a
wrong one is reported instead of retried forever.

**Update watch:** every 30 minutes, one server at a time, the agent asks Steam (or the plugin) for the
latest build — and again right after an update. `ServerDto.UpdateAvailable` drives the badge; a
notification goes out once per new build. A failed check keeps the last answer.

**Restart warnings:** per server (`configs/next/restart-warnings.json`), before each scheduled restart,
update or stop — managed schedules, the legacy restart setting and crontab files alike — the agent sends
the game's broadcast command ("say {message}") at the chosen times. A warning is only sent close to its
moment, never late ("in 5 minutes" two minutes out).

**Reachability:** on demand, "can players reach it?" checks the listening port, the bound address, Windows
Firewall rules (program or port), the public IP against the machine's own addresses (behind a router →
which ports to forward), and for Steam games Steam's `GetServersAtAddress` — the proof the internet sees it.

**Structure:**

- **Overview** — fleet KPIs, machine health strip, an *Attention* panel (crashed, crash-looping,
  update available, disk nearly full, machine offline), server grid grouped by machine or tag, bulk
  actions.
- **Server** — live header (status, players, CPU/RAM, one-click copy of the connect address, actions)
  and tabs: Overview (charts, recent events), Console, Players, Files, Config, Backups, Schedules,
  Add-ons, Logs, Settings (watchdogs, alerts, CPU affinity/priority).
- **Install wizard** — pick machine → search games (with artwork, built-ins and plugins) → name →
  ports auto-assigned and conflict-checked → branch/login → consents → live progress.
- **Jobs drawer** — every install/update/backup across all machines with progress, logs and cancel.
- **Machines** (hub) — health, agent version, pairing, update agents.
- **Plugins** — browse and install community plugins; update and remove them.
- **Settings** — users and roles, notifications (Discord/webhooks per event), security (2FA,
  sessions, HTTPS), updates.
- **Logs** (activity/audit, app log, crashes, Discord bot, plugins, diagnostics), **command palette (Ctrl+K)**, keyboard shortcuts, toasts and a notification centre.
- **First-run wizard** — create the owner account, choose or adopt a data folder, name the machine,
  optionally join a hub, auto-start and auto-logon guidance.

## 10. Distribution and updates (Phase 7)

- Installed per user as a normal folder (`%LOCALAPPDATA%\Programs\WindowsGSM`, no admin) — **no
  single-file temp extraction**. The app is published **self-contained** (win-x64), so no runtime needs
  installing; the plugin compiler finds its reference assemblies in the folder (`refs\`, verified by
  compiling a plugin from a published build).
- `WindowsGSM.exe` at the top is a **.NET Framework 4.8 launcher** (part of Windows, ~0.5 MB): it starts
  `versions\<current>\` with `--data` and tells it where the install is (`WGSM_LAUNCHER`,
  `WGSM_INSTALL_ROOT`). Startup entries (Run key, the agent's sign-in task) point at the launcher, so they
  survive version switches. Run from a fresh download, the launcher is **setup** (install folder, data
  folder with the agent's dry run, shortcuts, Apps & features entry).
- **Updates**: the agent reads the feed (GitHub releases with `WindowsGSM-<v>.zip` + `.sha256`, or a JSON
  feed), downloads and verifies the zip, unpacks `versions\<v>\` beside the running one (staged, then
  renamed), refreshes the launcher by rename if needed, starts `launcher --switch <v> --wait-pid <agent>
  --start-agent` and stops. The launcher records current/previous in `install.json`, prunes older
  versions and starts the new agent, which re-attaches to the game servers that kept running. **Rollback**
  is the same with `--rollback`. Routes are per machine (`/machines/{m}/agent/update…`), so a hub updates
  its members through the usual forwarding.
- **Adopting a folder**: `wgsm-agent --check-data <folder>` is a read-only dry run (configs parsed directly,
  plugins compiled in memory, no lock taken).

## 11. Decisions log

| # | Decision | Why |
|---|---|---|
| D1 | New build side-by-side in `WindowsGSM-Next/`, same repo | Legacy stays usable for fixes; fixes port with git |
| D2 | Imported engine files keep legacy paths; differences marked `// NEXT:` | Porting fixes is by path, no mapping |
| D3 | Compat shims instead of editing imported files where possible | Keeps imported files diff-able against legacy |
| D4 | Agent runs in the user session, not as a service | Console-window plugins need an interactive desktop |
| D5 | One web UI; desktop = tray + WebView2 | Build every feature once; revisitable until Phase 6 |
| D6 | No-build vanilla JS modules | Matches the current dashboard; nothing to install to work on the UI |
| D7 | Agents dial out to the hub | No inbound ports on game machines; works behind NAT |
| D8 | Every agent is a hub-of-one; routes always machine-scoped | No separate single-machine mode to maintain |
| D9 | DepotDownloader standard; SteamCMD only for Workshop or explicit override | SteamCMD is the most common source of failures |
| D10 | Plugin prompts answered by up-front consent, else live, else "no" | Headless-safe; never accepts a licence silently |
| D11 | Web assets embedded, folder install, no temp extraction | Direct lesson from the dashboard 404 incident |
| D12 | JSON for config, SQLite for history | Hand-editable settings; efficient charts and audit |
| D13 | Agent on port 8971; adopts legacy dashboard accounts; legacy app untouched | Both can run side by side during the move; nobody re-enrols passwords or 2FA |
| D14 | Permission checks live in route-group filters, not in each handler | One place to get right; a new endpoint is protected by declaring what it needs |
| D15 | The hub forwards raw API calls; the member replays them against itself as the delegated hub user | No second API to keep in step; the member's own filters enforce the hub's grants |
| D16 | A machine is a hub or a member, never both | No chains or loops; "where is this server controlled from" always has one answer |
| D17 | History lives on each machine (SQLite), not on the hub | Survives hub downtime; the hub forwards chart requests like any other |
| D18 | Plugins install from GitHub repositories named WindowsGSM.&lt;Game&gt; — owners only | The community convention legacy already searched; plugins are code, so it's an owner's call |
| D19 | Desktop = WinForms + WebView2 around the same web UI; notifications over the message bridge | One UI everywhere; no browser permission prompts inside the app |
| D20 | In-process features that act for someone (the Discord bot) call the agent's own API as a delegated user | One set of rules for permissions, forwarding and audit — nothing reaches into the engine around them |
| D21 | Self-contained app in versioned folders behind a .NET Framework launcher; per-user install | Nothing to install, updates never overwrite a running version, rollback is a rename, no admin prompts |
| D22 | Updates must match a published SHA-256; the agent restarts, game servers don't | A corrupt or tampered download never runs; players aren't kicked by an update |
