<#
Deletes regenerable build output and scratch artifacts of THIS checkout, for the justfile.

  tools\dev\clean.ps1 -Target build|artifacts|all [-DryRun] [-Yes]

build      dream\bin, dream\obj, dream.Tests\bin, dream.Tests\obj.
           Keeps the writable state that lives in the exe folder: config\player-profiles.db (+ -wal/-shm)
           and config\desktop-client.txt. A clean never costs you your worlds.
bins       only the side build outputs under artifacts\ (*-bin*, obj-*, Debug, Release, testresults); keeps
           captures, logs and images. Usually most of the space.
artifacts  everything under artifacts\ except db-backups\ and test-durations.csv (test timing history).
all        both.

Guards: every target must resolve strictly inside the repository; refuses (exit 1) while a dream.exe, testhost,
dotnet, MSBuild, VBCSCompiler or python process names a target in its path or command line; prints the plan
and size, then asks unless -Yes. -DryRun prints the plan only. Other worktrees are never touched: use
tools\dev\worktrees.ps1 -CleanBuildOutput for those.
#>
param(
    [Parameter(Mandatory)][ValidateSet('build', 'bins', 'artifacts', 'all')][string]$Target,
    [switch]$DryRun,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\guard.ps1"
$repo = Get-RepoRoot

$keepInBin = @('player-profiles.db', 'player-profiles.db-wal', 'player-profiles.db-shm', 'desktop-client.txt')
$keepInArtifacts = @('db-backups', 'test-durations.csv')

$plan = New-Object System.Collections.Generic.List[object]   # @{ Path; Kind = 'dir'|'file'; Preserve = [string[]] }

if ($Target -in 'build', 'all') {
    foreach ($rel in 'dream\bin', 'dream\obj', 'dream.Tests\bin', 'dream.Tests\obj') {
        $p = Join-Path $repo $rel
        if (Test-Path -LiteralPath $p) {
            $preserve = if ($rel -eq 'dream\bin') { $keepInBin } else { @() }
            $plan.Add(@{ Path = $p; Kind = 'dir'; Preserve = $preserve })
        }
    }
}
if ($Target -eq 'bins') {
    # Only the side build outputs under artifacts\ (verify-bin, capture-bin, hud-bin, obj-*, ...): the bulk of
    # the space, all regenerable. Captures, logs and PNGs stay.
    $art = Join-Path $repo 'artifacts'
    if (Test-Path -LiteralPath $art) {
        Get-ChildItem -LiteralPath $art -Directory -Force |
            Where-Object { $_.Name -match '(^|-)bin($|-)|^obj-|^(Debug|Release|testresults)$' } |
            ForEach-Object { $plan.Add(@{ Path = $_.FullName; Kind = 'dir'; Preserve = @() }) }
    }
}
if ($Target -in 'artifacts', 'all') {
    $art = Join-Path $repo 'artifacts'
    if (Test-Path -LiteralPath $art) {
        foreach ($item in Get-ChildItem -LiteralPath $art -Force) {
            if ($keepInArtifacts -contains $item.Name) { continue }
            $plan.Add(@{ Path = $item.FullName; Kind = $(if ($item.PSIsContainer) { 'dir' } else { 'file' }); Preserve = @() })
        }
    }
}

if ($plan.Count -eq 0) { Write-Host 'nothing to clean'; exit 0 }

foreach ($entry in $plan) { $null = Assert-InsideRepo -Path $entry.Path }

$busy = @()
foreach ($entry in $plan) {
    foreach ($proc in @(Get-ProcessesUsingPath -Path $entry.Path)) {
        $busy += "{0} pid {1} uses {2}" -f $proc.Name, $proc.ProcessId, $entry.Path
    }
}
if ($busy.Count -gt 0) {
    $verb = if ($DryRun) { 'busy' } else { 'refusing' }
    $busy | Sort-Object -Unique | ForEach-Object { Write-Host "${verb}: $_" -ForegroundColor Red }
    Write-Host 'Let it finish (or close it yourself); this script never kills processes.' -ForegroundColor Red
    if (-not $DryRun) { exit 1 }
}

$total = 0L
foreach ($entry in $plan) {
    $bytes = if ($entry.Kind -eq 'dir') { Get-FolderBytes -Path $entry.Path } else { (Get-Item -LiteralPath $entry.Path -Force).Length }
    $total += $bytes
    $note = if ($entry.Preserve.Count) { "  (keeps $($entry.Preserve -join ', '))" } else { '' }
    Write-Host ("{0,10}  {1}{2}" -f (Format-Bytes $bytes), $entry.Path.Substring($repo.Length + 1), $note)
}
Write-Host ("{0,10}  total" -f (Format-Bytes $total))

if ($DryRun) { Write-Host 'dry run: nothing deleted'; exit 0 }
if (-not $Yes) {
    $answer = Read-Host 'Delete these? [y/N]'
    if ($answer -notmatch '^(y|yes)$') { Write-Host 'cancelled'; exit 1 }
}

$failures = 0
foreach ($entry in $plan) {
    try {
        if ($entry.Kind -eq 'file') {
            Remove-Item -LiteralPath $entry.Path -Force
        } elseif ($entry.Preserve.Count -eq 0) {
            Remove-Item -LiteralPath $entry.Path -Recurse -Force
        } else {
            # Delete file by file, skipping the preserved state, then prune the empty folders bottom-up.
            Get-ChildItem -LiteralPath $entry.Path -Recurse -File -Force |
                Where-Object { $entry.Preserve -notcontains $_.Name } |
                Remove-Item -Force
            Get-ChildItem -LiteralPath $entry.Path -Recurse -Directory -Force |
                Sort-Object { $_.FullName.Length } -Descending |
                Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
                Remove-Item -Force
        }
    } catch {
        $failures++
        Write-Host "could not fully delete $($entry.Path): $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
if ($failures -gt 0) { Write-Host "$failures target(s) left partly in place (a file is open?)"; exit 1 }
Write-Host 'clean'
exit 0
