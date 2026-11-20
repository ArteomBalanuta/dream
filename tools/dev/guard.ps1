<#
Shared safety checks for the destructive dev scripts (db.ps1, clean.ps1) and the justfile.

Dot-source it for the functions:   . "$PSScriptRoot\guard.ps1"
Or run it as a check from the justfile:
  tools\dev\guard.ps1 -BinUnlocked [-Configuration Debug]
      exit 1 when a dream.exe runs from dream\bin\<Configuration>, whose files a build would have to overwrite.

It never stops or kills anything; it only reports and refuses.
#>
param(
    [switch]$BinUnlocked,
    [string]$Configuration = 'Debug'
)

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\')

function Get-RepoRoot { $script:RepoRoot }

# Processes whose executable path or command line mentions $Path: the game, test hosts, and the build
# servers that keep files under bin/obj open. Best effort: a node-reuse MSBuild often names no path.
function Get-ProcessesUsingPath {
    param([Parameter(Mandatory)][string]$Path)
    $needle = $Path.TrimEnd('\')
    $names = @('dream.exe', 'testhost.exe', 'dotnet.exe', 'MSBuild.exe', 'VBCSCompiler.exe', 'python.exe')
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $names -contains $_.Name } |
        Where-Object { $_.ProcessId -ne $PID } |
        Where-Object {
            ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($needle, [StringComparison]::OrdinalIgnoreCase)) -or
            ($_.CommandLine -and $_.CommandLine.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        }
}

# True when another process holds the file open without sharing: the way dream holds a world's host lease
# ({db}.world-N.lock) and the maintenance lock. The OS drops the lock when the holder dies.
function Test-FileLockHeld {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
        $stream.Dispose()
        return $false
    } catch [System.IO.IOException] {
        return $true
    } catch [System.UnauthorizedAccessException] {
        return $true
    }
}

# Full path of $Path, refusing anything that is not strictly inside the repository (never the root itself).
function Assert-InsideRepo {
    param([Parameter(Mandatory)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $full.StartsWith("$script:RepoRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "refusing: '$full' is not inside the repository '$script:RepoRoot'"
    }
    return $full
}

function Test-InsideRepo {
    param([Parameter(Mandatory)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    return $full.StartsWith("$script:RepoRoot\", [StringComparison]::OrdinalIgnoreCase)
}

function Get-FolderBytes {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($sum) { [long]$sum } else { 0 }
}

function Format-Bytes {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return '{0:N1} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:N0} KB' -f ($Bytes / 1KB) }
    return "$Bytes B"
}

function Confirm-Typed {
    param([string]$Prompt, [string]$Expected = 'yes')
    $answer = Read-Host "$Prompt Type '$Expected' to continue"
    return $answer -ceq $Expected
}

if ($MyInvocation.InvocationName -ne '.' -and $BinUnlocked) {
    $bin = Join-Path $script:RepoRoot "dream\bin\$Configuration"
    $holders = @(Get-ProcessesUsingPath -Path $bin | Where-Object { $_.Name -eq 'dream.exe' })
    if ($holders.Count -gt 0) {
        $ids = ($holders | ForEach-Object { $_.ProcessId }) -join ', '
        Write-Host "refusing: dream.exe (pid $ids) runs from $bin and locks its files." -ForegroundColor Red
        Write-Host "Close the game yourself, or build elsewhere: just build-verify" -ForegroundColor Red
        exit 1
    }
    exit 0
}
