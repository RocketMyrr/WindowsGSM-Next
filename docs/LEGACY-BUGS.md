# Bugs found in the legacy app while porting

> **Reference only.** Decision (2026-09-28): these are *not* being fixed in the legacy app — it stays as
> it is until Next replaces it. This list is here to consult if one of them bites in the meantime.

Porting the engine meant reading the legacy code line by line, and these turned up. All are fixed in
Next (search for `// NEXT:`). The ones marked **live** affect the app running on your servers today.

| # | Severity | Bug | Effect today | Where (legacy) |
|---|---|---|---|---|
| 1 | **High — live** | Servers re-adopted after a WindowsGSM restart never get exit events (`EnableRaisingEvents` is off for processes found by PID) | After any WindowsGSM restart, **crash detection and auto-restart silently stop working** for every server that was already running | `MainWindow` startup reattach loop (~line 1600) |
| 2 | **High — live** | `ServerConfig.SetSetting` truncates the config, then rewrites it; no locking | A crash, locked file or two simultaneous saves (web + UI + watchdog) can leave a server with an **empty or partial config** | `Functions/ServerConfig.cs` `SetSetting` |
| 3 | **Medium — live** | Config parser strips the first/last character of every value, assuming quotes | A hand-edited `autostart=1` or `key=` throws and makes the **whole config unreadable** | `Functions/ServerConfig.cs` constructor |
| 4 | **Medium — live** | CPU affinity mask built in a 32-bit int (`1 << i`) | On machines with **more than 32 logical processors**, cores 33+ wrap onto cores 1+ — wrong affinity | `Functions/CPU/Affinity.cs` |
| 5 | Medium — live | Over-long saved affinity string: `bits.Take(n).ToString()` returns the type name | Affinity becomes garbage (usually "all cores") | `Functions/CPU/Affinity.cs` |
| 6 | Medium — live | Older servers update via SteamCMD even though DepotDownloader is "the default" (per-server flag defaults off) | The SteamCMD problems you've been seeing | `Installer/SteamCMD.cs` `UpdateEx` |
| 7 | Medium — live | DepotDownloader update records the build even when it exited with an error | A failed update can be recorded as "up to date", hiding that it's needed | `Installer/SteamCMD.cs` `UpdateEx` |
| 8 | Low — live | Console window polling reads `MainWindowHandle` without `Refresh()` | A thread per server can spin (sleep loop) for the server's whole lifetime | `Server_BeginStart` window-hiding task |
| 9 | Low — live | Kill only kills the top process | A server started through a batch file/wrapper leaves the actual game running | `GameServer_Kill` |
| 10 | Low — live | Web Logs tab opened the daily log share-read-only | A web refresh overlapping a log write can make `MainWindow.Log()` throw | `Functions/Web/WebDashboardServer.cs` — **already fixed** in the uncommitted web-panel work |
| 11 | Low — live | `GetAvailablePort` reads the WPF grid and compares in a single pass | A new server can be given a port already in use | `Functions/ServerConfig.cs` |
| 12 | Info | Operations are globally serial — one server's update blocks starting any other | Slow bulk work; by design, to protect SteamCMD | `TryBeginServerOperation` |
| 13 | **High — live** | Restoring a whole-server backup deletes `servers/{id}` first, then extracts | If extraction fails part-way, **the server folder is simply gone** — no rollback | `GameServer_RestoreBackup` |
| 14 | Medium — live | Backup retention deletes old archives *before* creating the new one | A failed backup has already cost you a good one | `GameServer_Backup` |
| 15 | Low — live | Restore writes to any absolute path named in a backup's manifest | Harmless while restores are local-only; unsafe once restores can come from a browser | `GameServer_RestoreBackup` |
| 16 | Info | Built-in add-on installers extract zips without zip-slip checks | Only matters if an official add-on download were tampered with | `Functions/InstallAddons.cs` |
| 17 | Medium — live | Discord alert throttle is app-wide, and every suppressed alert restarts its window | Two servers crashing together → **only the first is reported**; a server crashing repeatedly can keep *all* alerts muted indefinitely | `MainWindow` Discord send throttle |
| 18 | Medium — live | Crontab CSV tasks (commands, backups…) only run when the server's *restart crontab* toggle is on | Scheduled commands silently never fire on servers that don't also use scheduled restarts | `MainWindow` crontab loop |
| 19 | Low — live | Health probe always uses A2S, whatever query method the game declares | Games that don't answer A2S (e.g. EOS/other query types) can be flagged unhealthy, or never checked properly | `MainWindow` health check |
| 20 | Low — live | Two installs started together can pick the same server id | The second install writes into the first one's folder / config | Install dialog / web install |
| 21 | Low — live | A failed install leaves its half-created server folder behind | Clutter, and a later install can reuse a dirty folder | Install flow |
| 22 | Low — live | The CrontabManager folder is created with admin-only ACLs | Non-elevated tools (and a Next agent in the user session) can't read it | `CrontabManager` |

If one ever needs fixing in legacy after all, items 1–4 are small and contained — the matching
`// NEXT:` change in Next shows the exact fix.
