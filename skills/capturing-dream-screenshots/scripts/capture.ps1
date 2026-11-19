<#
.SYNOPSIS
  One DREAM_HUD_CAPTURE run: launches one auto-connecting capture client with its own fresh data dir and a fixed
  simulation seed, waits for it to exit by itself, and prints the PNGs it wrote plus the stderr tail and every
  "[hud-capture] dev" line. Runs with distinct -Name (so distinct data dirs) may run in parallel; -Serial restores
  the old wait for any running dream.exe.

  Exit codes: 0 ok; 1 timeout or no PNG; 2 idle-wait aborted (-Serial only); 3 a dev step errored or got no reply
  (suppress with -AllowDevErrors).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\skills\capturing-dream-screenshots\scripts\capture.ps1 `
    -Name ground-noon -TimeOfDay noon -Script 'play,keys:g:3,wait:60,snap'

.EXAMPLE
  ... -Name ground-off-night -TimeOfDay night -Script 'play,wait:60,snap' -Env DREAM_GROUND_SCATTER=0,DREAM_MAX_FPS=60
#>
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Script,
    [string]$TimeOfDay = 'noon',
    [string[]]$Env = @(),
    [string]$Bin = '',
    [string]$OutRoot = '',
    [string]$IdentityFile = '',
    [int]$TimeoutSeconds = 300,
    [int]$WaitForIdleSeconds = 900,
    [string]$DataDir = '',
    [uint64]$Seed = 1,
    [switch]$KeepData,
    [switch]$FromSource,
    [switch]$Serial,
    [switch]$AllowDevErrors
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $Bin) { $Bin = Join-Path $repo 'artifacts\capture-bin\Release\net10.0' }
if (-not $OutRoot) { $OutRoot = Join-Path $repo 'artifacts\captures' }
# Resolve against $PWD (the shell's location), not the .NET process CWD that GetFullPath would use.
function Resolve-FullPath([string]$path) {
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.TrimEnd('\', '/') -ieq $root.TrimEnd('\', '/')) { return $root }
    return $full.TrimEnd('\', '/')
}
$Bin = Resolve-FullPath $Bin
$OutRoot = Resolve-FullPath $OutRoot
if (-not $DataDir) { $DataDir = Join-Path (Join-Path $OutRoot $Name) 'data' }
$DataDir = Resolve-FullPath $DataDir
$DataMarker = '.dream-capture-data'

function Test-NoColon([string]$path, [string]$what) {
    # A drive qualifier ("G:") is the only colon allowed; anywhere else it is an illegal Windows file name that shows up
    # as a silent auto-connect hang, not as an error.
    $rest = if ($path -match '^[A-Za-z]:') { $path.Substring(2) } else { $path }
    if ($rest.Contains(':')) { throw "$what contains a colon: $path" }
}

Test-NoColon $Name 'Name'
if ($Name -match '[\\/*?"<>|]') { throw "Name must be a plain folder name: $Name" }
if ($IdentityFile) { Test-NoColon $IdentityFile 'IdentityFile' }
Test-NoColon $OutRoot 'OutRoot'
Test-NoColon $DataDir 'DataDir'

$exe = Join-Path $Bin 'dream.exe'
if (-not (Test-Path $exe)) {
    throw "No dream.exe at $exe. Build first (see SKILL.md): dotnet build dream\dream.csproj -c Release `"/p:BaseOutputPath=$repo\artifacts\capture-bin\`""
}

