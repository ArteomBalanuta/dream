<#
.SYNOPSIS
  Build a self-contained, shareable client bundle: artifacts\dist\dream-<version>-<rid>.zip (win-x64) or .tar.gz (linux-x64).

.EXAMPLE
  tools\dev\package.ps1 -Rid win-x64
  tools\dev\package.ps1 -Rid all -Version 1.2.3
  tools\dev\package.ps1 -Rid linux-x64 -NoRestore     # after a shared restore (-Rid all -RestoreOnly)

Runs in Windows PowerShell 5.1 and pwsh 7. Never touches dream\bin: output goes to artifacts\dist-bin (obj) and
artifacts\dist (stage + archive). Exit 0 = archive written and verified, 1 = failed, 3 = refused (dirty dream\config).
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'linux-x64', 'all')][string]$Rid,
    [string]$Version = '',
    [switch]$NoRestore,
    [switch]$RestoreOnly,
    [string]$Vcomp140 = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
Set-Location $root
$csproj = Join-Path (Join-Path $root 'dream') 'dream.csproj'
$dist = Join-Path (Join-Path $root 'artifacts') 'dist'
$distBin = ($root -replace '\\', '/') + '/artifacts/dist-bin/'
$bundleDir = Join-Path $PSScriptRoot 'bundle'

$isWin = [IO.Path]::DirectorySeparatorChar -eq '\'

function Fail([string]$message, [int]$code = 1) {
    Write-Host "package: $message" -ForegroundColor Red
    exit $code
}

function Resolve-Python {
    if ($isWin) {
        $venv = Join-Path (Join-Path (Join-Path $root '.venv') 'Scripts') 'python.exe'
        if (Test-Path $venv) { return $venv }
        return 'python'
    }
    $venv = Join-Path (Join-Path (Join-Path $root '.venv') 'bin') 'python'
    if (Test-Path $venv) { return $venv }
    return 'python3'
}

function Find-Vcomp140 {
    $given = if ($Vcomp140) { $Vcomp140 } else { $env:DREAM_VCOMP140 }
    if ($given) {
        if (-not (Test-Path $given -PathType Leaf)) { Fail "vcomp140.dll not found at '$given' (-Vcomp140 / DREAM_VCOMP140)" }
        return (Resolve-Path $given).Path
    }
    if (-not $isWin) { Fail 'win-x64 bundles need vcomp140.dll (libbulletc.dll imports it) and this host cannot find it: pass -Vcomp140 <path> or set DREAM_VCOMP140' }
    $vswhere = Join-Path (Join-Path (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio') 'Installer') 'vswhere.exe'
    if (-not (Test-Path $vswhere)) { Fail "vswhere.exe not found ($vswhere): install the Visual Studio C++ build tools to find vcomp140.dll" }
    $installs = @(& $vswhere -products '*' -property installationPath)
    foreach ($inst in $installs) {
        $msvc = Join-Path (Join-Path (Join-Path $inst 'VC') 'Redist') 'MSVC'
        if (-not (Test-Path $msvc)) { continue }
        $hits = @(Get-ChildItem -Path $msvc -Directory | Sort-Object Name -Descending | ForEach-Object {
                Get-ChildItem -Path (Join-Path $_.FullName 'x64') -Directory -Filter 'Microsoft.VC14*.OpenMP' -ErrorAction SilentlyContinue |
                    ForEach-Object { Join-Path $_.FullName 'vcomp140.dll' } | Where-Object { Test-Path $_ }
            })
        if ($hits.Count -gt 0) { return $hits[0] }
    }
    Fail 'vcomp140.dll not found (pass -Vcomp140 <path> or set DREAM_VCOMP140); searched under VC\Redist\MSVC\<ver>\x64\Microsoft.VC14*.OpenMP (libbulletc.dll imports VCOMP140.DLL; install the VS C++ redistributable component)'
}

$dirty = @(git status --porcelain -- dream/config)
if ($dirty.Count -gt 0) {
    Write-Host ($dirty -join "`n")
    Fail 'dream/config has uncommitted changes (it can hold a world database); commit or remove them first' 3
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $sha = (git rev-parse --short=7 HEAD).Trim()
    $Version = (Get-Date).ToString('yyyyMMdd') + '-' + $sha
}
$py = Resolve-Python
$rids = if ($Rid -eq 'all') { @('win-x64', 'linux-x64') } else { @($Rid) }

if (-not $NoRestore) {
    # One restore for both RIDs: a per-RID restore would rewrite obj\project.assets.json and race when run in parallel.
    # RuntimeIdentifiers goes in through the environment because a ';' list cannot be quoted the same way in 5.1 and 7.
    $env:RuntimeIdentifiers = 'win-x64;linux-x64'
    try {
        dotnet restore $csproj -p:SelfContained=true
        if ($LASTEXITCODE -ne 0) { Fail "dotnet restore failed ($LASTEXITCODE)" }
    }
    finally { Remove-Item Env:\RuntimeIdentifiers -ErrorAction SilentlyContinue }
}

if ($RestoreOnly) { exit 0 }

foreach ($r in $rids) {
    $name = "dream-$Version-$r"
    $stage = Join-Path (Join-Path $dist $r) $name
    $ext = if ($r -eq 'win-x64') { 'zip' } else { 'tar.gz' }
    $archive = Join-Path $dist "$name.$ext"

    Write-Host "package: $r -> $stage"
    if (-not $stage.StartsWith($dist + [IO.Path]::DirectorySeparatorChar)) { Fail "stage $stage is outside $dist" }
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    if (Test-Path $archive) { Remove-Item -Force $archive }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    dotnet publish $csproj -c Release -r $r --self-contained true --no-restore `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false `
        "-p:BaseOutputPath=$distBin" -o $stage
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish $r failed ($LASTEXITCODE)" }

    if ($r -eq 'win-x64') { Copy-Item (Find-Vcomp140) (Join-Path $stage 'vcomp140.dll') -Force }
    Copy-Item (Join-Path $bundleDir 'README.txt') (Join-Path $stage 'README.txt') -Force
    if ($r -eq 'linux-x64') { Copy-Item (Join-Path $bundleDir 'run-dream.sh') (Join-Path $stage 'run-dream.sh') -Force }

    & $py -I (Join-Path $PSScriptRoot 'archive_bundle.py') verify --rid $r $stage
    if ($LASTEXITCODE -ne 0) { Fail "bundle verify failed for $r (stage kept at $stage)" }
    & $py -I (Join-Path $PSScriptRoot 'archive_bundle.py') pack --rid $r --stage $stage --out $archive
    if ($LASTEXITCODE -ne 0) { Fail "pack failed for $r" }
    & $py -I (Join-Path $PSScriptRoot 'archive_bundle.py') verify --rid $r $archive
    if ($LASTEXITCODE -ne 0) { Fail "archive verify failed for $r" }

    $mb = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host "package: $archive ($mb MB)" -ForegroundColor Green
}
exit 0
