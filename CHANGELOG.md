# Changelog

What changed in each version of WindowsGSM Next. The newest is first. Each version's section is also its release
notes on GitHub, and the "What's new" text the app shows before you update.

## 2.0.0-rc.4

Fixes from running rc.3 on real servers, and a 10-hour, 40-server soak test with no leaks.

- **Rust's console:** with `-noconsole -logfile …` the Console tab stayed on "No output yet", because Rust opens a
  window of its own. It now shows the log file live, also after WindowsGSM restarts while the game keeps running.
- **Settings → Discard** blanked the tab with "Unknown machine." until you reloaded the page.
- **Server scripts:** only the first 20 lines of a before-start or after-stop script's output go to the server's log;
  the rest is saved to `logs\servers\<id>\script-before-start.log` (or `script-after-stop.log`).
- **Updates:** pre-releases (alpha, beta, rc) are now opt-in, off for new installs — Updates → *Include
  pre-releases*. Installs that already have it on keep it on.

## 2.0.0-rc.3

Fixes from a second round of reviews and a 38-hour soak test.

- **Many servers on one PC:** with a few dozen servers running, the panel could take 10–60 seconds to answer (each
  server's captured console held threads the web panel needed). A 40-server test now answers in milliseconds.
- **Upgrading from the old WindowsGSM without stopping servers:** a game already running from a server's folder —
  left running by the old app, or started by hand — is taken over at start instead of being started a second time.
  One running as administrator while WindowsGSM isn't is flagged in the Health check, with what to do.
- **Going back a version keeps your newer settings:** saving a setting in an older version no longer drops the ones
  only the newer version knows, so updating again finds them as they were.
- **Notifications:** *Mark all read*, and clicking a notification opens what it's about. A game update is announced
  once while the server is behind, not again for every newer build (Rust staging publishes several a day).
- **Stopping games safely:** Avorion saves and stops instead of being ended; games whose own stop just ends the process
  (Eco, Mordhau, Squad, Insurgency: Sandstorm, Post Scriptum, Unturned and others) get Ctrl+C first so they can save —
  a toggle under Settings.
- **Add-ons:** Oxide, Carbon, SourceMod and AMX Mod X install only on a stopped server — over a running one they were
  left half-installed.
- **Sign-in:** wrong passwords lock that account only from the address they came from (someone guessing can't lock
  you out); resetting a password clears it everywhere.
- **Scheduler:** a run missed while the PC slept is skipped rather than fired late; one that finds the server busy
  retries, and tells you if it gave up. Interrupted servers with auto-restart come back after a reboot.
- **Crash restarts** wait for another operation to finish instead of giving up; **updates** check free disk space first
  and stop cleanly when cancelled.
- **Text files** keep their encoding and line endings when edited in the file manager or Configs.
- **Self-update:** refused while a backup, install or update is running; a failed switch to the new version goes back
  and says so; *Go back to the previous WindowsGSM version* in the Start menu.
- **A backup that fails always says why.** Server logs older than 90 days are cleaned up.
- **A game whose program is missing** says so plainly when it won't start, instead of a Windows error.
- **The panel offers a reload after WindowsGSM updates** in another tab or on its own. Buttons on empty pages
  ("Back to overview"…) no longer show an oversized icon.
- **Runs on Windows Server 2016 and 2019** without installing .NET Framework 4.8 first.

## 2.0.0-rc.2

Fixes from the release-candidate review: several machines, backups, and a few rough edges.

- **Backups can't lose data any more:** a file the running server has locked is left out (and noted) instead of being
  stored empty — a restore used to put that empty file back. Restoring a whole-server backup keeps the current copy of
  any file the backup left out, instead of deleting it. Test (✓) says when a backup left files out.
- **Backing up a running server saves the world first** (the game's save command, as Stop does).
- **A crash loop no longer replaces your good backups:** after a crash, "Backup before start" is skipped when the last
  backup is under an hour old.
- **Each server lists only its own backups** — a backup folder several servers share no longer mixes them (or lets one
  restore another's). Zips you put there by hand show only with WindowsGSM's naming.
- **Off-site: bring back backups from another PC's folder** — after a reinstall (or a dead disk), the old PC's
  off-site backups are offered, and come back named for this server.
- **Several machines:** a big download through the hub no longer freezes that machine's panel (or knocks it offline);
  *Update all* checks each machine first, with the hub's pre-release choice; two PCs with the same identity (a copied
  WindowsGSM folder, a cloned PC) are refused and told what to do instead of fighting; a machine the hub no longer
  knows says so plainly; leaving a hub removes the machine there too.
- **Minecraft: Turn on RCON** in the Health check for servers made before beta.11 — only when you press it.
- **Console tab:** a game that gives the panel no console (Rust with `-noconsole`) shows its log file instead of
  staying empty.
- **Discord bot:** the page no longer stays on "Connecting…" while the bot works; *Post actions in the channel* works
  in any channel (it needed a permission the bot often lacked).
- **Updates page:** shows what's in the version you're running, not only before an update.
- Players tab: spacing between Regulars and the whitelist.

## 2.0.0-rc.1

The release candidate for 2.0.0: no new features from here, only fixes. What to try before 2.0.0 is in docs/RC-CHECKLIST.md.

- **Security: API tokens end when an admin resets the user's password or disables them** (like their sign-ins) — a
  token made from a compromised account no longer survives the clean-up, or comes back when the account is re-enabled.
- **Docs:** the README, install guide and known issues cover what's new since beta.5 (runtimes, NeoForge, API tokens,
  Rust and Minecraft additions), and `docs/RC-CHECKLIST.md` lists what to try by hand before 2.0.0.

## 2.0.0-beta.11

Rust fixes (Oxide after updates, player names, map and world size, uMod with many plugins), NeoForge for Minecraft, and Minecraft RCON and console fixes.

- **The previous logo is back** (beta.10's new logo is reverted).
- **Rust: Oxide/Carbon go back on after a Rust update.** A Rust update (or Verify files) puts back the game files Oxide
  patches, so the server came up without plugins unless "update add-ons on start" happened to be on. Now the Oxide or
  Carbon WindowsGSM manages (or your custom build of it) is reinstalled right after the game's files change.
- **Rust: players' names** on the Players tab and in player history come from RCON's playerlist when the game's query
  leaves them out (asked at most every 30 seconds, only while someone's on).
- **Rust: the Map box works** (it was ignored), with a new **World size** box beside it: a seed (a number) plus the
  world size for a procedural map, or a custom map's download link (+server.levelurl, which ignores both). A map name
  like "Barren" still works. A quote in the server name or RCON password can no longer break the start line.
- **Rust: uMod plugins with many plugins installed.** uMod allows 30 requests a minute; checking or updating more
  plugins than that failed part-way. Requests are now paced, wait when uMod says "too many", look a plugin up by its
  exact name, and are remembered for 10 minutes.
- **Minecraft: NeoForge** as server software (Minecraft tab), beside Vanilla, Paper, Purpur and Fabric: NeoForge's own
  installer runs with the Java that Minecraft version needs, the server starts from NeoForge's arguments file, and
  NeoForge mods come from Modrinth like Fabric's. Releases only (no betas); the installer is checksum-verified.
- **Minecraft: "Update them before every start"** for Modrinth plugins and mods (Minecraft tab, off by default), like
  Rust's uMod option: the tracked ones go to Modrinth's newest just before each start and restart.
- **Minecraft: new servers have RCON on** (with a random password, and WindowsGSM's RCON settings to match), and the
  Health check says when an existing server's is off and how to turn it on.
- **Minecraft: Stop works after the agent restarts** — over RCON, instead of the server being ended without saving.
- **Minecraft: the console shows accented and other non-English characters correctly** (UTF-8).
- **Minecraft: new servers start with 2–4 GB of memory** (was 1 GB).

## 2.0.0-beta.10

A new logo, and the desktop app's sign-ins tidied up.

- **New logo** for the desktop app, the installer, the web panel, the phone home-screen icon and the Discord bot.
- **Fixed:** the desktop app on the server showed as "Unknown device" under Where you're signed in, and each time it
  signed itself back in (after an update, say) it left its old session listed. It now shows as the WindowsGSM app
  and replaces its earlier session.

## 2.0.0-beta.9

API tokens, Minecraft whitelist and operators, Counter-Strike 2, quicker Rust stops, and the console back after an agent restart.

- **API tokens** (Your account → API tokens): for scripts and tools such as Home Assistant or a Stream Deck, sent as
  `Authorization: Bearer wgsm_…`. A token can do what you can — same role and server permissions — except manage
  accounts, passwords, two-factor, sessions or tokens. Shown once, stored only as a hash; optional expiry, and each
  shows when and from where it was last used.
- **Minecraft whitelist and operators** on the Players tab (Java: whitelist and ops; Bedrock: allowlist). A running
  server is told with its own commands; a stopped one has its files edited (player IDs looked up from Mojang, or
  worked out for offline-mode servers).
- **Counter-Strike 2** as a game (Steam app 730, about 60 GB, downloads anonymously). CS:GO stays.
- **Rust stops in seconds with `-noconsole`**: Stop sends "quit" over RCON when it's set up, instead of waiting out
  the stop timeout and ending the process.
- **The Console tab after the agent restarts** shows the game's log file (Rust's `-logfile`, Minecraft's
  `logs\latest.log`) for servers that kept running, instead of staying empty until their next restart.
- **Health check: the game config's address.** When Minecraft's server.properties binds an address that isn't this
  PC's (after the PC's address changed), the Health check and the start log say so — the file isn't changed.
- **Lighter scheduler:** schedule files are checked for outside changes every 10 seconds instead of every second.

## 2.0.0-beta.8

The right Java and Windows runtimes for every server, fixed installs for PocketMine-MP and FiveM, safer Minecraft Bedrock updates, and four dead games removed.

- **Missing Windows runtimes are found and can be installed.** WindowsGSM reads what each server program needs
  (Visual C++ 2010–2022, DirectX's extra DLLs, .NET) from the program itself. A server's Health check lists anything
  missing with an **Install** button (admins): it runs the installer the game came with (Steam's _CommonRedist, or
  Unreal's) or Microsoft's own, and only when it's signed by Microsoft. A start stops with a clear message instead of
  hanging on Windows' "The code execution cannot proceed…" box, and a crash report says what exit codes like
  -1073741515 mean. On your own PC these are usually there already; a fresh Windows or Windows Server often lacks them.
- **Vintage Story gets the .NET it needs** (1.22: .NET 10) in WindowsGSM's own folder when Windows doesn't have it,
  and reads its version from Vintage Story's official feed.
- **Fixed: PocketMine-MP couldn't be installed** — its downloads moved from Jenkins to GitHub, and the old links now
  give a web page. It now installs the PHP build PocketMine asks for, from GitHub.
- **Fixed: FiveM (GTA V) couldn't be installed or updated** — the page WindowsGSM read is empty now, and build
  numbers outgrew its pattern. Uses FiveM's own version feed; failures are reported instead of looking like success.
- **Fixed: Minecraft Bedrock updates** copied only the program and four folders, so files Mojang added (the `config`
  and `data` folders, …) never arrived; and they deleted packs you'd added. Everything new is copied now; your settings, worlds and
  own packs stay.
- **Removed games that no longer work or are long abandoned:** Risk of Rain 2 (Steam's server download has been
  empty since 2020), Reign of Kings (last update 2019), Outlaws of the Old West (2020) and Heat (2021). An existing
  server of one of them shows as an unknown game; its files and backups are untouched.
- **Games that need a Steam login** (DayZ) say so and where to sign in, instead of "Steam account is not set up".
- **Fixed: Minecraft started with the wrong Java.** WindowsGSM took the "newest" Java by comparing version text
  (so 9 beat 25), only looked in one folder (or only inside JAVA_HOME), and installed Oracle's JDK 22 only when there
  was no Java at all — so Minecraft 26.x, which needs Java 25, was started with Java 8 and exited at once. Now the
  server.jar says which Java it needs; the oldest installed Java that's new enough is used (from JAVA_HOME, PATH and
  the usual vendors' folders); and when there's none, Eclipse Temurin of that version is downloaded into WindowsGSM's
  own folder (checksum-verified — nothing is installed on the PC).
- **Minecraft: a missing eula.txt is written again on start** (the EULA was accepted at install), instead of the
  server stopping straight away.
- **Fixed:** saving a settings file while another program (antivirus, a backup tool) had it open treated it as
  damaged and skipped keeping the previous copy as the .bak. It now waits for the file (up to about 3 seconds).

## 2.0.0-beta.7

Rust starts with only what you set, the game version in a server's Details, and a crash-log false alarm fixed.

- **The server's game version is in Details:** the installed build and the newest one ("build 21345678 · newest
  21346002 · Update available"), with the branch when it isn't the public one, when it was last checked, and a
  **Check now** button. It comes from the background check that already runs every 30 minutes.
- **Rust: the start line is only what you set.** It used to add every line of a WindowsGSM-made server.cfg (tick
  rate, description, seed, world size, …), which you couldn't see in the form. Now it's Rust's usual switches, the
  form's settings, RCON (from the RCON settings, when a port is set) and your Extra start parameters. On an existing
  server's first start, that file's settings move, so nothing about the server changes: the save folder
  (`+server.identity`) and `-logfile` to Extra start parameters, RCON to the RCON settings, and the rest into Rust's
  own `server\<identity>\cfg\server.cfg` (editable under Configs; anything already there wins). The old file is
  kept as server.cfg.old.
- **Fixed:** a harmless "A background task in the agent failed … ClientWebSocket" entry in the crash log after
  Steam version checks. The Steam connection now closes cleanly, and that leftover isn't logged as a failure.

## 2.0.0-beta.6

Stability fixes, a cleaner add-on setup (custom Oxide/Carbon builds, removing one, editing custom add-ons), and
"Start with new accounts" in setup.

- **Fixed: installing a game could crash the agent.** For about 30 games, writing the server's config files after
  the install ran unwatched: any error in it (a failed config download, a file in use) ended the agent. The install
  now waits for it, so the config exists before the server shows as installed, and a failure is a note in the
  server's log instead.
- **Fixed: after a reboot, WindowsGSM could take over the wrong program.** Reconnecting to running servers matched
  only the process ID and name, and Windows reuses IDs. For Minecraft (java) that could be any Java program, which
  Stop would then kill. It now also checks that the process was already running when WindowsGSM saved its ID.
- **Fixed: settings changes could be silently lost** when two saves of the same file happened at once, or
  antivirus held the file for a moment. Saves now wait their turn and retry briefly.
- **Machines that drop off the network show as offline within about 40 seconds** (hub links and the live
  dashboard connection now notice a dead link instead of waiting on Windows networking).
- **Setup: "Start with new accounts"** when using an existing folder — keeps the servers, backups and settings, but
  not the accounts or sign-ins (WindowsGSM's own, or the old app's web-dashboard ones): you create a new owner
  account when it opens. The old account files are kept in the folder, renamed.
- **Custom Oxide/Carbon builds take over:** a custom add-on whose link is an Oxide or Carbon build (e.g. Oxide
  staging) now shows on the built-in row as *From custom add-on "staging"*. Update add-ons installs only that build,
  not the release first and then your build on top.
- **Oxide and Carbon can't both be installed:** installing one is blocked while the other is on the server, and
  either can now be **removed** (plugins, configs and data are kept; removing Oxide runs Verify files to put back the
  Rust files it replaced).
- **Custom add-ons can be edited** (name, link, folder) instead of deleted and re-added.
- **Fixed:** reinstalling or updating SourceMod, AMX Mod X or DayZSAL silently changed nothing. They now overwrite
  their files properly and are protected against zip files that write outside the server folder.
- **Fixed:** AMX Mod X always showed "Not installed".
- **SourceMod and MetaMod:Source now install 1.12** (the current stable release; was 1.10).
- **Fixed:** a Windrose Plus install could hang forever. It now also gives up after 10 minutes.
- **DayZSAL:** downloads over https. DZSA no longer publishes the server download, so it's only listed on servers that
  already have it.

## 2.0.0-beta.5

Add-on versions at a glance, more Discord alert options, and security hardening.

- **Add-ons show their versions:** Oxide and Carbon say which version is installed and the newest on GitHub
  ("Installed 2.0.7807 · Latest 2.0.7830"), with **Update to …** when there's a newer one and a *staging* tag on a
  staging build. GitHub is asked at most once an hour.
- **Security:** a server's Discord webhook must be a Discord address (https://discord.com/api/webhooks/…), checked when
  it's saved and before anything is sent — so nobody who may edit one server can make WindowsGSM post to an address
  inside your network. Links from outside (the plugin list, uMod) can't run script.
- **Discord alerts: "Started"** — a message when someone starts or restarts the server by hand (Settings → Discord
  alerts; auto-starts and automatic or scheduled restarts keep their own switches).
- **Send a test** next to the webhook URL posts a test message, so a wrong URL shows up straight away.

## 2.0.0-beta.4

Off-site backups to your own file server, and home servers that keep starting when the PC's address changes.

- **Off-site backups to your own file server:** SFTP, FTPS (explicit or implicit) and FTP, next to the S3 services.
  Server, username, password and folder; uploads go under a temporary name until complete; "keep newest N", listing
  and bringing a backup back work as with S3. The server's SFTP key or self-signed FTPS certificate is trusted the
  first time and checked after that — a different one stops the upload. Plain FTP is marked as unencrypted.
- **A server set to an address this PC no longer has starts anyway.** At home the router gives the PC its address and
  it can change; the game then couldn't open its network socket and quit (Rust: "Couldn't initialize Steam
  Server", Enshrouded: "address not available"). Now the server moves to this PC's current address at start, with a
  note in its log and an *IP changes* notification. Per server, on by default: *Update the IP when this PC's address
  changes* (Settings → General). An address the PC has, or 0.0.0.0, is never changed.
- Health checks flag a server whose IP isn't an address of this PC, and say which one it should be.
- New servers get the PC's real network address, not a VPN's (NordVPN and the like) or a virtual adapter's.
- The IP field explains which address it wants: this PC's, which at home is its network address, not the public one.

## 2.0.0-beta.3

Several PCs on different versions are clear about it, and two Let's Encrypt fixes found by new tests against
Let's Encrypt's own test server.

- **PCs on different versions.** They keep working together as before, and now it's visible: Machines shows
  *Update to …* on each PC that can update, Overview lists them under *Needs attention*, and the hub's
  notifications say when another PC has an update waiting. *Update all* updates them together. A feature newer
  than a PC's version says "Second PC runs 2.0.0-beta.1, which doesn't have this yet" instead of "not found".
- **Let's Encrypt:** renewing or re-issuing soon after a successful one no longer fails ("Cannot update challenge
  with status valid") — an approval Let's Encrypt still remembers is used as it is.
- **Let's Encrypt:** the port-80 check also answers over IPv6, so a domain with an IPv6 (AAAA) record validates.

## 2.0.0-beta.2

Fixes and polish on the way to 2.0: reporting problems is built in, the panel is easier to read and to use
with a keyboard or screen reader, and the app on the same PC connects to the agent instantly.

- **Report a problem** in Help, Health checks and the Ctrl K search opens GitHub's bug-report form with your
  version filled in. Nothing is sent from the app; you write and submit the report on GitHub.
- The desktop app and the launcher no longer wait about 2 seconds each time they connect to the agent on the
  same PC (it now answers on IPv6's `::1` as well as `127.0.0.1`).
- **Easier to read and to use with a keyboard or screen reader.** Text and buttons meet WCAG AA contrast in
  both themes and every accent colour (the blue buttons are a shade deeper); servers on an offline PC stay
  readable (dashed edge, grey picture) instead of fading out; a *Skip to content* link; form hints and errors are
  read out with their field; the install steps say which one you're on; headings run in order; small controls
  have bigger click areas; and switching a server's tabs keeps focus on the tabs.
- Updated libraries: Discord.Net (the bot), Fido2 (passkeys) and the AWS S3 client (off-site backups).
- A [Known issues](docs/KNOWN-ISSUES.md) page: what's stable in the beta, how to go back a version, and known
  problems with ways around them.

## 2.0.0-beta.1

The first beta. Everything planned for 2.0 is in; from here on it's fixes. Settings files are stable: later versions
read them as they are, and upgrades are tested against data saved by earlier releases.

**Control your servers from another PC**
- Setup now asks what the PC is for. *Run game servers on this PC* is the full install, as before. *Control game
  servers on another PC* installs just the app: enter the address of the PC with the servers (or your hub) and sign
  in. Nothing runs in the background, and no servers live there.
- A full install can do the same: tray icon → **PC** → *Connect to another PC…*. Switch between your PCs there.
- **Stay signed in** (on by default): sign in once and the app signs itself back in from then on. Remove it any time
  under Account & security on that PC.
- A self-signed certificate shows its fingerprint once to check against Agent settings → HTTPS on that PC. After
  that the app trusts only it, and stops if it changes. Plain HTTP is refused across the internet.
- An app-only install updates itself when the PC it shows runs a newer version.

**Back up WindowsGSM's own setup**
- Agent settings → **Setup backup** puts everything that isn't game files in one file:
  - accounts,
  - agent settings,
  - automations and notification channels,
  - the Discord bot and off-site settings,
  - templates and plugins,
  - each server's settings.
- Use it for a new PC, or after a disk dies.
- Passwords and tokens can come along, protected by a passphrase you choose.
- Restoring happens when the agent restarts, after the current settings are saved to the backups folder. Game
  servers keep running.

**Export diagnostics**
- Health checks → **Export diagnostics**: one zip for a bug report, containing versions, health checks, recent logs
  and settings. Passwords, tokens, keys and webhook addresses are taken out.

**Settings that survive damage**
- Every settings file keeps its previous copy.
- A file that can't be read (cut off mid-save, a bad hand edit) is read from that copy. If that's unreadable too,
  the file is kept aside: it's never silently replaced by an empty one that wipes your automations, pairings or
  channels. Health checks names it.
- Going back to an older version no longer trips over settings a newer one added.

**Security**
- Sign-in:
  - Stronger password hashing (600,000 iterations); existing passwords upgrade at the next sign-in.
  - A wrong username takes as long to answer as a wrong password, so accounts can't be found by timing.
  - The first-run setup code is replaced after 10 wrong guesses.
- Custom add-on downloads can't reach this PC itself or link-local addresses (such as a cloud host's metadata
  service).
- The Users page explains that Files and Add-ons let someone change the programs a server runs.
- New [SECURITY.md](SECURITY.md): how WindowsGSM protects your machine, and how to report a problem privately.

**Also**
- Release pages describe what changed, instead of linking to a list of commits.
- The automated tests no longer fail now and then on GitHub's build machines.

## 2.0.0-alpha.6

**Off-site backups**
- Agent settings → **Off-site backups**: any S3-compatible storage — Backblaze B2, Cloudflare R2, Wasabi, Amazon S3,
  MinIO — with *Test connection*. Turn on *Also upload each backup off-site* per server (Backups tab): every new
  backup uploads in the background, the newest few are kept there, and **Bring back** restores one if this PC loses
  its backups.

**Rust plugins from uMod**
- A **Plugins** tab on Rust servers with Oxide or Carbon: search uMod (most downloaded first), install with the plugins
  it requires, **Update all**, remove, and *Keep up to date* for plugins you added by hand. Checked against uMod's
  checksum, never overwrites your edits, no restart needed. Optionally update them before every start.

**Clean stops for more games**
- When a plugin "presses" Ctrl+C on a server that has no window of its own (captured, or found again after an agent
  restart), the Ctrl+C now reaches the server instead of going nowhere — most community plugins stop games this way.
- After an agent restart, plugins use their normal clean stop again.
- New: *Send Ctrl+C before the game's own stop* (Settings → Stopping safely), and a warning for games whose plugin
  just ends the process (ARK: Survival Evolved, DayZ, The Forest…).

**Fixes**
- Typing a Console-tab command to a server re-adopted after an agent restart could crash the agent (alpha.4–5).
- Restoring a backup's settings can no longer bring back a script for someone who isn't an admin.
- Games always read input from their own console.
- The console handling is now covered by automated end-to-end tests.

## 2.0.0-alpha.5

**Scripts before start and after stop**
- Settings → **Scripts**: your own `.bat` or `.ps1` before every start (restarts and crash restarts too) and after
  every stop — rotate logs, clean up files. For every game, not just Rust as in the old app (whose setting carries
  over). Only admins can choose a script, and only those two kinds of file. Output goes to the server's log; a time
  limit stops a script that hangs; optionally, a failing before-start script stops the start.

**Rust's console (and games like it)**
- Rust leaves the console it's started in and joins the console of the program that started it. WindowsGSM now
  follows it, so **Show window**, commands from the Console tab and a clean `quit` on stop all work — before, its
  real console stayed hidden and stops ended in a kill. Several such servers starting together each get their own.
- Commands are typed straight into a game's console instead of being sent to its (often hidden) window, where they
  could sit unread.
- Games whose plugin can't capture the console (Rust, ARK, DayZ…) are no longer asked to: Settings greys the option
  out, as the old app did.

**Fixes**
- 7 Days to Die commands from the Console tab work again, and simulated key presses only go to a game's window when
  it's really in front — never into another app.
- Many servers starting at once no longer tie up the agent while their consoles are set up.

## 2.0.0-alpha.4

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

## 2.0.0-alpha.3

The first release of WindowsGSM Next: a rebuild of WindowsGSM where your servers run in a background agent
rather than inside a window, controlled from a web panel, the desktop app, your phone or Discord.

**What's in it**
- **Servers that don't depend on a window.** Close the panel, restart the agent, or update WindowsGSM itself:
  game servers keep running and are picked up again, with crash detection still working.
- **One panel everywhere:** the desktop app (tray icon, Windows notifications), any browser, and your phone. Live
  console, logs, players, performance charts, a file editor, a game config editor, backups, add-ons, schedules and
  settings for every server.
- **Many PCs, one panel:** pair your other machines with a hub and control every server from one place, with the
  same accounts and permissions.
- **Every plugin still works:** all 46 built-in games, plus community plugins found and installed from the panel.
- **Steam downloads with DepotDownloader** for installs and updates, including rolling back to an earlier build,
  plus "update available" checks.
- **Automation:** schedules, restart warnings in game chat, automations (restart when empty, warn on high CPU or
  low disk), crash-loop protection and a memory guard.
- **Notifications** in the panel, on the desktop, and to Discord or any webhook. A **Discord bot** with `/panel`,
  `/list` and `/stats` across every machine.
- **Safe by default:** accounts with roles and per-server permissions, two-factor sign-in and passkeys, an audit
  log, and encrypted secrets.
- **Setup and updates:** installs for you only (no administrator needed), carries over your WindowsGSM 1.x folder
  (servers, backups, accounts, schedules, Discord bot), and updates itself from GitHub releases with one-click
  rollback. Game servers keep running throughout.
