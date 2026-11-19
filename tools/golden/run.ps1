<#
.SYNOPSIS
  Golden-image run: captures each scene with capture.ps1 (fresh data dir, fixed seed) and compares the PNGs
  with tools/golden/baselines/<scene>/ through golden.py. Exit 0 only when every scene PASSes.

.PARAMETER Scene
  A scene name under tools/golden/scenes (without .json), or 'all'.
.PARAMETER Record
  Capture every scene twice (runs "a" and "b"), store run a downscaled as the baseline and the a-vs-b
  difference as noise.json. Commit tools/golden/baselines/ afterwards.
.PARAMETER Parallel
  Run the captures of all selected scenes (and both record runs) at once. Each has its own data dir.
.PARAMETER FromSource
  Passed to capture.ps1: shaders, ui and config/content come from the source tree.
.PARAMETER Bin
  Passed to capture.ps1 when given: the folder holding dream.exe.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\golden\run.ps1 -Scene all -Record -Parallel
  powershell -ExecutionPolicy Bypass -File .\tools\golden\run.ps1 -Scene spawn-noon
#>
param(
    [Parameter(Mandatory = $true)][string]$Scene,
    [switch]$Record,
    [switch]$Parallel,
    [switch]$FromSource,
    [string]$Bin = '',
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sceneDir = Join-Path $PSScriptRoot 'scenes'
$capture = Join-Path $repo 'skills\capturing-dream-screenshots\scripts\capture.ps1'
$golden = Join-Path $PSScriptRoot 'golden.py'
$outRoot = Join-Path $repo 'artifacts\golden\capture'
$psExe = (Get-Process -Id $PID).Path

function Get-SceneNames([string]$selector, [string]$dir) {
    $all = @(Get-ChildItem $dir -Filter *.json -ErrorAction SilentlyContinue | ForEach-Object { $_.BaseName } | Sort-Object)
    if ($selector -eq 'all') { return $all }
    if ($all -notcontains $selector) { throw "No scene '$selector' in $dir (have: $($all -join ', '))" }
    return @($selector)
}

# Builds the capture.ps1 argument list for one run (a plain string array; values are quoted for -File).
function Get-CaptureArguments($def, [string]$name) {
    $cap = Join-Path $outRoot $name
    # Stale PNGs from an earlier run would be compared as extra steps.
    if (Test-Path $cap) { Get-ChildItem $cap -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force }
    $list = [System.Collections.Generic.List[string]]::new()
    $list.AddRange([string[]]@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$capture`""))
    $list.AddRange([string[]]@('-Name', $name, '-Script', "`"$($def.script)`"", '-TimeOfDay', [string]$def.timeOfDay))
    $list.AddRange([string[]]@('-OutRoot', "`"$outRoot`"", '-DataDir', "`"$(Join-Path $cap 'data')`""))
    $list.AddRange([string[]]@('-Seed', [string]$def.seed, '-TimeoutSeconds', [string]$TimeoutSeconds))
    $envList = @($def.env | Where-Object { $_ })
    if ($envList.Count -gt 0) { $list.AddRange([string[]]@('-Env', "`"$($envList -join ',')`"")) }
    if ($FromSource) { $list.Add('-FromSource') }
    if ($Bin) { $list.AddRange([string[]]@('-Bin', "`"$Bin`"")) }
    return , $list.ToArray()
}

# Starts every run, waits for all of them (one at a time unless -Parallel) and returns name -> exit code.
function Invoke-Captures([object[]]$runs) {
    $codes = @{}
    $started = @()
    foreach ($run in $runs) {
        $log = Join-Path $outRoot ("{0}.run.log" -f $run.Name)
        New-Item -ItemType Directory -Force $outRoot | Out-Null
        $p = Start-Process -FilePath $psExe -ArgumentList $run.Arguments -PassThru -NoNewWindow `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        $null = $p.Handle
        if ($Parallel) { $started += [pscustomobject]@{ Name = $run.Name; Process = $p } }
        else { $p.WaitForExit(); $codes[$run.Name] = $p.ExitCode }
    }
    foreach ($s in $started) { $s.Process.WaitForExit(); $codes[$s.Name] = $s.Process.ExitCode }
    return $codes
}

$names = Get-SceneNames $Scene $sceneDir
$defs = @{}
foreach ($n in $names) { $defs[$n] = Get-Content (Join-Path $sceneDir "$n.json") -Raw | ConvertFrom-Json }

$suffixes = if ($Record) { @('-a', '-b') } else { @('') }
$runs = @()
foreach ($n in $names) {
    foreach ($suffix in $suffixes) {
        $runName = "golden-$n$suffix"
        $runs += [pscustomobject]@{ Scene = $n; Name = $runName; Arguments = (Get-CaptureArguments $defs[$n] $runName) }
    }
}

$codes = Invoke-Captures $runs
$failed = $false
foreach ($run in $runs) {
    if ($codes[$run.Name] -ne 0) {
        Write-Host ("FAIL {0}: capture.ps1 exit code {1} (see {2}.run.log)" -f $run.Name, $codes[$run.Name], (Join-Path $outRoot $run.Name))
        $errLog = Join-Path $outRoot ("{0}.run.log.err" -f $run.Name)
        if (Test-Path $errLog) { Get-Content $errLog -Tail 5 | ForEach-Object { Write-Host "  $_" } }
        $failed = $true
    }
}
if ($failed) { exit 1 }

foreach ($n in $names) {
    if ($Record) {
        & python -I $golden record (Join-Path $outRoot "golden-$n-a") (Join-Path $outRoot "golden-$n-b") $n
    }
    else {
        & python -I $golden compare (Join-Path $outRoot "golden-$n") $n
    }
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}
if ($failed) { exit 1 }
exit 0
