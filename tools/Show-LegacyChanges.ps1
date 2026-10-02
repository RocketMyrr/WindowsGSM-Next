<#
.SYNOPSIS
    Lists (and optionally applies) legacy engine changes that haven't been ported to WindowsGSM Next yet.

.DESCRIPTION
    Engine files keep the same relative path in both trees:
        WindowsGSM-Remaster/WindowsGSM/<path>   ->   WindowsGSM-Next/src/WindowsGSM.Core/<path>
    so a legacy fix can be replayed onto Next as a path-shifted patch. The baseline commit lives in
    tools/legacy-fork.txt. See docs/PORTING.md.

.PARAMETER Apply
    Apply the pending engine changes to Next with a 3-way merge. Conflicts are left marked in the files.

.PARAMETER MarkPorted
    Move the baseline to the current HEAD, after you've ported (or deliberately skipped) everything listed.
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$MarkPorted
)

$ErrorActionPreference = 'Stop'
$repo = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$forkFile = Join-Path $PSScriptRoot 'legacy-fork.txt'
$since = (Get-Content $forkFile -Raw).Trim()

$legacyRoot = 'WindowsGSM-Remaster/WindowsGSM/'
$nextRoot = 'WindowsGSM-Next/src/WindowsGSM.Core'
$enginePaths = @('Functions', 'GameServer', 'Installer', 'WindowsFirewall.cs') | ForEach-Object { "$legacyRoot$_" }
# The old web dashboard and WPF dialogs are redesigned in Next (Agent/Web), never patched across.
$enginePaths += @('Functions/Web', 'Functions/Dialogs') | ForEach-Object { ":(exclude)$legacyRoot$_" }
# Legacy files Next replaced rather than imported - changes to these need a manual look, not a patch.
$replaced = @('Functions/UI.cs', 'Functions/CrontabManager.cs', 'Functions/GoogleAnalytics.cs', 'Functions/AnalyticsSettings.cs')

Push-Location $repo
try {
    if ($MarkPorted) {
        $head = (git rev-parse --short HEAD).Trim()
        Set-Content -Path $forkFile -Value $head
        Write-Host "Baseline moved from $since to $head." -ForegroundColor Green
        return
    }

    $commits = git log --oneline "$since..HEAD" -- @enginePaths
    if (-not $commits) {
        Write-Host "Nothing to port: no legacy engine changes since $since." -ForegroundColor Green
    } else {
        Write-Host "Legacy engine commits since ${since}:" -ForegroundColor Cyan
        $commits | ForEach-Object { Write-Host "  $_" }
    }

    # Include uncommitted legacy edits too - fixes are often made and tested before committing.
    $files = @(git -c core.safecrlf=false diff --name-only $since -- @enginePaths 2>$null) | Where-Object { $_ } | Sort-Object -Unique
    if (-not $files) { return }

    Write-Host "`nChanged engine files (legacy -> Next):" -ForegroundColor Cyan
    foreach ($f in $files) {
        $rel = $f.Substring($legacyRoot.Length)
        $nextPath = Join-Path $repo "$nextRoot/$rel"
        $note = if ($replaced -contains $rel) { 'replaced in Next - port by hand' }
                elseif (-not (Test-Path $nextPath)) { 'not in Next yet (new file - Apply will add it)' }
                elseif (Select-String -Path $nextPath -Pattern '// NEXT:' -Quiet) { 'has NEXT changes - review the merge' }
                else { 'identical path - applies cleanly' }
        Write-Host ("  {0,-55} {1}" -f $rel, $note)
    }

    if ($Apply) {
        $patch = git -c core.safecrlf=false diff --relative=$legacyRoot --binary $since -- @enginePaths 2>$null
        if (-not $patch) { return }
        $tmp = New-TemporaryFile
        try {
            # git diff output must be written without a BOM and with LF, or git apply rejects it.
            [System.IO.File]::WriteAllText($tmp, (($patch -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
            git apply --3way --directory=$nextRoot --whitespace=nowarn $tmp
            if ($LASTEXITCODE -eq 0) {
                Write-Host "`nApplied. Build and run the tests, then run with -MarkPorted." -ForegroundColor Green
            } else {
                Write-Host "`nApplied with conflicts - resolve the marked files, then build and test." -ForegroundColor Yellow
            }
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
}
finally { Pop-Location }
