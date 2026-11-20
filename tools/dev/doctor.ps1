<#
Read-only environment check for the justfile: toolchain, Python venv, running game/test processes, disk.
Changes nothing. Exit 1 when a required tool is missing.
#>
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\guard.ps1"
$repo = Get-RepoRoot
$bad = 0

function Report($ok, $label, $detail) {
    $mark = if ($ok) { 'ok  ' } else { 'WARN' }
    $color = if ($ok) { 'Green' } else { 'Yellow' }
    Write-Host ("{0} {1,-14} {2}" -f $mark, $label, $detail) -ForegroundColor $color
}

$want = (Get-Content (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    Push-Location $repo; $have = (& dotnet --version) 2>$null; Pop-Location
    Report ($LASTEXITCODE -eq 0) 'dotnet' "$have (global.json $want, rollForward latestFeature)"
} else { Report $false 'dotnet' 'not on PATH (.NET 10 SDK required)'; $bad++ }

$just = Get-Command just -ErrorAction SilentlyContinue
if ($just) {
    $v = (& just --version) -replace '^just\s+', ''
    $okJust = [version]($v -replace '[^0-9.].*$', '') -ge [version]'1.42'
    Report $okJust 'just' "$v (needs >= 1.42 for [parallel])"
} else { Report $false 'just' 'not on PATH: winget install --id Casey.Just -e' }

$venv = Join-Path $repo '.venv\Scripts\python.exe'
if (Test-Path $venv) {
    $mods = & $venv -c "import numpy, PIL; print('numpy', numpy.__version__, 'pillow', PIL.__version__)" 2>&1
    Report ($LASTEXITCODE -eq 0) 'python venv' "$mods"
} else { Report $false 'python venv' 'no .venv: just venv' }

$procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -in 'dream.exe', 'testhost.exe' })
if ($procs.Count) {
    foreach ($p in $procs) { Report $false 'running' ("{0} pid {1} {2}" -f $p.Name, $p.ProcessId, $p.ExecutablePath) }
    Write-Host '     dream.exe in dream\bin blocks build/rebuild (use build-verify); a testhost blocks test recipes.'
} else { Report $true 'running' 'no dream.exe or testhost' }

$drive = (Split-Path $repo -Qualifier).TrimEnd(':')
$free = (Get-PSDrive $drive).Free
Report ($free -gt 20GB) 'disk' ("{0} free on {1}: (stale worktree bin/obj fill it; just worktrees)" -f (Format-Bytes $free), $drive)

if ($bad) { exit 1 }
exit 0