$vars = [ordered]@{
    DREAM_AUDIO               = '0'
    DREAM_AUTO_CONNECT        = '1'
    DREAM_DATA_DIR            = $DataDir
    DREAM_SIMULATION_SEED     = [string]$Seed
    DREAM_TIME_OF_DAY         = $TimeOfDay
    DREAM_HUD_CAPTURE         = ''
    DREAM_HUD_CAPTURE_SCRIPT  = $Script
}
# The default identity lives in the data dir; an explicit file shares one identity across runs.
if ($IdentityFile) { $vars['DREAM_IDENTITY_FILE'] = $IdentityFile }
if ($FromSource) { $vars['DREAM_ASSET_ROOT'] = Join-Path $repo 'dream' }
# "-File" passes "A=1,B=2" as one string, so split on commas here (values cannot contain commas).
foreach ($pair in @($Env | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {
    $i = $pair.IndexOf('=')
    if ($i -lt 1) { throw "-Env entries are KEY=VALUE, got '$pair'" }
    $vars[$pair.Substring(0, $i)] = $pair.Substring($i + 1)
}
if ($vars.Contains('DREAM_SERVER_HOST')) { throw 'DREAM_SERVER_HOST would connect to a remote server; captures use the integrated local server' }

$cap = Join-Path $OutRoot $Name
New-Item -ItemType Directory -Force $cap | Out-Null
$vars['DREAM_HUD_CAPTURE'] = $cap
$outLog = Join-Path $cap 'out.log'
$errLog = Join-Path $cap 'err.log'

# A fresh world per run unless -KeepData. The wipe is recursive, so it only touches a folder this script owns: one
# under $OutRoot, or one holding the marker file this script wrote when it created it. Anything else is refused.
function Test-SamePath([string]$a, [string]$b) { return ($a.TrimEnd('\', '/') -ieq $b.TrimEnd('\', '/')) }
function Test-UnderPath([string]$child, [string]$parent) {
    $p = $parent.TrimEnd('\', '/') + '\'
    return $child.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)
}
function Get-WipeRefusal([string]$dir) {
    $protected = @($repo, $Bin, $OutRoot, $HOME, [Environment]::GetFolderPath('UserProfile'), $env:SystemRoot, $env:ProgramFiles) | Where-Object { $_ }
    if (Test-SamePath $dir ([System.IO.Path]::GetPathRoot($dir))) { return 'it is a drive root' }
    foreach ($q in $protected) {
        if (Test-SamePath $dir $q) { return "it is $q" }
        if (Test-UnderPath $q $dir) { return "it contains $q" }
    }
    if (Test-UnderPath $dir $Bin) { return 'it is inside the build output folder' }
    if (Test-UnderPath $dir $OutRoot) { return $null }
    if (Test-Path -LiteralPath (Join-Path $dir $DataMarker) -PathType Leaf) { return $null }
    return "it is neither under OutRoot ($OutRoot) nor a folder this script created earlier (no $DataMarker marker)"
}
$dataExisted = Test-Path -LiteralPath $DataDir
if (-not $KeepData -and $dataExisted) {
    $why = Get-WipeRefusal $DataDir
    if ($why) { throw "Refusing to delete DataDir $DataDir : $why. Pass -KeepData to reuse it, or pick a folder under OutRoot." }
    Remove-Item -LiteralPath $DataDir -Recurse -Force
    $dataExisted = $false
}
New-Item -ItemType Directory -Force $DataDir | Out-Null
if (-not $dataExisted) { Set-Content -LiteralPath (Join-Path $DataDir $DataMarker) -Value 'created by capture.ps1; safe to wipe' -Encoding ascii }

# With -Serial: strictly one dream.exe at a time, wait for every other one to finish. Never stop a process this script did not start.
$deadline = (Get-Date).AddSeconds($WaitForIdleSeconds)
$announced = $false
while ($Serial -and (Get-Process dream -ErrorAction SilentlyContinue)) {
    if (-not $announced) {
        $ids = (Get-Process dream | ForEach-Object { $_.Id }) -join ', '
        Write-Host "waiting: dream.exe already running (pid $ids); not touching it"
        $announced = $true
    }
    if ((Get-Date) -gt $deadline) { Write-Host "ABORT: dream.exe still running after $WaitForIdleSeconds s; launched nothing"; exit 2 }
    Start-Sleep -Seconds 3
}

$saved = @{}
foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process') }
$timedOut = $false
try {
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, [string]$vars[$k], 'Process') }
    Write-Host ("launch: {0}" -f $exe)
    foreach ($k in $vars.Keys) { Write-Host ("  {0}={1}" -f $k, $vars[$k]) }
    $p = Start-Process -FilePath $exe -WorkingDirectory $Bin -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru
    $null = $p.Handle   # cache the handle now, or ExitCode reads empty after the process exits
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue   # only the process started above
        $p.WaitForExit(10000) | Out-Null
    }
}
finally {
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
}

$pngs = @(Get-ChildItem $cap -Filter *.png -ErrorAction SilentlyContinue | Sort-Object Name)
Write-Host ''
Write-Host ("capture dir: {0}" -f $cap)
if ($timedOut) { Write-Host ("TIMEOUT after {0} s: stopped pid {1}" -f $TimeoutSeconds, $p.Id) } else { Write-Host ("exit code: {0}" -f $p.ExitCode) }
Write-Host ("pngs ({0}):" -f $pngs.Count)
$pngs | ForEach-Object { Write-Host ("  {0}" -f $_.Name) }

Write-Host ''
Write-Host '--- stderr tail (server startup errors land here) ---'
if (Test-Path $errLog) { Get-Content $errLog -Tail 20 | ForEach-Object { Write-Host $_ } }
Write-Host '--- stdout: session, startup and fault lines (join rejections land here) ---'
if (Test-Path $outLog) {
    Select-String -Path $outLog -Pattern '\[session\]|\[startup\]|Join rejected|render-fault|wrote ' |
        Select-Object -Last 25 | ForEach-Object { Write-Host $_.Line }
}

$devLines = @()
if (Test-Path $outLog) { $devLines = @(Select-String -Path $outLog -Pattern '^\[hud-capture\] dev ' | ForEach-Object { $_.Line }) }
Write-Host '--- dev steps ---'
$devLines | ForEach-Object { Write-Host $_ }
$devFailed = @($devLines | Where-Object { $_ -match '^\[hud-capture\] dev .* -> (\[dev\] error:|no reply)' })

if ($timedOut -or $pngs.Count -eq 0) { exit 1 }
if ($devFailed.Count -gt 0 -and -not $AllowDevErrors) {
    Write-Host ("FAIL: {0} dev step line(s) errored or got no reply (use -AllowDevErrors to ignore)" -f $devFailed.Count)
    exit 3
}
exit 0
