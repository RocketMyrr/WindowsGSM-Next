# WindowsGSM Next — Roadmap

Phases run in this order. Each ends with an **exit test**: something you can actually do that proves the
phase is done. The legacy app (`../WindowsGSM-Remaster/`) stays in service throughout.

## Why this order

1. **Engine first** — everything depends on it, and it's where the risk is (behaving exactly like the
   app you trust today). Getting it right before anything is built on top avoids rework.
2. **API before UI** — the UI is built against a stable contract, and the API is already machine-scoped,
   so multi-machine later needs no redesign.
3. **UI foundation before multi-machine** — the hub is only as useful as the screens that show it. The
   data model is multi-machine from Phase 3, so nothing gets rebuilt when the hub arrives.
4. **Multi-machine as soon as a single machine is pleasant to use** — it's the headline feature.
5. **Polish, desktop shell and migration last** — they're thin layers over finished parts.

Rough sizes: S ≈ a session or two, M ≈ several, L ≈ many.

---

## Phase 1 — Foundation  *(M)* — **done**

- [x] `WindowsGSM-Next/` solution beside the legacy app; shared build settings; central package versions
- [x] Engine layer imported at identical paths (`Functions/`, `GameServer/`, `Installer/`)
- [x] Builds with **no WPF** — compat shims (`MainWindow` statics), locked console buffer, plugin image
      loading moved out, port allocation reads configs instead of the UI grid
- [x] Plugin prompts (EULA, Java) → `UserPrompt` consent model; legacy `UI.CreateYesNoPromptV1` kept
- [x] DepotDownloader standard for installs and updates (`SteamContentPolicy`), build recorded after
      installs, SteamCMD→DepotDownloader build fallback
- [x] Tests: every plugin-kit plugin compiles and loads; every built-in server constructs headless;
      DepotDownloader rules; consent rules; port allocation (34 passing)
- [x] Architecture, roadmap, porting guide
- [x] CI workflow (`.github/workflows/next.yml`) that builds Next and runs the tests on every push —
      verified locally with the same commands; first real run happens on your next push
- [x] Characterization tests for the on-disk formats Next adopts in place (config round-trip incl.
      custom keys, SetSetting semantics, build cache, add-on stores). The MainWindow-bound rules (backup
      selection, crash-loop maths) get theirs as they're ported in Phase 2 — crash-loop done.
- [x] Fixed on the way: hand-edited configs no longer brick the whole file; atomic, serialised config
      writes (40 concurrent writers, no lost updates) — see [LEGACY-BUGS.md](LEGACY-BUGS.md)

**Exit test:** `dotnet test` is green, and CI runs it on every push. ✅

## Phase 2 — Engine  *(L)* — **built; exit test pending on a copy of real data**

- [x] `ServerRegistry` + `ServerInstance` (config, runtime, status) as the single source of truth
- [x] Event bus; per-server and global operation gates — servers no longer block each other; a shared
      download limit and a SteamCMD lock replace the legacy app-wide lock
- [x] Jobs: progress %, stage, live log, cancel, history; trailing-edge progress throttling
- [x] Lifecycle: start / stop / restart / kill, embedded-console capture (canonical per-server buffer),
      CPU priority and affinity (64-bit mask fix), console-window handling, process-tree kill
