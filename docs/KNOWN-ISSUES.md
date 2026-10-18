# Known issues

Problems we already know about in WindowsGSM Next, with ways around them. If yours isn't here, please
[report it](https://github.com/RocketMyrr/WindowsGSM-Next/issues/new?template=bug_report.yml) — in the app,
**Report a problem** (Help, Health checks, or Ctrl K) opens the same form with your version filled in.

## About the beta

2.0 is in beta: everything planned for it is in, and from here on it's fixes.

- **Stable:** WindowsGSM's settings files. Later versions read them as they are, and every release is tested
  against data saved by earlier ones. Your game servers' own files are never changed by an update.
- **Least tried by people so far,** so worth extra care: several PCs with a hub, off-site backups to
  S3-compatible storage, and HTTPS with Let's Encrypt. Each is tested automatically against the real thing (two
  agents, a real S3 server, Let's Encrypt's test server), but real setups vary more.
- **Going back:** Agent settings → Updates → *Go back to <version>* returns to the version you had before the
  last update. Game servers keep running.
- **Before switching from WindowsGSM 1.x:** try it on a copy of your WindowsGSM folder first, and keep backups.
  Agent settings → Setup backup saves WindowsGSM's own setup (accounts, settings, schedules, the Discord bot) in
  one file.
- **Reporting:** attach the zip from Health checks → *Export diagnostics*. Security problems go to
  [Report a vulnerability](https://github.com/RocketMyrr/WindowsGSM-Next/security/advisories/new), not a public
  issue.

## Install and updates

**Windows SmartScreen warns about the installer** ("Windows protected your PC").
WindowsGSM isn't code-signed yet, so Windows doesn't recognise the publisher. Choose *More info → Run anyway*.
Each release has a `.sha256` file beside the zip if you want to check the download is the one published.

**The browser warns about the certificate** when the panel uses HTTPS with its own (self-signed) certificate.
Expected: the certificate is genuine, but no public authority vouches for it. Compare the fingerprint with the
one under Agent settings → HTTPS. For no warning at all, use Let's Encrypt with a domain name.

## Servers

**After the agent restarts or updates, the Console tab is quiet for some servers.**
The servers keep running, and RCON works throughout. Games that also write their console to a log file — Rust and
other Unity games started with `-logfile`, and Minecraft: Java — show that file instead until their next restart;
other games' console output appears again after each server's next restart.

**Minecraft servers made before 2.0.0-beta.11 have RCON off.**
Without it, a server that kept running while the agent restarted can't be stopped cleanly (it's ended after the stop
timeout), and TPS and whitelist changes on a running server don't work. The server's Health check says how to turn it
on: `enable-rcon=true` and an `rcon.password` in server.properties, and the same port and password in Settings.

**Rust started with `-noconsole` and without RCON is slow to stop.**
Stop sends "quit" over RCON when the server has an RCON port in its settings (servers set up by WindowsGSM do).
Without RCON it types "quit" into the console, which `-noconsole` turns off, so WindowsGSM waits out the stop
timeout and then ends it. Set an RCON port, or remove `-noconsole` and turn off *Show the console window on this
machine* in Settings instead.
