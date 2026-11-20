<#
.SYNOPSIS
  Build a self-contained, shareable client bundle: artifacts\dist\dream-<version>-<rid>.zip (win-x64) or .tar.gz (linux-x64).

.EXAMPLE
  tools\dev\package.ps1 -Rid win-x64
  tools\dev\package.ps1 -Rid all -Version 1.2.3
  tools\dev\package.ps1 -Rid linux-x64 -NoRestore     # after a shared restore (-Rid all -RestoreOnly)

Runs in Windows PowerShell 5.1 and pwsh 7. The bundle is built from a clean export of HEAD (git archive) in
artifacts\dist-src\<sha>, so git-ignored and untracked files (local OBJ/zip packs, world databases) can never ship, and
the publish gets its own obj\ and bin\: the checkout's dream\bin is never touched. Output: artifacts\dist (stage +
archive). Uncommitted changes to tracked files are NOT in the bundle (a warning says so). Exit 0 = archive written and
verified, 1 = failed.
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
$dist = Join-Path (Join-Path $root 'artifacts') 'dist'
$distSrc = Join-Path (Join-Path $root 'artifacts') 'dist-src'
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
    # After the explicit sources: the Visual Studio redist copy, then the system one that vc_redist installs.
    $vswhere = Join-Path (Join-Path (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio') 'Installer') 'vswhere.exe'
    $installs = if (Test-Path $vswhere) { @(& $vswhere -products '*' -property installationPath) } else { @() }
    foreach ($inst in $installs) {
        $msvc = Join-Path (Join-Path (Join-Path $inst 'VC') 'Redist') 'MSVC'
        if (-not (Test-Path $msvc)) { continue }
        $hits = @(Get-ChildItem -Path $msvc -Directory | Sort-Object Name -Descending | ForEach-Object {
                Get-ChildItem -Path (Join-Path $_.FullName 'x64') -Directory -Filter 'Microsoft.VC14*.OpenMP' -ErrorAction SilentlyContinue |
                    ForEach-Object { Join-Path $_.FullName 'vcomp140.dll' } | Where-Object { Test-Path $_ }
            })
        if ($hits.Count -gt 0) { return $hits[0] }
    }
    $system = Join-Path (Join-Path $env:SystemRoot 'System32') 'vcomp140.dll'
    if (Test-Path $system -PathType Leaf) { return $system }
    Fail "vcomp140.dll not found (pass -Vcomp140 <path> or set DREAM_VCOMP140); searched VC\Redist\MSVC\<ver>\x64\Microsoft.VC14*.OpenMP under every Visual Studio install and $system (libbulletc.dll imports VCOMP140.DLL; install the Visual C++ redistributable)"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $sha = (git rev-parse --short=7 HEAD).Trim()
    $Version = (Get-Date).ToString('yyyyMMdd') + '-' + $sha
}
$py = Resolve-Python
$rids = if ($Rid -eq 'all') { @('win-x64', 'linux-x64') } else { @($Rid) }

# Resolve vcomp140 before anything slow runs: a missing redistributable must not cost an export and a publish first.
$vcomp = if (($rids -contains 'win-x64') -and -not $RestoreOnly) { Find-Vcomp140 } else { $null }

# The bundle is HEAD, not the working tree.
$modified = @(git status --porcelain --untracked-files=no)
if ($modified.Count -gt 0) {
    Write-Host ($modified -join "`n")
    Write-Host 'package: WARNING tracked files have uncommitted changes; the bundle is built from HEAD and does not include them' -ForegroundColor Yellow
}

# One clean export of HEAD per run, shared by both RIDs (parallel publishes only read project.assets.json from it).
# -NoRestore reuses the export the restore step made; anything else re-exports.
$fullSha = (git rev-parse HEAD).Trim()
$src = Join-Path $distSrc $fullSha
$marker = Join-Path $src '.export-complete'
if ($NoRestore) {
    if (-not (Test-Path $marker)) { Fail "no export of HEAD at ${src}: run without -NoRestore (or -Rid all -RestoreOnly) first" }
}
else {
    if (-not $src.StartsWith($distSrc + [IO.Path]::DirectorySeparatorChar)) { Fail "export dir $src is outside $distSrc" }
    if (Test-Path $src) { Remove-Item -Recurse -Force $src }
    New-Item -ItemType Directory -Force -Path $src | Out-Null
    $tarFile = Join-Path $distSrc "$fullSha.tar"
    git archive --format=tar -o $tarFile HEAD
    if ($LASTEXITCODE -ne 0) { Fail "git archive failed ($LASTEXITCODE)" }
    tar -xf $tarFile -C $src
    if ($LASTEXITCODE -ne 0) { Fail "extracting the HEAD export failed ($LASTEXITCODE)" }
    Remove-Item -Force $tarFile

    # Verify the export is complete: same file count and total size as the tree HEAD records (no Git LFS here, so
    # archive content is real content; a mismatch would mean pointer files or a truncated extract).
    $tree = @(git ls-tree -r -l HEAD)
    $want = [long]0
    foreach ($line in $tree) { $want += [long](($line -split '\s+', 5)[3]) }
    $got = @(Get-ChildItem -Path $src -Recurse -File -Force)
    $gotBytes = [long](($got | Measure-Object -Property Length -Sum).Sum)
    if ($got.Count -ne $tree.Count) { Fail "export has $($got.Count) files, HEAD has $($tree.Count)" }
    if ($gotBytes -ne $want) { Fail "export is $gotBytes bytes, HEAD records $want" }
    Write-Host ("package: exported HEAD $($fullSha.Substring(0, 7)): $($got.Count) files, {0:N1} MB -> $src" -f ($gotBytes / 1MB))
    Set-Content -Path $marker -Value $fullSha
}
$csproj = Join-Path (Join-Path $src 'dream') 'dream.csproj'

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
        -o $stage
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish $r failed ($LASTEXITCODE)" }

    if ($r -eq 'win-x64') { Copy-Item $vcomp (Join-Path $stage 'vcomp140.dll') -Force }
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
