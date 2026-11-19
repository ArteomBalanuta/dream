<#
.SYNOPSIS
  Runs a tier of dream.Tests, tees the output, enforces a timeout and records the duration.

.DESCRIPTION
  tools/dev/test.ps1 -Area Content|Rendering|Simulation|Net|Client|Fast|Full [-Filter x] [-TimeoutMinutes 20]

  Areas (dotnet test --filter):
    Content     FullyQualifiedName~Dream.Tests.Simulation.Content
    Rendering   FullyQualifiedName~Dream.Tests.Rendering
    Simulation  FullyQualifiedName~Dream.Tests.Simulation
    Net         FullyQualifiedName~Dream.Tests.Multiplayer | ~Dream.Tests.Server
    Client      FullyQualifiedName~Dream.Tests.Client
    Fast        everything except Dream.Tests.Integration, .Load and .BugHunt
    Full        no filter (about 3.5 to 4.5 minutes)

  -Filter narrows the area further: a bare word means FullyQualifiedName~word, anything containing
  ~ = ! & or | is used as a filter expression. Refuses to start while any testhost runs (concurrent
  suites produce spurious persistence failures) unless -AllowConcurrent. On timeout the watchdog
  kills only the dotnet process tree this script started.

  Output: artifacts/test-logs/<area>-<stamp>.log. Appends utc,sha,area,seconds,passed,failed,skipped
  to artifacts/test-durations.csv and prints the last 5 rows for the area. Exit code: dotnet's, 124 on
  timeout, 3 when refused because of a running testhost.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Content', 'Rendering', 'Simulation', 'Net', 'Client', 'Fast', 'Full')]
    [string]$Area,
    [string]$Filter = '',
    [int]$TimeoutMinutes = 20,
    [switch]$AllowConcurrent
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'dream.Tests\dream.Tests.csproj'

$areaFilters = @{
    Content    = 'FullyQualifiedName~Dream.Tests.Simulation.Content'
    Rendering  = 'FullyQualifiedName~Dream.Tests.Rendering'
    Simulation = 'FullyQualifiedName~Dream.Tests.Simulation'
    Net        = 'FullyQualifiedName~Dream.Tests.Multiplayer|FullyQualifiedName~Dream.Tests.Server'
    Client     = 'FullyQualifiedName~Dream.Tests.Client'
    Fast       = 'FullyQualifiedName!~Dream.Tests.Integration&FullyQualifiedName!~Dream.Tests.Load&FullyQualifiedName!~Dream.Tests.BugHunt'
    Full       = ''
}

$running = @(Get-Process -Name 'testhost*' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and -not $AllowConcurrent) {
    Write-Host ("refusing to start: {0} testhost process(es) running (ids {1}); concurrent suites give false failures. Use -AllowConcurrent to override." -f `
        $running.Count, (($running | ForEach-Object { $_.Id }) -join ','))
    exit 3
}

$expression = $areaFilters[$Area]
if ($Filter) {
    $extra = if ($Filter -match '[~=!&|]') { $Filter } else { "FullyQualifiedName~$Filter" }
    $expression = if ($expression) { "($expression)&($extra)" } else { $extra }
}

$artifacts = Join-Path $repo 'artifacts'
$logDir = Join-Path $artifacts 'test-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$log = Join-Path $logDir "$($Area.ToLowerInvariant())-$stamp.log"
$csv = Join-Path $artifacts 'test-durations.csv'

$sha = (& git -C $repo rev-parse --short HEAD 2>$null)
if (-not $sha) { $sha = 'unknown' }

$dotnetArgs = @('test', $project)
if ($expression) { $dotnetArgs += @('--filter', $expression) }
Write-Host ("area {0}, filter '{1}', log {2}" -f $Area, $expression, $log)

# One process we own, output teed line by line to the console and the log.
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
$psi.WorkingDirectory = $repo
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
# Arguments string (ArgumentList does not exist on Windows PowerShell 5.1); every part is quoted.
$psi.Arguments = (($dotnetArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' ')

$writer = New-Object System.IO.StreamWriter($log, $false, (New-Object System.Text.UTF8Encoding($false)))
$writer.AutoFlush = $true
$sync = [object]::new()
$handler = {
    if ($null -ne $EventArgs.Data) {
        [System.Threading.Monitor]::Enter($Event.MessageData.Sync)
        try {
            $Event.MessageData.Writer.WriteLine($EventArgs.Data)
            [Console]::WriteLine($EventArgs.Data)
        } finally { [System.Threading.Monitor]::Exit($Event.MessageData.Sync) }
    }
}
$state = [pscustomobject]@{ Writer = $writer; Sync = $sync }

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$subs = @(
    Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action $handler -MessageData $state
    Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action $handler -MessageData $state
)

$watch = [System.Diagnostics.Stopwatch]::StartNew()
$timedOut = $false
try {
    [void]$proc.Start()
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()
    if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        $timedOut = $true
        Write-Host "timeout after $TimeoutMinutes min: killing the dotnet test process tree (pid $($proc.Id))"
        & taskkill.exe /PID $proc.Id /T /F | Out-Null
        [void]$proc.WaitForExit(15000)
    } else {
        $proc.WaitForExit()   # flush async readers
    }
} finally {
    $watch.Stop()
    Start-Sleep -Milliseconds 300
    foreach ($s in $subs) { Unregister-Event -SubscriptionId $s.Id -ErrorAction SilentlyContinue }
    $writer.Flush()
    $writer.Dispose()
}

$exitCode = if ($timedOut) { 124 } else { $proc.ExitCode }

# Counts from the log: "Passed!  - Failed:     0, Passed:  9220, Skipped:     0, Total:  9220, Duration: ..."
$passed = 0; $failed = 0; $skipped = 0
$summary = Select-String -Path $log -Pattern '(Passed|Failed)!\s+-\s+Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)' | Select-Object -Last 1
if ($summary) {
    $failed = [int]$summary.Matches[0].Groups[2].Value
    $passed = [int]$summary.Matches[0].Groups[3].Value
    $skipped = [int]$summary.Matches[0].Groups[4].Value
}

$seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
if (-not (Test-Path -LiteralPath $csv)) {
    Set-Content -LiteralPath $csv -Value 'utc,sha,area,seconds,passed,failed,skipped' -Encoding ascii
}
$row = ('{0},{1},{2},{3},{4},{5},{6}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'), $sha, $Area, `
    $seconds.ToString([System.Globalization.CultureInfo]::InvariantCulture), $passed, $failed, $skipped)
Add-Content -LiteralPath $csv -Value $row -Encoding ascii

Write-Host ''
Write-Host ("{0}: exit {1}, {2}s, passed {3}, failed {4}, skipped {5}" -f $Area, $exitCode, $seconds, $passed, $failed, $skipped)
Write-Host 'last runs for this area (utc,sha,area,seconds,passed,failed,skipped):'
Get-Content -LiteralPath $csv | Select-Object -Skip 1 | Where-Object { $_.Split(',')[2] -eq $Area } | Select-Object -Last 5 | ForEach-Object { Write-Host "  $_" }

exit $exitCode
