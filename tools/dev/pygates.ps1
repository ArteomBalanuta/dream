<#
Runs the repo's standalone Python gates in parallel and reports one line per gate.

  tools\dev\pygates.ps1 [-Group all|golden|gallery|audio|ui] [-Python <exe>] [-Throttle N]

Every gate is an independent process that only reads the repo (none writes into dream\), so they parallelise
safely; logs go to artifacts\py-gates\<gate>.log. Exit 0 only when every gate exits 0.

Not included: check_look.py (measures a capture frame you pass it), the generators (they write tracked files),
and anything dotnet (one test suite at a time - see tools\dev\test.ps1).
#>
param(
    [ValidateSet('all', 'quick', 'golden', 'gallery', 'audio', 'ui')][string]$Group = 'all',
    [string]$Python,
    [int]$Throttle = [Math]::Max(2, [Environment]::ProcessorCount - 2)
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Python) {
    $venv = Join-Path $repo '.venv\Scripts\python.exe'
    $Python = if (Test-Path $venv) { $venv } else { 'python' }
}

$gates = New-Object System.Collections.Generic.List[object]
function Add-Gate($name, [string[]]$arguments) { $gates.Add([pscustomobject]@{ Name = $name; Args = $arguments }) }

if ($Group -in 'all', 'quick', 'golden') { Add-Gate 'golden' @('-I', 'tools\golden\tests\test_golden.py') }
if ($Group -in 'all', 'quick', 'gallery') { Add-Gate 'gallery' @('-I', 'tools\dev\gallery.py', '--selftest') }
if ($Group -in 'all', 'quick', 'audio') { Add-Gate 'audio' @('-I', 'tools\audio\build_all.py', '--check-only') }
if ($Group -in 'all', 'ui') {
    Get-ChildItem (Join-Path $repo 'tools\ui-assets\tests') -Filter 'check_*.py' |
        Where-Object { $_.Name -ne 'check_look.py' } |
        Sort-Object Name |
        ForEach-Object { Add-Gate ('ui-' + $_.BaseName.Substring(6)) @("tools\ui-assets\tests\$($_.Name)") }
}

$logDir = Join-Path $repo 'artifacts\py-gates'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$pending = [System.Collections.Generic.Queue[object]]::new($gates)
$running = @{}
$results = New-Object System.Collections.Generic.List[object]
$started = Get-Date

while ($pending.Count -gt 0 -or $running.Count -gt 0) {
    while ($pending.Count -gt 0 -and $running.Count -lt $Throttle) {
        $gate = $pending.Dequeue()
        $log = Join-Path $logDir "$($gate.Name).log"
        $proc = Start-Process -FilePath $Python -ArgumentList $gate.Args -WorkingDirectory $repo -NoNewWindow -PassThru `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        $null = $proc.Handle   # keep the handle so ExitCode survives the process
        $running[$proc.Id] = [pscustomobject]@{ Gate = $gate; Proc = $proc; Log = $log; Start = Get-Date }
    }
    foreach ($id in @($running.Keys)) {
        $r = $running[$id]
        if ($r.Proc.HasExited) {
            $r.Proc.WaitForExit()
            $results.Add([pscustomobject]@{ Name = $r.Gate.Name; Code = $r.Proc.ExitCode; Seconds = ((Get-Date) - $r.Start).TotalSeconds; Log = $r.Log })
            $running.Remove($id)
        }
    }
    Start-Sleep -Milliseconds 100
}

$failed = 0
foreach ($r in $results | Sort-Object Name) {
    $ok = $r.Code -eq 0
    if (-not $ok) { $failed++ }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0,-4} {1,-24} {2,6:N1}s  {3}" -f $(if ($ok) { 'ok' } else { 'FAIL' }), $r.Name, $r.Seconds, $(if ($ok) { '' } else { $r.Log })) -ForegroundColor $color
}
Write-Host ("{0} gates, {1} failed, {2:N0}s wall clock, logs in {3}" -f $results.Count, $failed, ((Get-Date) - $started).TotalSeconds, $logDir)
if ($failed -gt 0) { exit 1 }
exit 0
