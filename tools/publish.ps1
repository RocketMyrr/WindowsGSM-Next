<#
  Builds a WindowsGSM release: dist\WindowsGSM-<version>.zip (+ .sha256) laid out as
      WindowsGSM.exe                 the launcher / setup (.NET Framework 4.8 — built into Windows)
      versions\<version>\            the app: WindowsGSM.exe (desktop), wgsm-agent.exe, everything they need
  Self-contained, so nothing else has to be installed. Upload both files to a GitHub release on the update
  feed's repository and every installed copy will offer the update.

  .\tools\publish.ps1                       version from Directory.Build.props
  .\tools\publish.ps1 -Version 2.0.1        a specific version (-Version 2.1.0-beta.1 for a pre-release)
#>
param(
    [string]$Version,
    [string]$Out = (Join-Path $PSScriptRoot "..\dist")
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $repo "Directory.Build.props")
    $prefix = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $suffix = $props.Project.PropertyGroup.VersionSuffix | Where-Object { $_ } | Select-Object -First 1
    $Version = if ($suffix) { "$prefix-$suffix" } else { $prefix }
}
$parts = $Version.Split("-", 2)
$versionArgs = @("-p:Version=$Version", "-p:VersionPrefix=$($parts[0])")
$versionArgs += if ($parts.Length -gt 1) { "-p:VersionSuffix=$($parts[1])" } else { "-p:VersionSuffix=" }

$stage = Join-Path $Out "WindowsGSM-$Version"
$app = Join-Path $stage "versions\$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null
Write-Host "Building WindowsGSM $Version → $stage"

foreach ($project in "src\WindowsGSM.Agent", "src\WindowsGSM.Desktop") {
    dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true -o $app @versionArgs -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}
dotnet build (Join-Path $repo "src\WindowsGSM.Launcher") -c Release -o (Join-Path $Out "launcher") @versionArgs -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Building the launcher failed." }
Copy-Item (Join-Path $Out "launcher\WindowsGSM.exe"), (Join-Path $Out "launcher\WindowsGSM.exe.config") $stage
Get-ChildItem $app -Filter *.pdb | Remove-Item

@"
WindowsGSM $Version
===================

INSTALL OR UPDATE
  1. Extract this zip first (right-click it -> Extract All...). Running it from inside the zip doesn't work.
  2. Run WindowsGSM.exe from the extracted folder.
     - Windows may say "Windows protected your PC": click More info -> Run anyway.
       (WindowsGSM isn't signed with a paid certificate.)
  3. Setup takes it from there:
     - Already installed? It offers to update it - one click. Your game servers keep running.
     - Coming from WindowsGSM 1.x? Point it at your old folder to keep your servers, backups and accounts.
       It checks the folder first and changes nothing until you press Install.
     - New? It asks where your game servers should live, then installs.
  No administrator rights needed. Press F1 in setup for help at any step.

UPDATING LATER
  In the app: Agent settings -> Updates -> Install (it tells you when a new version is out).
  Or download a newer zip and run its WindowsGSM.exe. Either way game servers keep running,
  and the previous version stays installed so you can go back.

UNINSTALL
  Windows Settings -> Apps -> WindowsGSM, or Start menu -> WindowsGSM -> Uninstall WindowsGSM.
  Only the app is removed - your game servers, backups and settings stay.

HELP
  Inside the app: the Help page in the sidebar.
"@ | Set-Content (Join-Path $stage "README.txt") -Encoding UTF8

$zip = Join-Path $Out "WindowsGSM-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  WindowsGSM-$Version.zip" | Set-Content "$zip.sha256" -Encoding ASCII -NoNewline

$mb = [math]::Round((Get-Item $zip).Length / 1MB)
Write-Host "Done: $zip ($mb MB)"
Write-Host "      $zip.sha256"
