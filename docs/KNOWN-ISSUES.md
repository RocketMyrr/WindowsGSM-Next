# Known issues

Problems we already know about in WindowsGSM Next, with ways around them. If yours isn't here, please
[report it](https://github.com/RocketMyrr/WindowsGSM-Next/issues/new?template=bug_report.yml) — in the app,
**Report a problem** (Help, Health checks, or Ctrl K) opens the same form with your version filled in.

## About the beta

2.0 is in beta: everything planned for it is in, and from here on it's fixes.

- **Stable:** WindowsGSM's settings files. Later versions read them as they are, and every release is tested
  against data saved by earlier ones. Your game servers' own files are never changed by an update.
- **Least tested so far,** so worth extra care: several PCs with a hub, off-site backups to S3-compatible
  storage, and HTTPS with Let's Encrypt.
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

**After the agent restarts or updates, the Console tab is quiet for servers whose console it captures.**
The servers keep running, and RCON works throughout. Their console output appears again after each server's next
restart.

**Rust started with `-noconsole` is slow to stop.**
A normal Stop types "quit" into the server's console, which `-noconsole` turns off, so WindowsGSM waits out the
stop timeout (30 seconds unless changed in Settings) and then ends it. Remove `-noconsole` from the start
parameters. To keep the window off the desktop, turn off *Show the console window on this machine* in the
server's Settings instead: the window is hidden but the console keeps working.