- [x] Watchdog: crash handling + crash report, auto-restart through the gate, crash-loop backoff and
      suspension (legacy numbers, tested), **reattach with working crash detection** (legacy bug #1)
- [x] Update + validate as jobs with live DepotDownloader progress (download context — no plugin API
      change); update-on-start pre-start step; Steam branch tracking
- [x] **Remote build lookup via SteamKit2** — verified live against Steam; SteamCMD only as fallback.
      SteamCMD is now off the normal install / update / check path.
- [x] Data-root lock (Next-vs-Next lock file + detects a running legacy app on the same folder)
- [x] Install (with consents) and import; delete — atomic id reservation (concurrent installs can't
      collide), installer output drained into the job, failure report + cleanup, delete keeps backups
- [x] Backups (selective, retention, before-start) and restore — one format and one settings file
      (`backup.json`, migrated from both legacy formats); lists legacy desktop *and* web backups; retention
      runs only after a successful backup; staged restore with rollback; paths jailed to the server folder
- [x] Add-ons (built-in + custom) and update-add-ons-on-start — zip-slip safe extraction
- [x] Console commands: embedded stdin, window typing, RCON (Source and Rust WebRCON), audited by sender
- [x] Watchdog monitors: health probe using the game's own query method (not always A2S), memory guard;
      CPU / RAM / players sampling
- [x] Scheduler: one cron engine for restarts, starts, stops, backups, updates and timed commands /
      RCON; reads the legacy CSV crontab; auto-update check; auto-start at boot
- [x] Notifications (Discord) behind one service, throttled per server per kind
- [x] A small console harness (`tools/WindowsGSM.Harness`, `wgsm-harness`) to drive the engine by hand
- [x] ~~Metrics history store (SQLite)~~ → **moved to Phase 6**, where the charts that read it are built *(done there)*.
      Phase 2 keeps the last hour in memory, which is all the API and UI need until then.

**Exit test:** from the harness, install a real server, start it, see live console output, send a
command, update it, back it up, restore it — on a copy of your real data folder. *(The same flow passes
end-to-end against the test game plugin; the real-data run is yours to do.)*

## Phase 3 — Agent & API  *(M)* — **built; exit test pending on a copy of real data**

- [x] `WindowsGSM.Agent` host (`wgsm-agent`, port 8971 so it can run beside the legacy dashboard on
      8970); `WindowsGSM.Contracts` DTOs (platform-neutral, shared with the future hub and clients)
- [x] REST `/api/v2/machines/{machine}/servers/{id}/…` covering everything Phase 2 can do — power,
      update/validate/update-check, console, logs, players, metrics, settings (allow-listed keys,
      all-or-nothing), Steam branches, files (jailed, links not followed out, conflict-safe saves,
      1 GB uploads), backups + restore + settings, add-ons, schedules, install/import/delete, jobs
- [x] WebSocket event stream (`/api/v2/events`): topics, seq-based resume with `reset` on big gaps,
      per-viewer permission filtering, slow-viewer overflow handling, origin check, sessions re-checked
- [x] Auth: users, roles (Owner/Admin/Operator/Viewer/Member) + machine/server-scoped grants, TOTP (with
      replay protection), revocable sessions, monthly JSONL audit log, login rate limit + lockout, CSRF
      header, security headers (HSTS only for trusted certs), PFX/PEM/self-signed/Let's Encrypt with live
      renewal. **Legacy dashboard accounts are adopted on first start** — same passwords and 2FA.
- [x] First-run setup: owner account from the machine itself, or with a one-time code from elsewhere
- [x] Live plugin prompts: a job waiting on "Accept EULA?" is pushed to viewers and waits for an answer
      (10 min, then "no"); only people who could start that job may answer
- [x] Start-at-logon task with restart-on-failure (`--register-startup`); health endpoint; structured
      JSON-lines agent log (14 days)
- [x] API tests: auth, 2FA, sessions, permissions, machine scoping, files, backups, settings, schedules,
      install + live prompt, event stream (40 tests, real engine + real processes)
- [x] Readiness checks endpoint (moved to Phase 4, with the page that shows them — done there)

**Exit test:** with the agent running headless and no window open, everything the legacy web
dashboard can do works through the new API. *(Covered by the API tests against the test game; the
real-data run is yours: `wgsm-agent --data <copy of your data folder>`.)*

## Phase 4 — Web UI foundation  *(L)* — **built; exit test is yours (day-to-day use)**

- [x] Design system (tokens, dark/light/system themes, components), app shell, path-based routing with
      per-page cleanup, live data layer (one WebSocket, resume on reconnect, no polling for server state)
- [x] Overview (KPIs with machine sparklines, *Needs attention* panel, server cards with live
      players/CPU/RAM, filters, search, sort, card/list views, bulk actions)
- [x] Server page: live header + job strip; tabs Overview (chart, details, activity, health), Console
      (live, filter, history, RCON), Logs (live), Players, Files (editor with JSON/XML validation,
      conflict-safe save, drag-drop upload with progress), Backups (+ settings), Add-ons, Schedules
      (plain-English builder), Settings (grouped, validated, save bar)
- [x] Activity drawer (jobs with progress/log/cancel, plugin questions); toasts; connection status
- [x] Install wizard (search 47+ games, Steam branches, agreements up front or answered live, live progress)
- [x] First-run setup (owner account, machine name) — data folder is chosen when the agent starts
- [x] Users & access (roles + per-server permission presets), audit log, health checks, agent settings,
      account (password, 2FA with a built-in QR code, sessions)
- [x] Accessibility pass (keyboard focus, focus trap in dialogs, arrow-key tabs/menus, reduced motion,
      AA text contrast) and phone layout
- [x] Readiness checks endpoint (machine + per server) — carried over from Phase 3
- [x] Real game artwork: Steam cover + banner fetched once by the agent and cached (curated store ids for
      every built-in game, verified against Steam; name search for plugin games; initials tile fallback)
- [x] **Game config** tab: finds each game's own config files (known locations first, then a ranked scan)
      and edits them as a form — cfg, properties, INI (incl. Palworld option lists), XML, JSON, simple YAML;
      saves change only the edited values (XML byte-for-byte), conflict-safe, keeps encoding

**Exit test:** you run one real machine day-to-day from the new UI without reaching for the old app.

## Phase 5 — Multi-machine  *(M)* — **built; exit test is yours (your two machines)**

- [x] Hub mode; machine registry; one-time pairing codes (8 characters, 15 minutes, single use);
      per-machine credentials (stored hashed); an https hub's certificate is pinned at pairing
- [x] Agent → hub outbound WebSocket with reconnect/backoff; every API call to a member is forwarded down
      the link (bodies streamed both ways, so uploads/downloads work); event relay, console lines only
      for consoles someone is watching; fresh snapshot after every server change
- [x] Machines page (online/offline, version, last seen, CPU/RAM/disk, rename, remove; *Add a machine*
      with code + addresses + reachability warning; *Join a hub* / *Leave* on a member); machine
      picker and grouping across the UI (overview, install, health, audit, users, activity)
- [x] Per-machine permissions (the hub's grants travel with each request; the member decides); the
      member's audit log records hub users as "name (via hub)", readable from the hub
- [x] Offline handling (last known servers, dimmed; actions hidden; everything else fails fast with
      `503 machine_offline`; pages rebuild when the machine comes back)

**Exit test:** both of your machines appear in one panel; you start, stop, console into and update
servers on either from a single login, including from a phone.

## Phase 6 — Rich features & desktop  *(M–L)* — **built; exit test is yours**

- [x] Schedules: per server *(Phase 4)*, and a fleet page — what runs next everywhere, schedules by
      server, and "add to several servers" across machines in one go
- [x] Notifications centre: every alert, failed job and machine outage kept (newest 500), a bell with
      per-person unread count, a history page, optional desktop pop-ups per device; channels send chosen
      events for chosen machines/servers to Discord or any webhook (throttled, test button, URL kept secret);
      join-code alerts included
- [x] Metrics history store (SQLite, per machine: minutes for 14 days, hours for 400): server charts for
      1h/24h/7d/30d, machine charts up to a year, player sessions (regulars, recent visits)
- [x] Plugin catalog: search GitHub for WindowsGSM.* repositories, install / update / remove (owners;
      compiled live, path-safe extraction); readiness checks page *(Phase 4)*
- [x] Command palette (Ctrl+K or /): pages, servers, per-server actions and tabs, machines; shortcuts
      (g o / g n / g i / g m, a, ?); server **tags** (filter chips, search, shown on cards) and **saved views**
      (filter + tag + search + sort + layout, per browser)
- [x] `WindowsGSM.Desktop`: tray icon, native window (WebView2), starts the agent if needed, Windows
      notifications while hidden, one copy per data folder, remembers its window, "Start with Windows"
- [~] Fleet actions: start / stop / restart / update / back up every server shown or on a machine, and update every
      machine's WindowsGSM *(done — the last in Phase 7)*; move/clone a server between machines *(stretch, not started)*

**Added after review (legacy parity and polish):**

- [x] **Discord bot** — `/panel` (private control panel: server dropdown; start / update, restart / stop /
      force stop with confirmation; "who did what" posted in the channel), `/list`, `/stats` — across every
      machine; only listed Discord users, each limited to chosen servers; imports the legacy bot's token and
      admin list (left off until the old bot is turned off). The other legacy slash commands are dropped.
- [x] **Can players reach it?** — listening port, bound address, Windows Firewall, router/public IP (with
      what to forward), and Steam's public server list (the legacy "global server list" check)
- [x] **Update available** badge on cards and the server page, a *Needs attention* entry and a notification
      (checked every 30 minutes, one server at a time, and right after an update)
- [x] **Restart warnings** in game chat before scheduled restarts, updates and stops (chosen times, the
      game's broadcast command, a message template, "send a test")
- [x] Wide screens use the space (content up to 1760 px); server cards line up with or without tags

**Exit test:** the desktop app replaces the legacy window for everyday use.

## Phase 7 — Migration & release  *(M)*

- [x] Adopt an existing data folder in place; dry-run report (`wgsm-agent --check-data`, and in setup):
      servers and their games, plugins that don't compile, shared ports, accounts, Discord bot, backups,
      schedules, what doesn't carry over; blocks while the old app runs from that folder
- [x] Per-user folder install (no admin): a .NET Framework 4.8 launcher (built into Windows) that is also the
      setup; self-contained `versions\<ver>\`; updates from GitHub releases (or a JSON feed), SHA-256
      checked, unpacked side by side and switched by the launcher while **game servers keep running**;
      one-click rollback; *Update all* from the hub; uninstall from Apps & features (data kept)
- [x] `tools/publish.ps1` builds the release zip + checksum; verified end to end (install layout → update
      alpha.1 → alpha.2 with a running server re-attached → rollback)
- [x] Docs: [INSTALL.md](INSTALL.md) — install, switching, updates, uninstall, pairing, Discord bot, plugin
      authoring notes, publishing
- [ ] Beta on one machine alongside legacy (separate data copy) → cut over machine by machine *(yours)*

**Exit test:** both machines run Next in production; the legacy app is retired.
Start with: `.\tools\publish.ps1`, run the zip's `WindowsGSM.exe` on one machine pointed at a **copy** of its
data folder, live with it, then switch that machine over, then the other.

## Later — ideas, not scheduled

- [ ] **Single-app mode** ("Only while the app is open"): run the agent's engine and panel inside
      `WindowsGSM.exe` itself — one program, one window, the same panel, nothing served beyond its own window.
      Offered at setup beside the default "Run in the background". Trade-off to make clear in the UI: while the
      app is closed nothing looks after the servers (they keep running and are re-adopted on reopen, but no
      crash restarts, memory guard, schedules, backups, auto-updates, automations, Discord bot, notifications,
      hub or phone access); minimising to the tray keeps it all going. Mostly re-uses `AgentApp` in-process from
      the desktop app; updates would restart the app rather than the agent.
