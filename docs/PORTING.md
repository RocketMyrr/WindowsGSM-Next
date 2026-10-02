# Porting fixes from the legacy app

The legacy app (`WindowsGSM-Remaster/WindowsGSM/`) stays in service while Next is built, so fixes will
keep landing there. This is how they get carried over.

## What maps where

| Legacy (`WindowsGSM-Remaster/WindowsGSM/…`) | Next | How to port |
|---|---|---|
| `Functions/*.cs`, `GameServer/**`, `Installer/**`, `WindowsFirewall.cs` | `WindowsGSM-Next/src/WindowsGSM.Core/` — **same relative path** | Apply the same diff (script below) |
| `MainWindow.xaml.cs` engine logic (start/stop, install, backup, watchdogs…) | Core services (Phase 2+) | Re-apply by hand in the matching service |
| `Functions/Web/*`, `wwwroot/*` | Agent / Web (Phase 3–4) | Re-apply by hand; the API and UI are redesigned |
| WPF UI (`UI/*`, `MainWindow.xaml`) | — | Usually nothing to port |

Files Next changed on purpose carry `// NEXT:` comments explaining the difference. When a legacy fix
touches one of those spots, merge by hand and keep the Next behaviour.

Files that were **not** imported because Next replaces them: `Functions/UI.cs` (replaced by a
compatible prompt shim), `Functions/Dialogs/*`, `Functions/CrontabManager.cs` (→ Phase 2 scheduler),
`Functions/GoogleAnalytics.cs`, `Functions/AnalyticsSettings.cs`, `Functions/Web/*`.

## Fork point

Next was created from legacy commit **`56851ae`** *plus* the web-panel work that was uncommitted at the
time (it includes the console `GetSince` change in `Functions/ServerConsole.cs`). The commit recorded in
[`../tools/legacy-fork.txt`](../tools/legacy-fork.txt) is the baseline the script diffs from.

> Once that pending web-panel work is committed in legacy, put that commit's hash in
> `tools/legacy-fork.txt` — its engine changes are already in Next.

## The script

```powershell
# List legacy engine changes since the baseline, and where each file lives in Next
./tools/Show-LegacyChanges.ps1

# Try to apply them to Next (3-way merge; conflicts are left marked for you to resolve)
./tools/Show-LegacyChanges.ps1 -Apply

# After porting, move the baseline forward so the same changes aren't listed again
./tools/Show-LegacyChanges.ps1 -MarkPorted
```
