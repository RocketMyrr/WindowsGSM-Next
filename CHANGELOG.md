# Changelog

What changed in each version of WindowsGSM Next. The newest is first. Each version's section is also its release
notes on GitHub, and the "What's new" text the app shows before you update.

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
