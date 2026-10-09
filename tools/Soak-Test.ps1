<#
  The long-running test: runs WindowsGSM's agent for days and watches whether anything keeps growing — memory,
  handles, threads, log and settings files, leftover game processes, slower answers. Samples go to samples.csv as
  it runs; at the end, or when you press Ctrl+C, it writes report.md and report.html (a chart per measurement)
  and says what, if anything, looks wrong.

  .\tools\Soak-Test.ps1                    Sandbox (the default). Builds this repo and runs a separate agent with
                                           its own data folder and port, and stand-in game servers that print a
                                           line every second, restart every 30 minutes, back up every 2 hours and
                                           get a console command every 5 minutes. A panel client signs in and
                                           polls like an open browser. Your own servers and settings aren't
                                           touched. Open the sandbox's panel any time: the address and sign-in
                                           are in sandbox-login.txt in the results folder.
  .\tools\Soak-Test.ps1 -Watch             Measures the agent already running on this PC, with your real
                                           servers. Read-only: it changes nothing.
  .\tools\Soak-Test.ps1 -Report <folder>   Writes the report again for an earlier run.

  Options: -Hours 48   -Servers 4   -SampleMinutes 10   -Port 8979   -Out <folder>
           -Watch also takes -ProcessId (if several agents run) and -DataFolder (if it can't tell).
  Results go to %LOCALAPPDATA%\WindowsGSM-Soak\<date-time>\ unless -Out says otherwise.
#>
#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = "Sandbox")]
param(
    [Parameter(ParameterSetName = "Watch", Mandatory = $true)] [switch]$Watch,
    [Parameter(ParameterSetName = "Report", Mandatory = $true)] [string]$Report,
    [Parameter(ParameterSetName = "Sandbox")] [Parameter(ParameterSetName = "Watch")] [ValidateRange(0.05, 2000)] [double]$Hours = 48,
    [Parameter(ParameterSetName = "Sandbox")] [Parameter(ParameterSetName = "Watch")] [ValidateRange(0.1, 240)] [double]$SampleMinutes = 10,
    [Parameter(ParameterSetName = "Sandbox")] [ValidateRange(1, 20)] [int]$Servers = 4,
    [Parameter(ParameterSetName = "Sandbox")] [ValidateRange(1024, 65535)] [int]$Port = 8979,
    [Parameter(ParameterSetName = "Watch")] [int]$ProcessId,
    [Parameter(ParameterSetName = "Watch")] [string]$DataFolder,
    [Parameter(ParameterSetName = "Sandbox")] [Parameter(ParameterSetName = "Watch")] [string]$Out
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# ───────────────────────────── Report ─────────────────────────────

# What each column is, and when its change over the run counts as a problem. Early and late are the averages of the
# first and last third of the run after a warm-up; "grow" flags late - early above both an amount and a share.
$Measures = @(
    @{ Key = "private_mb";        Label = "Memory (private bytes)";     Unit = "MB";  Grow = 50;  Share = 0.15 },
    @{ Key = "working_set_mb";    Label = "Memory (working set)";       Unit = "MB";  Grow = 80;  Share = 0.25; Note = "Windows trims this on its own; private bytes is the one that matters." },
    @{ Key = "handles";           Label = "Handles";                    Unit = "";    Grow = 200; Share = 0.20 },
    @{ Key = "threads";           Label = "Threads";                    Unit = "";    Grow = 15;  Share = 0.25 },
    @{ Key = "cpu_pct";           Label = "CPU (of the whole PC)";      Unit = "%";   Grow = 5;   Share = 1.0 },
    @{ Key = "api_ms";            Label = "Panel response time";        Unit = "ms";  Grow = 150; Share = 2.0 },
    @{ Key = "game_processes";    Label = "Game processes";             Unit = "" },
    @{ Key = "servers_running";   Label = "Servers running";            Unit = "" },
    @{ Key = "logs_mb";           Label = "Logs folder";                Unit = "MB";  PerDay = 200 },
    @{ Key = "configs_mb";        Label = "Settings (configs folder)";  Unit = "MB";  Grow = 5;   Share = 0.5 },
    @{ Key = "server_configs_mb"; Label = "Servers' settings folders";  Unit = "MB";  Grow = 5;   Share = 0.5 },
    @{ Key = "backups_mb";        Label = "Backups folder";             Unit = "MB";  Grow = 50;  Share = 0.25; Note = "Each server keeps its newest 3 backups, so this should level off." },
    @{ Key = "agent_output_mb";   Label = "Agent's own output";         Unit = "MB";  PerDay = 100 }
)

function Num($v) { if ($null -eq $v -or "$v" -eq "") { return $null }; return [double]::Parse("$v", [Globalization.CultureInfo]::InvariantCulture) }
function Fmt($v) { if ($null -eq $v) { return "–" }; if ([math]::Abs($v) -ge 100) { return $v.ToString("0", [Globalization.CultureInfo]::InvariantCulture) }; return $v.ToString("0.##", [Globalization.CultureInfo]::InvariantCulture) }
function Avg($xs) { $xs = @($xs | Where-Object { $null -ne $_ }); if (-not $xs.Count) { return $null }; return ($xs | Measure-Object -Average).Average }

function Write-SoakReport([string]$folder) {
    $csv = Join-Path $folder "samples.csv"
    if (-not (Test-Path -LiteralPath $csv)) { Write-Warning "No samples in $folder yet."; return }
    $rows = @(Import-Csv -LiteralPath $csv)
    if (-not $rows.Count) { Write-Warning "No samples in $folder yet."; return }
    $info = @{}
    $infoFile = Join-Path $folder "run.json"
    if (Test-Path -LiteralPath $infoFile) { (Get-Content -LiteralPath $infoFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $info[$_.Name] = $_.Value } }
    $events = @()
    $eventsFile = Join-Path $folder "events.txt"
    if (Test-Path -LiteralPath $eventsFile) { $events = @(Get-Content -LiteralPath $eventsFile) }

    # The longest stretch on one agent process: a restart starts memory and handles from scratch.
    $segments = @($rows | Group-Object agent_pid | Sort-Object Count -Descending)
    $main = if ($segments.Count) { @($segments[0].Group) } else { @() }
    $hours = if ($rows.Count) { Num $rows[-1].hours } else { 0 }
    $span = if ($main.Count -gt 1) { (Num $main[-1].hours) - (Num $main[0].hours) } else { 0 }
    # Warm-up: the first hour, or the first 10% of a shorter run.
    $warm = [math]::Min(1.0, $span * 0.1)
    $settled = @($main | Where-Object { (Num $_.hours) - (Num $main[0].hours) -ge $warm })
    $third = [math]::Floor($settled.Count / 3)
    $enough = $third -ge 2

    $findings = New-Object System.Collections.Generic.List[string]
    $lines = New-Object System.Collections.Generic.List[object]
    foreach ($m in $Measures) {
        $all = @($rows | ForEach-Object { Num $_.($m.Key) })
        if (-not @($all | Where-Object { $null -ne $_ }).Count) { continue }
        $early = $null; $late = $null; $verdict = ""
        if ($enough) {
            $early = Avg (@($settled | Select-Object -First $third) | ForEach-Object { Num $_.($m.Key) })
            $late = Avg (@($settled | Select-Object -Last $third) | ForEach-Object { Num $_.($m.Key) })
        }
        if ($enough -and $null -ne $early -and $null -ne $late) {
            if ($m.Grow) {
                $limit = [math]::Max($m.Grow, [math]::Abs($early) * $m.Share)
                if ($late - $early -gt $limit) { $verdict = "rising"; $findings.Add("**$($m.Label)** went from $(Fmt $early) to $(Fmt $late)$($m.Unit) over the run.") }
            }
            if ($m.PerDay -and $span -gt 0) {
                $firstV = Num $settled[0].($m.Key); $lastV = Num $settled[-1].($m.Key)
                $perDay = ($lastV - $firstV) / ([math]::Max(0.01, (Num $settled[-1].hours) - (Num $settled[0].hours))) * 24
                if ($perDay -gt $m.PerDay) { $verdict = "rising"; $findings.Add("**$($m.Label)** grows by about $(Fmt $perDay) MB a day.") }
            }
        }
        if ($m.Key -eq "game_processes" -and $info.servers) {
            $extra = @($rows | Where-Object { (Num $_.game_processes) -gt [int]$info.servers }).Count
            if ($extra -ge 2) { $verdict = "rising"; $findings.Add("**Leftover game processes:** $extra samples saw more game processes than servers ($($info.servers)).") }
        }
        if ($m.Key -eq "servers_running" -and $info.servers) {
            # Not in the first few minutes: they're still being auto-started.
            $down = @($rows | Where-Object { (Num $_.hours) -ge 0.1 -and $_.servers_stopped -ne "" -and (Num $_.servers_stopped) -gt 0 }).Count
            if ($down -ge 2) { $verdict = "problem"; $findings.Add("**Servers found stopped** in $down samples (they should always be running or restarting).") }
        }
        $lines.Add([pscustomobject]@{ M = $m; Early = $early; Late = $late; Verdict = $verdict; Values = $all })
    }
    $errors = ($rows | ForEach-Object { Num $_.client_errors } | Measure-Object -Sum).Sum
    if ($errors -gt 0) { $findings.Add("**The panel client hit $errors errors** — see events.txt.") }
    $crashes = @($events | Where-Object { $_ -match "stopped unexpectedly" }).Count
    if ($crashes) { $findings.Add("**The agent stopped unexpectedly $crashes time(s)** — see agent-err.txt and the agent's logs.") }

    $title = if ($info.mode -eq "watch") { "Watching the running agent" } else { "Sandbox: $($info.servers) stand-in servers" }
    $summary = if (-not $enough) { "Too short to judge yet: it needs a few hours of samples after the first hour's warm-up." }
               elseif ($findings.Count) { "Something kept growing or went wrong — details below." }
               else { "Nothing kept growing. Memory, handles, threads, files and response times stayed level." }

    # report.md
    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine("# Long-running test: $title").AppendLine()
    [void]$md.AppendLine("$(Fmt $hours) hours, $($rows.Count) samples, from $($rows[0].time) to $($rows[-1].time). WindowsGSM $($info.version).").AppendLine()
    [void]$md.AppendLine("**$summary**").AppendLine()
    foreach ($f in $findings) { [void]$md.AppendLine("- $f") }
    if ($findings.Count) { [void]$md.AppendLine() }
    if ($segments.Count -gt 1) { [void]$md.AppendLine("The agent restarted during the run; the comparison uses its longest stretch ($(Fmt $span) hours).").AppendLine() }
    [void]$md.AppendLine("| Measurement | Start | Early | Late | End | |").AppendLine("|---|---|---|---|---|---|")
    foreach ($l in $lines) {
        $v = @($l.Values | Where-Object { $null -ne $_ })
        $flag = if ($l.Verdict) { "⚠ $($l.Verdict)" } else { "" }
        [void]$md.AppendLine("| $($l.M.Label) | $(Fmt $v[0]) | $(Fmt $l.Early) | $(Fmt $l.Late) | $(Fmt $v[-1]) | $flag |")
    }
    if ($events.Count) { [void]$md.AppendLine().AppendLine("## Events").AppendLine(); foreach ($e in $events) { [void]$md.AppendLine("- $e") } }
    [IO.File]::WriteAllText((Join-Path $folder "report.md"), $md.ToString(), (New-Object Text.UTF8Encoding $false))

    # report.html: one small chart per measurement.
    $enc = { param($s) [Net.WebUtility]::HtmlEncode("$s") }
    $html = New-Object System.Text.StringBuilder
    [void]$html.Append(@"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Long-running test</title><style>
:root { --bg:#f7f8fa; --fg:#1b1f24; --muted:#5b6470; --card:#fff; --line:#2f6fde; --warn:#c2410c; --grid:#e3e6ea; }
@media (prefers-color-scheme: dark) { :root { --bg:#0e1116; --fg:#e6e9ee; --muted:#9aa4b1; --card:#161b22; --line:#6ea0ff; --warn:#fb923c; --grid:#262c35; } }
body { background:var(--bg); color:var(--fg); font:15px/1.5 system-ui, sans-serif; margin:0; padding:24px 16px; }
main { max-width:900px; margin:0 auto; } h1 { font-size:22px; margin:0 0 4px; } .muted { color:var(--muted); }
.summary { font-weight:600; margin:16px 0 8px; } ul { margin:0 0 16px; padding-left:20px; }
.grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(260px, 1fr)); gap:12px; }
.card { background:var(--card); border-radius:10px; padding:12px 14px; border:1px solid var(--grid); }
.card.warn { border-color:var(--warn); } .card h2 { font-size:14px; margin:0; } .card .nums { font-size:13px; color:var(--muted); }
.card.warn .flag { color:var(--warn); font-weight:600; } svg { width:100%; height:70px; display:block; margin-top:6px; }
</style></head><body><main>
"@)
    [void]$html.Append("<h1>Long-running test: $(& $enc $title)</h1><div class=""muted"">$(Fmt $hours) hours · $($rows.Count) samples · $(& $enc $rows[0].time) to $(& $enc $rows[-1].time) · WindowsGSM $(& $enc $info.version)</div>")
    [void]$html.Append("<p class=""summary"">$(& $enc $summary)</p>")
    if ($findings.Count) { [void]$html.Append("<ul>"); foreach ($f in $findings) { [void]$html.Append("<li>" + ((& $enc $f) -replace '\*\*(.+?)\*\*', '<b>$1</b>') + "</li>") }; [void]$html.Append("</ul>") }
    [void]$html.Append("<div class=""grid"">")
    foreach ($l in $lines) {
        $v = @($l.Values); $present = @($v | Where-Object { $null -ne $_ })
        $min = ($present | Measure-Object -Minimum).Minimum; $max = ($present | Measure-Object -Maximum).Maximum
        $range = if ($max -gt $min) { $max - $min } else { 1 }
        $flag = if ($l.Verdict) { "<span class=""flag""> · $($l.Verdict)</span>" } else { "" }
        $cls = if ($l.Verdict) { "card warn" } else { "card" }
        $note = if ($l.M.Note) { "<div class=""nums"">$(& $enc $l.M.Note)</div>" } else { "" }
        [void]$html.Append("<div class=""$cls""><h2>$(& $enc $l.M.Label)$flag</h2><div class=""nums"">$(Fmt $present[0]) → $(Fmt $present[-1]) $($l.M.Unit) · low $(Fmt $min), high $(Fmt $max)</div>")
        [void]$html.Append("<svg viewBox=""0 0 300 70"" preserveAspectRatio=""none"" role=""img"" aria-label=""$(& $enc $l.M.Label) over time""><line x1=""0"" y1=""68"" x2=""300"" y2=""68"" stroke=""var(--grid)""/><polyline fill=""none"" stroke=""var(--line)"" stroke-width=""1.5"" vector-effect=""non-scaling-stroke"" points=""$(Get-Points $v $min $range)""/></svg>$note</div>")
    }
    [void]$html.Append("</div>")
    if ($events.Count) { [void]$html.Append("<h2 style=""font-size:16px;margin-top:24px"">Events</h2><ul>"); foreach ($e in $events) { [void]$html.Append("<li>$(& $enc $e)</li>") }; [void]$html.Append("</ul>") }
    [void]$html.Append("</main></body></html>")
    [IO.File]::WriteAllText((Join-Path $folder "report.html"), $html.ToString(), (New-Object Text.UTF8Encoding $false))

    Write-Host ""
    Write-Host $summary -ForegroundColor $(if ($findings.Count) { "Yellow" } elseif ($enough) { "Green" } else { "Gray" })
    foreach ($f in $findings) { Write-Host ("  - " + ($f -replace '\*\*', '')) -ForegroundColor Yellow }
    Write-Host "Report: $(Join-Path $folder 'report.html')"
}

# SVG polyline points, always with "." decimals whatever the PC's number format.
function Get-Points($values, $min, $range) {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $out = for ($i = 0; $i -lt $values.Count; $i++) {
        if ($null -eq $values[$i]) { continue }
        $x = if ($values.Count -gt 1) { 300.0 * $i / ($values.Count - 1) } else { 0 }
        $y = 66.0 - 62.0 * ($values[$i] - $min) / $range
        $x.ToString("0.#", $inv) + "," + $y.ToString("0.#", $inv)
    }
    return ($out -join " ")
}

if ($PSCmdlet.ParameterSetName -eq "Report") {
    $folder = (Resolve-Path -LiteralPath $Report).Path
    Write-SoakReport $folder
    return
}

# ───────────────────────────── Shared ─────────────────────────────

if (-not $Out) { $Out = Join-Path $env:LOCALAPPDATA ("WindowsGSM-Soak\" + (Get-Date -Format "yyyyMMdd-HHmm")) }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path
$csvPath = Join-Path $Out "samples.csv"
$eventsPath = Join-Path $Out "events.txt"
$inv = [Globalization.CultureInfo]::InvariantCulture

function Write-Text([string]$path, $text) {
    if ($text -is [array]) { $text = ($text -join "`r`n") + "`r`n" }
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
}

function Note([string]$text) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $text"
    Add-Content -LiteralPath $eventsPath -Value $line -Encoding UTF8
    Write-Host $line -ForegroundColor DarkCyan
}

function Get-FolderMB([string[]]$paths) {
    $sum = 0L
    foreach ($p in $paths) {
        if (-not (Test-Path -Path $p)) { continue }
        $m = Get-ChildItem -Path $p -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum
        if ($m.Sum) { $sum += [long]$m.Sum }
    }
    return [math]::Round($sum / 1MB, 2)
}

$script:clientErrors = 0
$script:apiMs = $null
$script:lastCpu = $null
$cores = [Environment]::ProcessorCount

function Measure-Agent($proc) {
    $proc.Refresh()
    $now = Get-Date
    $cpu = $proc.TotalProcessorTime
    $pct = $null
    if ($script:lastCpu -and $script:lastCpu.Pid -eq $proc.Id) {
        $wall = ($now - $script:lastCpu.At).TotalSeconds
        if ($wall -gt 0) { $pct = [math]::Round(100 * ($cpu - $script:lastCpu.Cpu).TotalSeconds / ($wall * $cores), 2) }
    }
    $script:lastCpu = @{ Pid = $proc.Id; At = $now; Cpu = $cpu }
    return @{
        working_set_mb = [math]::Round($proc.WorkingSet64 / 1MB, 1)
        private_mb     = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        handles        = $proc.HandleCount
        threads        = $proc.Threads.Count
        cpu_pct        = $pct
    }
}

$columns = "time", "hours", "agent_pid", "private_mb", "working_set_mb", "handles", "threads", "cpu_pct", "api_ms", "game_processes",
    "servers_running", "servers_stopped", "logs_mb", "configs_mb", "server_configs_mb", "backups_mb", "agent_output_mb", "client_errors"

function Add-Sample([hashtable]$s) {
    if (-not (Test-Path -LiteralPath $csvPath)) { Set-Content -LiteralPath $csvPath -Value ($columns -join ",") -Encoding UTF8 }
    $vals = foreach ($c in $columns) {
        $v = $s[$c]
        if ($null -eq $v) { "" } elseif ($v -is [double] -or $v -is [single] -or $v -is [decimal]) { $v.ToString($inv) } else { "$v" }
    }
    Add-Content -LiteralPath $csvPath -Value ($vals -join ",") -Encoding UTF8
}

# ───────────────────────────── Sandbox ─────────────────────────────

$sandbox = $PSCmdlet.ParameterSetName -eq "Sandbox"
$agent = $null
$base = $null
$machine = $null
$session = $null
$login = $null
$harnessDir = $null
$dataRoot = $null

function Invoke-Api([string]$method, [string]$path, $body) {
    $req = @{ Uri = "$base/api/v2$path"; Method = $method; WebSession = $script:session; Headers = @{ "X-WGSM-CSRF" = "1" }; TimeoutSec = 60; UseBasicParsing = $true }
    if ($null -ne $body) { $req.Body = ($body | ConvertTo-Json -Compress); $req.ContentType = "application/json" }
    try { return Invoke-RestMethod @req }
    catch { $script:clientErrors++; Note "Panel client: $method $path failed: $($_.Exception.Message)"; return $null }
}

# One item per server (PowerShell 7 hands a JSON array back as a single object).
function Get-ServerList { return @((Invoke-Api GET "/machines/$machine/servers" $null) | ForEach-Object { $_ }) }

function Connect-Panel {
    $script:session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = Invoke-Api POST "/auth/login" @{ username = $login.User; password = $login.Password }
    if (-not $r -or -not $r.ok) { Note "Panel client couldn't sign in." }
}

# Opens the live connection the way a browser tab does, and counts the updates it gets in a few seconds.
function Test-Live([int]$seconds) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.Options.Cookies = $script:session.Cookies
    $cts = New-Object System.Threading.CancellationTokenSource ($seconds * 1000)
    $count = 0
    try {
        $ws.ConnectAsync([Uri](($base -replace "^http", "ws") + "/api/v2/events"), $cts.Token).GetAwaiter().GetResult()
        $seg = [ArraySegment[byte]]::new((New-Object byte[] 65536))
        while ($ws.State -eq "Open") {
            $r = $ws.ReceiveAsync($seg, $cts.Token).GetAwaiter().GetResult()
            if ($r.MessageType -eq "Close") { break }
            if ($r.EndOfMessage) { $count++ }
        }
    }
    catch [OperationCanceledException] { }
    catch { if (-not $cts.IsCancellationRequested) { $script:clientErrors++; Note "Panel client: live connection failed: $($_.Exception.Message)" } }
    finally { $ws.Dispose(); $cts.Dispose() }
    return $count
}

function Start-SandboxAgent {
    $exe = Join-Path $Out "bin\agent\wgsm-agent.exe"
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $p = Start-Process -FilePath $exe -ArgumentList @("--data", "`"$dataRoot`"") -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $Out "agent-out-$stamp.txt") -RedirectStandardError (Join-Path $Out "agent-err-$stamp.txt")
    $null = $p.Handle # keeps the exit code readable after it ends
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $deadline) {
        if ($p.HasExited) { throw "The sandbox agent ended straight away (exit code $($p.ExitCode)). See $Out\agent-err-*.txt." }
        try { $null = Invoke-RestMethod -Uri "$base/api/v2/info" -TimeoutSec 3 -UseBasicParsing; return $p } catch { Start-Sleep -Seconds 1 }
    }
    throw "The sandbox agent didn't answer on $base within 3 minutes."
}

function Stop-Sandbox {
    if ($script:agent -and -not $script:agent.HasExited) { try { $script:agent.Kill() } catch { } }
    # Game servers outlive the agent by design: end the stand-ins too.
    Get-Process -Name "wgsm-console-harness" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($harnessDir, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { try { $_.Kill() } catch { } }
}

# ───────────────────────────── Setup ─────────────────────────────

if ($sandbox) {
    if (Test-Path -LiteralPath (Join-Path $Out "data")) { throw "$Out already has a sandbox in it. Choose another -Out (or leave it out for a new folder)." }
    # 127.0.0.1 rather than localhost: agents up to 2.0.0-beta.1 don't listen on ::1, and "localhost" waits ~2 s for that.
    $base = "http://127.0.0.1:$Port"
    if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq $Port) {
        throw "Something on this PC already uses port $Port. Choose another with -Port."
    }

    Write-Host "Building the agent and the stand-in game into $Out\bin …"
    foreach ($b in @(@("src\WindowsGSM.Agent\WindowsGSM.Agent.csproj", "agent"), @("tests\WindowsGSM.ConsoleHarness\WindowsGSM.ConsoleHarness.csproj", "harness"))) {
        & dotnet build (Join-Path $repo $b[0]) -c Release -o (Join-Path $Out "bin\$($b[1])") --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Building $($b[0]) failed." }
    }
    $harnessDir = Join-Path $Out "bin\harness"
    $harness = Join-Path $harnessDir "wgsm-console-harness.exe"

    $dataRoot = Join-Path $Out "data"
    New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot "configs\next"), (Join-Path $dataRoot "plugins\SoakGame.cs") | Out-Null
    Write-Text (Join-Path $dataRoot "configs\next\agent.json") (@{ Port = $Port; MachineName = "Soak test" } | ConvertTo-Json)

    # The stand-in game: the console harness in "chatty" mode, its output captured into the Console tab the way
    # Minecraft's plugin does it, stopped by typing "quit".
    $plugin = @'
using System.Diagnostics;
using System.Threading.Tasks;
using WindowsGSM.Functions;

namespace WindowsGSM.Plugins
{
    public class SoakGame
    {
        public Plugin Plugin = new Plugin { name = "SoakGame", author = "tools", description = "Stand-in game for the long-running test", version = "1", url = "", color = "#ffffff" };
        private readonly ServerConfig _serverData;
        public string Error, Notice;
        public string FullName = "WindowsGSM Soak Game";
        public string StartPath = "";
        public bool AllowsEmbedConsole = true;
        public int PortIncrements = 1;
        public object QueryMethod = null;
        public string Port = "46000", QueryPort = "46001", Defaultmap = "", Maxplayers = "20", Additional = "";

        public SoakGame(ServerConfig serverData) { _serverData = serverData; }

        public async Task<Process> Start()
        {
            var p = new Process
            {
                StartInfo =
                {
                    FileName = @"{{HARNESS}}", Arguments = "game chatty",
                    WorkingDirectory = ServerPath.GetServersServerFiles(_serverData.ServerID), UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            var console = new ServerConsole(_serverData.ServerID);
            p.OutputDataReceived += console.AddOutput;
            p.ErrorDataReceived += console.AddOutput;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        public async Task Stop(Process p)
        {
            await Task.Run(() =>
            {
                try { p.StandardInput.WriteLine("quit"); }
                catch { p.Kill(); } // picked up again after an agent restart: no input to type into
            });
        }

        public async Task<Process> Install() { return null; }
        public async Task<Process> Update(bool validate = false, string custom = null) { return null; }
        public bool IsInstallValid() => true;
        public bool IsImportValid(string path) => true;
        public string GetLocalBuild() => "";
        public async Task<string> GetRemoteBuild() => "";
    }
}
'@
    Write-Text (Join-Path $dataRoot "plugins\SoakGame.cs\SoakGame.cs") $plugin.Replace("{{HARNESS}}", $harness)

    $rng = New-Object Random
    for ($i = 1; $i -le $Servers; $i++) {
        $configs = Join-Path $dataRoot "servers\$i\configs"
        $files = Join-Path $dataRoot "servers\$i\serverfiles\world"
        New-Item -ItemType Directory -Force -Path $configs, $files | Out-Null
        $port = 46000 + 10 * $i
        Write-Text (Join-Path $configs "WindowsGSM.cfg") @(
            'servergame="WindowsGSM Soak Game [SoakGame.cs]"', "servername=`"Soak $i`"", 'serverip="127.0.0.1"',
            "serverport=`"$port`"", "serverqueryport=`"$($port + 1)`"", 'servermaxplayer="20"',
            'autostart="1"', 'autorestart="1"', 'embedconsole="1"', 'showconsole="0"', 'stoptimeout="15"')
        # A "world" to back up: a few MB that change a little between backups would be more realistic, but the
        # size staying put is what shows that old backups are cleared away.
        for ($k = 1; $k -le 4; $k++) { $bytes = New-Object byte[] (512KB); $rng.NextBytes($bytes); [IO.File]::WriteAllBytes((Join-Path $files "region-$k.dat"), $bytes) }
        # Staggered so they don't all restart at once: restart twice an hour, back up every 2 hours, a command every 5 minutes.
        $r = (7 * $i) % 30
        $schedules = @(
            @{ Cron = "$r,$($r + 30) * * * *"; Action = "Restart"; Payload = ""; Arguments = ""; Enabled = $true },
            @{ Cron = "$((11 * $i + 3) % 60) */2 * * *"; Action = "Backup"; Payload = ""; Arguments = ""; Enabled = $true },
            @{ Cron = "*/5 * * * *"; Action = "Command"; Payload = "status"; Arguments = ""; Enabled = $true })
        Write-Text (Join-Path $configs "schedules.json") (ConvertTo-Json -InputObject $schedules -Depth 3)
    }

    Write-Host "Starting the sandbox agent on $base …"
    $agent = Start-SandboxAgent
    $machine = (Invoke-RestMethod -Uri "$base/api/v2/info" -UseBasicParsing).machine

    # The owner account, created the way the first-run page does it (from this PC). Test-only: a random password,
    # kept in the results folder so you can open the sandbox's panel and look around.
    $chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray()
    $bytes = New-Object byte[] 20
    $crypto = [Security.Cryptography.RandomNumberGenerator]::Create(); $crypto.GetBytes($bytes); $crypto.Dispose()
    $pw = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    $login = @{ User = "soak"; Password = $pw }
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = Invoke-Api POST "/setup" @{ username = $login.User; password = $login.Password; machineName = "Soak test" }
    if (-not $r -or -not $r.ok) { Stop-Sandbox; throw "Couldn't create the sandbox's owner account." }
    Write-Text (Join-Path $Out "sandbox-login.txt") @(
        "Sandbox panel (test only — it ends with the test): $base", "Username: $($login.User)", "Password: $($login.Password)")
} else {
    $candidates = @(Get-Process -Name "wgsm-agent" -ErrorAction SilentlyContinue)
    if ($ProcessId) { $candidates = @($candidates | Where-Object Id -eq $ProcessId) }
    if ($candidates.Count -eq 0) { throw "No WindowsGSM agent is running$(if ($ProcessId) { " with process id $ProcessId" }). Start it, then run this again." }
    if ($candidates.Count -gt 1) { throw "Several agents are running ($(($candidates | ForEach-Object Id) -join ', ')). Choose one with -ProcessId." }
    $agent = $candidates[0]
    $null = $agent.Handle
    $commandLine = (Get-CimInstance Win32_Process -Filter "ProcessId=$($agent.Id)").CommandLine
    if (-not $DataFolder -and $commandLine -match '--data\s+(?:"([^"]+)"|(\S+))') { $DataFolder = if ($Matches[1]) { $Matches[1] } else { $Matches[2] } }
    if ($DataFolder) { $dataRoot = (Resolve-Path -LiteralPath $DataFolder).Path }
    else { Write-Warning "Couldn't tell the agent's data folder, so folder sizes aren't measured. Pass -DataFolder to include them." }
    $settingsFile = if ($dataRoot) { Join-Path $dataRoot "configs\next\agent.json" } else { $null }
    $agentPort = 8971; $scheme = "http"
    if ($settingsFile -and (Test-Path -LiteralPath $settingsFile)) {
        try { $st = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json; if ($st.Port) { $agentPort = $st.Port }; if ($st.UseHttps) { $scheme = "https" } } catch { }
    }
    $base = "${scheme}://127.0.0.1:$agentPort"
}

function Get-Info([int]$timeout) {
    $req = @{ Uri = "$base/api/v2/info"; TimeoutSec = $timeout; UseBasicParsing = $true }
    if ($base.StartsWith("https") -and $PSVersionTable.PSVersion.Major -ge 7) { $req.SkipCertificateCheck = $true }
    return Invoke-RestMethod @req
}
$version = try { (Get-Info 10).version } catch { "unknown" }
@{ mode = $(if ($sandbox) { "sandbox" } else { "watch" }); servers = $(if ($sandbox) { $Servers } else { $null }); version = $version
   started = (Get-Date).ToString("o"); hours = $Hours; data = $dataRoot; base = $base } | ConvertTo-Json | ForEach-Object { Write-Text (Join-Path $Out "run.json") $_ }

# ───────────────────────────── Run ─────────────────────────────

$start = Get-Date
$end = $start.AddHours($Hours)
$nextSample = $start
$nextPoll = $start
$nextLive = $start.AddMinutes(2)
$nextLogin = $start.AddHours(6)
$pollIndex = 0
Note "Started: $(if ($sandbox) { "sandbox with $Servers stand-in servers, panel at $base" } else { "watching agent $($agent.Id)$(if ($dataRoot) { ", data $dataRoot" })" }). Runs $Hours hours; Ctrl+C ends it early with a report."
Write-Host "Results: $Out" -ForegroundColor Cyan

try {
    while ((Get-Date) -lt $end) {
        $now = Get-Date

        # The agent ended: in the sandbox that's a finding (and it's started again, which also tests picking
        # running servers back up); when watching, look for the agent that replaced it (an update or restart).
        if ($agent.HasExited) {
            if ($sandbox) {
                Note "The agent stopped unexpectedly (exit code $($agent.ExitCode)). Starting it again."
                $agent = Start-SandboxAgent
            } else {
                Note "Agent $($agent.Id) ended. Waiting for it to come back (an update or restart) …"
                $agent = $null
                $waitUntil = (Get-Date).AddMinutes(10)
                while (-not $agent -and (Get-Date) -lt $waitUntil) {
                    Start-Sleep -Seconds 10
                    $agent = Get-Process -Name "wgsm-agent" -ErrorAction SilentlyContinue | Select-Object -First 1
                }
                if (-not $agent) { Note "No agent came back within 10 minutes; ending the run."; break }
                $null = $agent.Handle
                Note "Now watching agent $($agent.Id)."
            }
        }

        # Like a panel left open: a look at the servers every minute, one server's details in turn, the live
        # connection now and then, and a fresh sign-in every 6 hours (sessions last 8).
        if ($sandbox -and $now -ge $nextPoll) {
            $nextPoll = $now.AddMinutes(1)
            $null = Invoke-Api GET "/machines" $null
            $list = @(Get-ServerList)
            if ($list.Count) {
                $s = $list[$pollIndex++ % $list.Count]
                foreach ($p in "/metrics", "/logs?count=100", "/console") { $null = Invoke-Api GET "/machines/$machine/servers/$($s.id)$p" $null }
            }
        }
        if ($sandbox -and $now -ge $nextLive) { $nextLive = $now.AddMinutes(10); $null = Test-Live 15 }
        if ($sandbox -and $now -ge $nextLogin) { $nextLogin = $now.AddHours(6); Connect-Panel }

        if ($now -ge $nextSample) {
            $nextSample = $nextSample.AddMinutes($SampleMinutes)
            $s = Measure-Agent $agent
            $s.time = $now.ToString("yyyy-MM-dd HH:mm:ss")
            $s.hours = [math]::Round(($now - $start).TotalHours, 3)
            $s.agent_pid = $agent.Id
            $sw = [Diagnostics.Stopwatch]::StartNew()
            try { $null = Get-Info 30; $s.api_ms = $sw.ElapsedMilliseconds } catch { $s.api_ms = $null }
            if ($sandbox) {
                $s.game_processes = @(Get-Process -Name "wgsm-console-harness" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($harnessDir, [StringComparison]::OrdinalIgnoreCase) }).Count
                $list = @(Get-ServerList)
                if ($list.Count) {
                    $s.servers_running = @($list | Where-Object state -eq "Running").Count
                    $s.servers_stopped = @($list | Where-Object state -eq "Stopped").Count
                }
                $s.agent_output_mb = [math]::Round(((Get-ChildItem -LiteralPath $Out -Filter "agent-*.txt" -File | Measure-Object Length -Sum).Sum) / 1MB, 2)
            } else {
                $s.game_processes = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($agent.Id)" -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin "conhost.exe", "WerFault.exe" }).Count
            }
            if ($dataRoot) {
                $s.logs_mb = Get-FolderMB @((Join-Path $dataRoot "logs"))
                $s.configs_mb = Get-FolderMB @((Join-Path $dataRoot "configs"))
                $s.server_configs_mb = Get-FolderMB @((Join-Path $dataRoot "servers\*\configs"))
                $s.backups_mb = Get-FolderMB @((Join-Path $dataRoot "backups"))
            }
            $s.client_errors = $script:clientErrors
            $script:clientErrors = 0
            Add-Sample $s
            $running = if ($sandbox -and $null -ne $s.servers_running) { " · $($s.servers_running)/$Servers servers running" } else { "" }
            $done = $now - $start
            Write-Host ("{0}  {1}h {2:00}m of {3}h · memory {4} MB · handles {5} · threads {6}{7}" -f (Get-Date -Format "HH:mm"), [math]::Floor($done.TotalHours), $done.Minutes, $Hours, $s.private_mb, $s.handles, $s.threads, $running)
        }
        Start-Sleep -Seconds 5
    }
    Note "Finished."
}
finally {
    if ($sandbox) { Stop-Sandbox; Note "Sandbox agent and stand-in servers stopped. Its data stays in $dataRoot." }
    Write-SoakReport $Out
}
