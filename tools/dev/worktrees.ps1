<#
.SYNOPSIS
  Lists the git worktrees of this repo with merge state, dirtiness and build-output sizes.

.DESCRIPTION
  Read-only by default: it prints one row per worktree plus totals and changes nothing.

  -CleanBuildOutput  deletes dream/bin, dream/obj, dream.Tests/bin, dream.Tests/obj and artifacts/*-bin
                     in each worktree, except the current worktree, the main worktree, and any worktree
                     with a dream.exe or testhost running from its path.
  -RemoveMerged      runs `git worktree remove` (never --force) for worktrees whose branch is merged into
                     the base branch AND that are clean AND not locked; never the main or current one.
                     Asks before each removal unless -Yes.
  Never deletes branches and never uses git stash.

.PARAMETER Base
  The branch "merged" is measured against. Default master.
.PARAMETER Repo
  Any path inside the repo to inspect. Default: the current directory.
#>
[CmdletBinding()]
param(
    [switch]$CleanBuildOutput,
    [switch]$RemoveMerged,
    [switch]$Yes,
    [string]$Base = 'master',
    [string]$Repo = (Get-Location).Path
)

$ErrorActionPreference = 'Continue'  # git writes to stderr for odd worktrees; every call is checked explicitly

function Invoke-Git {
    param([string]$Dir, [string[]]$GitArgs)
    $out = & git -C $Dir @GitArgs 2>$null
    return ,@($out)
}

function Get-DirectoryBytes {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return 0L }
    $sum = 0L
    try {
        foreach ($f in [System.IO.Directory]::EnumerateFiles($Path, '*', [System.IO.SearchOption]::AllDirectories)) {
            try { $sum += ([System.IO.FileInfo]$f).Length } catch { }
        }
    } catch { }
    return $sum
}

function Format-Size {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N0} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N0} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function Get-BuildOutputDirs {
    param([string]$Root)
    $dirs = New-Object System.Collections.Generic.List[string]
    foreach ($rel in 'dream/bin', 'dream/obj', 'dream.Tests/bin', 'dream.Tests/obj') {
        $p = Join-Path $Root $rel
        if (Test-Path -LiteralPath $p -PathType Container) { $dirs.Add($p) }
    }
    $art = Join-Path $Root 'artifacts'
    if (Test-Path -LiteralPath $art -PathType Container) {
        foreach ($d in Get-ChildItem -LiteralPath $art -Directory -Filter '*-bin' -ErrorAction SilentlyContinue) {
            $dirs.Add($d.FullName)
        }
    }
    return ,$dirs.ToArray()
}

function Get-NormalPath {
    param([string]$Path)
    return ([System.IO.Path]::GetFullPath($Path)).TrimEnd('\', '/').Replace('/', '\')
}

function Get-RunningFromPaths {
    # Executable paths and command lines of running dream.exe / testhost processes, lower-cased.
    $paths = New-Object System.Collections.Generic.List[string]
    try {
        $procs = Get-CimInstance Win32_Process -ErrorAction Stop |
            Where-Object { $_.Name -like 'dream.exe' -or $_.Name -like 'testhost*' }
        foreach ($p in $procs) {
            if ($p.ExecutablePath) { $paths.Add($p.ExecutablePath.ToLowerInvariant().Replace('/', '\')) }
            if ($p.CommandLine) { $paths.Add($p.CommandLine.ToLowerInvariant().Replace('/', '\')) }
        }
    } catch { }
    return ,$paths.ToArray()
}

function Test-RunningIn {
    param([string]$WorktreePath, [string[]]$Running)
    $needle = (Get-NormalPath $WorktreePath).ToLowerInvariant() + '\'
    foreach ($r in $Running) { if ($r.Contains($needle)) { return $true } }
    return $false
}

$top = (Invoke-Git $Repo @('rev-parse', '--show-toplevel'))[0]
if (-not $top) { Write-Error "Not inside a git repository: $Repo"; exit 1 }
$current = Get-NormalPath $top

# Parse `git worktree list --porcelain`: blank-line separated records.
$raw = Invoke-Git $Repo @('worktree', 'list', '--porcelain')
$entries = New-Object System.Collections.Generic.List[object]
$cur = $null
foreach ($line in $raw) {
    if ([string]::IsNullOrWhiteSpace($line)) { if ($cur) { $entries.Add($cur); $cur = $null }; continue }
    if ($line.StartsWith('worktree ')) {
        if ($cur) { $entries.Add($cur) }
        $cur = [ordered]@{ Path = $line.Substring(9); Head = ''; Branch = '(detached)'; Locked = $false; Prunable = $false; Bare = $false }
    } elseif ($cur) {
        if ($line.StartsWith('HEAD ')) { $cur.Head = $line.Substring(5) }
        elseif ($line.StartsWith('branch ')) { $cur.Branch = $line.Substring(7) -replace '^refs/heads/', '' }
        elseif ($line -eq 'bare') { $cur.Bare = $true }
        elseif ($line -like 'locked*') { $cur.Locked = $true }
        elseif ($line -like 'prunable*') { $cur.Prunable = $true }
    }
}
if ($cur) { $entries.Add($cur) }

$running = Get-RunningFromPaths
$mode = if ($CleanBuildOutput -or $RemoveMerged) { 'ACTIVE' } else { 'read-only' }
Write-Host ("worktrees: {0} (base {1}, mode {2})" -f $entries.Count, $Base, $mode)

$rows = New-Object System.Collections.Generic.List[object]
$index = 0
foreach ($e in $entries) {
    $path = Get-NormalPath $e.Path
    $isMain = ($index -eq 0)
    $index++
    $exists = Test-Path -LiteralPath $path -PathType Container
    $merged = $false
    if ($e.Head -and $e.Head -notmatch '^0+$') {
        & git -C $Repo merge-base --is-ancestor $e.Head $Base 2>$null
        $merged = ($LASTEXITCODE -eq 0)
    }
    $dirty = $false
    if ($exists -and -not $e.Bare) {
        $status = Invoke-Git $path @('status', '--porcelain')
        $dirty = (@($status | Where-Object { $_ }).Count -gt 0)
    }
    $dirs = if ($exists) { Get-BuildOutputDirs $path } else { @() }
    $bytes = 0L
    foreach ($d in $dirs) { $bytes += Get-DirectoryBytes $d }
    $rows.Add([pscustomobject]@{
        Path = $path; Branch = $e.Branch; IsMain = $isMain; IsCurrent = ($path -ieq $current)
        Merged = $merged; Dirty = $dirty; Locked = $e.Locked; Prunable = $e.Prunable
        Exists = $exists; Bytes = $bytes; BuildDirs = $dirs
        Busy = ($exists -and (Test-RunningIn $path $running))
    })
}

foreach ($r in $rows) {
    $flags = @()
    if ($r.IsMain) { $flags += 'main' }
    if ($r.IsCurrent) { $flags += 'current' }
    if ($r.Merged) { $flags += 'merged' }
    if ($r.Dirty) { $flags += 'dirty' }
    if ($r.Locked) { $flags += 'locked' }
    if ($r.Prunable) { $flags += 'prunable' }
    if ($r.Busy) { $flags += 'running' }
    Write-Host ("{0,10}  {1,-28} [{2}]  {3}" -f (Format-Size $r.Bytes), $r.Branch, ($flags -join ','), $r.Path)
}

$total = ($rows | Measure-Object -Property Bytes -Sum).Sum
$mergedCount = @($rows | Where-Object { $_.Merged -and -not $_.IsMain }).Count
$removable = @($rows | Where-Object { $_.Merged -and -not $_.Dirty -and -not $_.Locked -and -not $_.IsMain -and -not $_.IsCurrent -and $_.Exists })
$cleanable = @($rows | Where-Object { -not $_.IsMain -and -not $_.IsCurrent -and -not $_.Busy -and $_.Bytes -gt 0 })
$cleanBytes = ($cleanable | Measure-Object -Property Bytes -Sum).Sum
Write-Host ''
Write-Host ("totals: {0} worktrees, build output {1}, merged {2}, dirty {3}, locked {4}, prunable {5}, running {6}" -f `
    $rows.Count, (Format-Size ([long]$total)), $mergedCount, @($rows | Where-Object Dirty).Count, `
    @($rows | Where-Object Locked).Count, @($rows | Where-Object Prunable).Count, @($rows | Where-Object Busy).Count)
Write-Host ("cleanable with -CleanBuildOutput: {0} in {1} worktrees; removable with -RemoveMerged: {2}" -f `
    (Format-Size ([long]$cleanBytes)), $cleanable.Count, $removable.Count)

if ($CleanBuildOutput) {
    $freed = 0L
    foreach ($r in $rows) {
        if ($r.IsMain -or $r.IsCurrent) { continue }
        if ($r.Busy) { Write-Host "skip (dream.exe/testhost running): $($r.Path)"; continue }
        foreach ($d in $r.BuildDirs) {
            $size = Get-DirectoryBytes $d
            try {
                Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction Stop
                $freed += $size
            } catch {
                Write-Host "could not delete $d : $($_.Exception.Message)"
            }
        }
    }
    Write-Host ("cleaned build output: {0} freed" -f (Format-Size $freed))
}

if ($RemoveMerged) {
    foreach ($r in $removable) {
        if ($r.Busy) { Write-Host "skip (running): $($r.Path)"; continue }
        $go = $Yes
        if (-not $go) {
            $answer = Read-Host "Remove worktree $($r.Path) (branch $($r.Branch))? [y/N]"
            $go = ($answer -match '^(y|yes)$')
        }
        if (-not $go) { Write-Host "kept: $($r.Path)"; continue }
        & git -C $Repo worktree remove $r.Path
        if ($LASTEXITCODE -eq 0) { Write-Host "removed: $($r.Path)" } else { Write-Host "git refused (left in place): $($r.Path)" }
    }
}
