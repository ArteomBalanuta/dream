param(
    [int]$DurationSeconds = 30,
    [int]$ClientCount = 2,
    [int]$Port = 5057,
    [switch]$SkipBuild,
    [switch]$KeepRunning,
    [switch]$NoStopExisting,
    # dream.exe to launch; defaults to this checkout's dream\bin\Debug\net10.0\dream.exe. Pass a build
    # from another output path (Release, a worktree's artifacts) with -SkipBuild.
    [string]$Exe
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Title) {
    Write-Output ""
    Write-Output "== $Title =="
}

function Get-RepoRoot {
    $scriptPath = $PSCommandPath
    if (-not $scriptPath) {
        return (Get-Location).Path
    }

    return (Resolve-Path (Join-Path (Split-Path $scriptPath) '..\..\..')).Path
}

function Start-DreamProcess {
    param(
        [string]$Exe,
        [string]$WorkingDirectory,
        [string]$Name,
        [string]$Arguments,
        [string]$LogDir,
        [hashtable]$Environment,
        [bool]$Hidden
    )

    foreach ($key in $Environment.Keys) {
        [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], 'Process')
    }

    $stdout = Join-Path $LogDir "$Name.stdout.log"
    $stderr = Join-Path $LogDir "$Name.stderr.log"
    $startArgs = @{
        FilePath = $Exe
        WorkingDirectory = $WorkingDirectory
        RedirectStandardOutput = $stdout
        RedirectStandardError = $stderr
        PassThru = $true
    }

    if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
        $startArgs.ArgumentList = $Arguments
    }

    if ($Hidden) {
        $startArgs.WindowStyle = 'Hidden'
    }

    $process = Start-Process @startArgs
    [pscustomobject]@{
        Name = $Name
        Process = $process
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Read-LogLines([string]$Path) {
    if (Test-Path $Path) {
        return Get-Content $Path
    }

    return @()
}

function Get-NumberMatches {
    param(
        [string[]]$Lines,
        [string]$Regex
    )

    $values = New-Object System.Collections.Generic.List[double]
    foreach ($line in $Lines) {
        $match = [regex]::Match($line, $Regex)
        if ($match.Success) {
            $values.Add([double]::Parse($match.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture))
        }
    }

    return $values
}

function Get-MaxOrZero($Values) {
    if ($Values.Count -eq 0) {
        return 0
    }

    return ($Values | Measure-Object -Maximum).Maximum
}

$repoRoot = Get-RepoRoot
$exe = if ([string]::IsNullOrWhiteSpace($Exe)) {
    Join-Path $repoRoot 'dream\bin\Debug\net10.0\dream.exe'
}
else {
    (Resolve-Path $Exe).Path
}
$project = Join-Path $repoRoot 'dream\dream.csproj'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $repoRoot "logs\perf-run-$stamp"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

Write-Section 'Setup'
Write-Output "Repo: $repoRoot"
Write-Output "Logs: $logDir"
Write-Output "Exe: $exe"
Write-Output "Clients: $ClientCount"
Write-Output "DurationSeconds: $DurationSeconds"
Write-Output "Port: $Port"

if (-not $NoStopExisting) {
    Get-Process dream -ErrorAction SilentlyContinue | Stop-Process -Force
}

if (-not $SkipBuild) {
    Write-Section 'Build'
    dotnet build $project --nologo /p:UseSharedCompilation=false
}

if (-not (Test-Path $exe)) {
    throw "Expected executable was not found: $exe"
}

if ($Port -ne 5057) {
    Write-Warning "The current dedicated server bootstrap uses its default port. Verify server code supports overriding $Port before trusting endpoint results."
}

$processes = New-Object System.Collections.Generic.List[object]
$commonEnv = @{
    DREAM_RENDER_PERF_LOG = '1'
    DREAM_NET_DIAGNOSTICS = '1'
}

Write-Section 'Launch'
$serverEnv = $commonEnv.Clone()
$serverEnv['DREAM_SERVER_PORT'] = [string]$Port
$serverEnv['DREAM_NET_DIAGNOSTICS_PATH'] = Join-Path $logDir 'server.net.log'
$server = Start-DreamProcess -Exe $exe -WorkingDirectory (Split-Path $exe) -Name 'server' -Arguments '--server' -LogDir $logDir -Environment $serverEnv -Hidden $true
$processes.Add($server)
Write-Output "Started server pid=$($server.Process.Id)"
Start-Sleep -Seconds 2

for ($i = 1; $i -le $ClientCount; $i++) {
    $clientEnv = $commonEnv.Clone()
    $clientEnv['DREAM_AUTO_CONNECT'] = '1'
    $clientEnv['DREAM_SERVER_HOST'] = '127.0.0.1'
    $clientEnv['DREAM_SERVER_PORT'] = [string]$Port
    $clientEnv['DREAM_CLIENT_NAME'] = "codex-client-$i"
    $clientEnv['DREAM_CLIENT_SESSION_ID'] = "client-codex-$i"
    # One stable identity per client, kept beside the exe's database: the names stay owned across runs, more
    # than 5 clients do not hit the per-account character limit, and the real token file is never touched.
    $clientEnv['DREAM_IDENTITY_FILE'] = Join-Path (Split-Path $exe) "perf-identity-client$i.json"
    $clientEnv['DREAM_NET_DIAGNOSTICS_PATH'] = Join-Path $logDir "client$i.net.log"
    $client = Start-DreamProcess -Exe $exe -WorkingDirectory (Split-Path $exe) -Name "client$i" -Arguments '' -LogDir $logDir -Environment $clientEnv -Hidden $false
    $processes.Add($client)
    Write-Output "Started client$i pid=$($client.Process.Id)"
    Start-Sleep -Seconds 3
}

Write-Section 'Sampling'
$samples = New-Object System.Collections.Generic.List[object]
$deadline = (Get-Date).AddSeconds($DurationSeconds)
while ((Get-Date) -lt $deadline) {
    foreach ($processInfo in $processes) {
        $process = Get-Process -Id $processInfo.Process.Id -ErrorAction SilentlyContinue
        if ($process) {
            $fps = $null
            if ($process.MainWindowTitle -match 'FPS:\s*(\d+)') {
                $fps = [int]$matches[1]
            }

            $samples.Add([pscustomobject]@{
                Name = $processInfo.Name
                Id = $process.Id
                Title = $process.MainWindowTitle
                Responding = $process.Responding
                Cpu = $process.CPU
                WorkingSetMb = [math]::Round($process.WorkingSet64 / 1MB, 1)
                Fps = $fps
            })
        }
    }

    Start-Sleep -Seconds 1
}

Write-Section 'Process Samples'
$samples |
    Group-Object Name |
    ForEach-Object {
        $fpsValues = $_.Group | Where-Object { $null -ne $_.Fps } | Select-Object -ExpandProperty Fps
        [pscustomobject]@{
            Name = $_.Name
            Samples = $_.Count
            MinFps = if ($fpsValues) { ($fpsValues | Measure-Object -Minimum).Minimum } else { $null }
            MaxFps = if ($fpsValues) { ($fpsValues | Measure-Object -Maximum).Maximum } else { $null }
            LastTitle = ($_.Group | Select-Object -Last 1).Title
            Responding = ($_.Group | Select-Object -Last 1).Responding
            WorkingSetMb = ($_.Group | Select-Object -Last 1).WorkingSetMb
        }
    } |
    Format-Table -AutoSize

Write-Section 'Diagnostics Summary'
$allLogs = Get-ChildItem -Path $logDir -File -Filter '*.log' -ErrorAction SilentlyContinue
foreach ($file in $allLogs | Sort-Object Name) {
    $lines = Read-LogLines $file.FullName
    $slowLines = $lines | Where-Object { $_ -match 'slow|queued=' }
    $sendMs = Get-NumberMatches -Lines $lines -Regex 'sendMs=([0-9.]+)'
    $advanceMs = Get-NumberMatches -Lines $lines -Regex 'advanceMs=([0-9.]+)'
    $broadcastMs = Get-NumberMatches -Lines $lines -Regex 'broadcastMs=([0-9.]+)'
    $players2 = ($lines | Where-Object { $_ -match 'players=2' }).Count
    $remoteVisible = ($lines | Where-Object { $_ -match 'client\.render\.remote' -and $_ -match 'visible=[1-9]' }).Count
    $renderPerf = ($lines | Where-Object { $_ -match 'render-perf|renderPerf|chunk|physicsMs|drawMs' }).Count

    [pscustomobject]@{
        Log = $file.Name
        Lines = $lines.Count
        Players2Lines = $players2
        RemoteVisibleLines = $remoteVisible
        SlowOrQueuedLines = $slowLines.Count
        MaxSendMs = [math]::Round((Get-MaxOrZero $sendMs), 2)
        MaxAdvanceMs = [math]::Round((Get-MaxOrZero $advanceMs), 2)
        MaxBroadcastMs = [math]::Round((Get-MaxOrZero $broadcastMs), 2)
        RenderPerfLines = $renderPerf
    }
}

Write-Section 'Last Relevant Lines'
foreach ($file in $allLogs | Sort-Object Name) {
    Write-Output "-- $($file.Name) --"
    Select-String -Path $file.FullName -Pattern 'server.snapshot|client.presenter.state|client.render.remote|slow|queued=|render-perf|physicsMs|drawMs' |
        Select-Object -Last 8 |
        ForEach-Object { $_.Line }
}

if (-not $KeepRunning) {
    Write-Section 'Cleanup'
    foreach ($processInfo in $processes) {
        $process = Get-Process -Id $processInfo.Process.Id -ErrorAction SilentlyContinue
        if ($process) {
            Stop-Process -Id $process.Id -Force
            Write-Output "Stopped $($processInfo.Name) pid=$($process.Id)"
        }
    }
}
else {
    Write-Section 'Left Running'
    $processes | ForEach-Object { Write-Output "$($_.Name) pid=$($_.Process.Id)" }
}

Write-Section 'Done'
Write-Output "Logs: $logDir"
