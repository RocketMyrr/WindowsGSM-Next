<#
  Downloads the servers some tests talk to, so they test against the real thing rather than a stand-in:

    pebble\pebble.exe   Pebble — Let's Encrypt's own ACME test server (certificates for the HTTPS tests)
    seaweedfs\weed.exe  SeaweedFS — an S3-compatible storage server (off-site backup tests)
    sftpgo\sftpgo.exe  SFTPGo — an SFTP / FTP / FTPS server (off-site backups to a file server)

  Pinned versions, checked against their SHA-256 before use. Without them those tests are skipped locally (CI
  always has them). Run once:

    .\tools\Get-TestTools.ps1                  into %LOCALAPPDATA%\WindowsGSM-TestTools
    .\tools\Get-TestTools.ps1 -Dir C:\tools    somewhere else (then set WGSM_TEST_TOOLS to that folder)
#>
param([string]$Dir = $(if ($env:WGSM_TEST_TOOLS) { $env:WGSM_TEST_TOOLS } else { Join-Path $env:LOCALAPPDATA "WindowsGSM-TestTools" }))
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue" # Invoke-WebRequest is many times slower with the progress bar

$tools = @(
    @{ Name = "pebble"; Exe = "pebble.exe"; Inside = "pebble-windows-amd64/windows/amd64/pebble.exe"
       Url = "https://github.com/letsencrypt/pebble/releases/download/v2.10.1/pebble-windows-amd64.zip"
       Sha256 = "2539F3903302D75F897522B23B1B52B3300F5F992245F13D3BDF3967CF23AF6D" },
    @{ Name = "seaweedfs"; Exe = "weed.exe"; Inside = "weed.exe"
       Url = "https://github.com/seaweedfs/seaweedfs/releases/download/4.48/windows_amd64.zip"
       Sha256 = "FE90C04C0620AD1A1C756F86CD5E1443773F56A446688077A4BB5EC04C3CC874" },
    @{ Name = "sftpgo"; Exe = "sftpgo.exe"; Inside = "sftpgo.exe"
       Url = "https://github.com/drakkan/sftpgo/releases/download/v2.7.6/sftpgo_v2.7.6_windows_portable.zip"
       Sha256 = "AF3994BB690962C7575858AEE368A4C1D6FD4748B35770BB0773EB06408D504D" }
)

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($t in $tools) {
    $folder = Join-Path $Dir $t.Name
    $exe = Join-Path $folder $t.Exe
    $stamp = Join-Path $folder "version.txt"
    if ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $stamp) -and (Get-Content -LiteralPath $stamp -Raw).Trim() -eq $t.Sha256) {
        Write-Host "$($t.Name): already there."
        continue
    }
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $zip = Join-Path $folder "download.zip"
    Write-Host "$($t.Name): downloading $($t.Url)"
    foreach ($attempt in 1..3) {
        try { Invoke-WebRequest -Uri $t.Url -OutFile $zip -UseBasicParsing; break }
        catch { if ($attempt -eq 3) { throw }; Start-Sleep -Seconds (5 * $attempt) }
    }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($hash -ne $t.Sha256) { Remove-Item -LiteralPath $zip -Force; throw "$($t.Name): the download's SHA-256 is $hash, expected $($t.Sha256). Not using it." }
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $t.Inside } | Select-Object -First 1
        if (-not $entry) { throw "$($t.Name): $($t.Inside) isn't in the download." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $exe, $true)
    }
    finally { $archive.Dispose() }
    Remove-Item -LiteralPath $zip -Force
    Set-Content -LiteralPath $stamp -Value $t.Sha256
    Write-Host "$($t.Name): ready ($exe)"
}
