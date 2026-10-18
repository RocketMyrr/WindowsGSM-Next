# Release-candidate checklist

What to try by hand before a release candidate becomes 2.0.0. The automated tests cover the logic; this covers what
only a real PC, real games and real downloads show. Tick each line, and note the version tried.

Use a **clean** machine for part A — a fresh Windows 11 or Windows Server 2022/2025 virtual machine with nothing
installed (no Java, no Visual C++ redistributables, no .NET). A gaming PC already has those, which hides problems.

## A. Fresh install (clean VM)

- [ ] Download the release zip; check it against its `.sha256`. Run `WindowsGSM.exe`; note the SmartScreen warning.
- [ ] Setup creates the owner account; the panel opens in the desktop app and in a browser on another PC
      (*Reachable from other computers* on).
- [ ] Health checks page: nothing red that shouldn't be.
- [ ] Restart the PC: the agent starts by itself, servers set to auto-start come up.

## B. Rust

- [ ] Install a Rust server; it starts, shows in the server browser, and players can join.
- [ ] Add-ons → install Oxide. Rust tab → install a uMod plugin with a requirement (e.g. Kits → ImageLibrary).
- [ ] With a player on: the Players tab shows their name; the chart shows FPS.
- [ ] Map box: a seed + World size starts that map; a custom map link starts that map.
- [ ] Stop with `-noconsole` in Extra start parameters: stops in seconds (over RCON), world saved.
- [ ] **A real Rust update** (or Verify files): afterwards the log says *Oxide put back after the game update*, and
      the plugins load on the next start.
- [ ] Restart the agent while Rust runs: it's picked up again; the Console tab shows `server.log`.

## C. Minecraft

- [ ] New Paper server: starts with the Java it needs (on the clean VM: downloaded — the log says so); EULA handled.
- [ ] Health check shows RCON on; the chart shows TPS.
- [ ] Players tab: add to the whitelist and operators while stopped, and while running; both stick.
- [ ] Modrinth: install a plugin with a dependency; turn on *Update them before every start*.
- [ ] Switch a server to **NeoForge**; install a NeoForge mod from Modrinth; it starts and loads the mod.
- [ ] Restart the agent while Minecraft runs, then Stop: it stops cleanly (over RCON), world saved.
- [ ] A name with accents in chat shows correctly in the Console tab.

## D. Runtimes (clean VM)

- [ ] Install a game that needs Visual C++ (e.g. an Unreal game such as Squad, or Conan Exiles). Health check lists
      the missing runtime; **Install** fixes it; the server starts.
- [ ] Vintage Story: starts, with .NET downloaded for WindowsGSM's own use.

## E. Backups and files

- [ ] Back up, test (✓), restore a server; the world is back.
- [ ] Off-site backup to an S3 bucket or FTP/SFTP server; delete the local backup; bring it back from off-site.
- [ ] File manager: edit and save a config; the config history shows the change and can put it back.

## F. Several machines

- [ ] Pair a second PC with a hub; control its servers from the hub; *Update all* updates both.
- [ ] Unplug the second PC's network: it shows offline within a minute; plug it back: it reconnects.

## G. Accounts and access

- [ ] A member with permission on one server sees only that one.
- [ ] Two-factor sign-in and a passkey work; sign out everywhere works.
- [ ] API token: `curl` lists servers and restarts one; deleting the token stops it; a password reset by an admin
      ends it.
- [ ] Discord bot: status, start/stop from Discord; a crash alert arrives.

## H. Coming from WindowsGSM 1.x

On a **copy** of a real 1.x install (servers, backups, web dashboard users, schedules):

- [ ] Setup → *Use an existing folder*: servers, backups, schedules and dashboard accounts carry over.
- [ ] Each server starts as before; Rust servers' old `server.cfg` moves out (server.cfg.old) and the world is the same.
- [ ] *Start with new accounts* variant: servers kept, old accounts set aside.

## I. Updating WindowsGSM itself

- [ ] From the previous release: Updates → Check now → update; servers keep running; *Go back* returns to the
      previous version.
- [ ] The desktop app restarts with the update and shows as *WindowsGSM app* (one entry) under *Where you're
      signed in*.

When every section passes (or what fails is fixed and re-tried), the release candidate can become 2.0.0.
