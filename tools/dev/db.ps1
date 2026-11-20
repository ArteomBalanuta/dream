<#
Local world database helper (SQLite, player-profiles.db) for the justfile.

  tools\dev\db.ps1 -Action path    [-Configuration Debug]
  tools\dev\db.ps1 -Action backup  [-Configuration Debug]            online-safe copy via dream --db backup
  tools\dev\db.ps1 -Action restore -Source <file> [-Configuration Debug]   dream --db restore <file> --yes
  tools\dev\db.ps1 -Action reset   [-Configuration Debug] [-Yes] [-DryRun] [-AllowOutsideRepo]

Which database: DREAM_DATA_DIR when it is set (the same rule dream uses), otherwise
dream\bin\<Configuration>\net10.0\config\player-profiles.db.

reset is the only destructive action and is reversible by construction:
  - refuses (exit 7, dream's own "world held" code) while any world lease or the maintenance lock is held,
    i.e. while a game, server or --worlds/--db command has the database open;
  - refuses a database outside the repository unless -AllowOutsideRepo (a DREAM_DATA_DIR may point at a
    real server's data);
  - always copies the database and its -wal/-shm into artifacts\db-backups\<stamp>-reset\ and checks the
    copy's hash before deleting anything;
  - asks for a typed 'reset' unless -Yes.
The next start of the game or server creates a fresh database from config\player-profiles.init.sql.
#>
param(
    [Parameter(Mandatory)][ValidateSet('path', 'backup', 'restore', 'reset')][string]$Action,
    [string]$Configuration = 'Debug',
    [string]$Source,
    [switch]$Yes,
    [switch]$DryRun,
    [switch]$AllowOutsideRepo
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\guard.ps1"
$repo = Get-RepoRoot
$binDir = Join-Path $repo "dream\bin\$Configuration\net10.0"
$exe = Join-Path $binDir 'dream.exe'
$backupRoot = Join-Path $repo 'artifacts\db-backups'

if ($env:DREAM_DATA_DIR) {
    $dataDir = [System.IO.Path]::GetFullPath($env:DREAM_DATA_DIR)
    $origin = 'DREAM_DATA_DIR'
} else {
    $dataDir = Join-Path $binDir 'config'
    $origin = "default for $Configuration"
}
$db = Join-Path $dataDir 'player-profiles.db'

function Get-DbFamily {
    @($db, "$db-wal", "$db-shm") | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
}

function Get-LockFiles {
    Get-ChildItem -LiteralPath $dataDir -Filter 'player-profiles.db.*.lock' -File -ErrorAction SilentlyContinue
}

function Require-Exe {
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Host "no $exe yet: run 'just build' (or set DREAM_CONFIG) first" -ForegroundColor Red
        exit 1
    }
}

switch ($Action) {
    'path' {
        Write-Host "database : $db ($origin)"
        if (Test-Path -LiteralPath $db) {
            Write-Host ("size     : {0}, modified {1:yyyy-MM-dd HH:mm}" -f (Format-Bytes (Get-Item $db).Length), (Get-Item $db).LastWriteTime)
        } else {
            Write-Host 'size     : (does not exist; the next start creates it)'
        }
        foreach ($lock in Get-LockFiles) {
            $state = if (Test-FileLockHeld -Path $lock.FullName) { 'HELD (a game, server or command has it open)' } else { 'free' }
            Write-Host ("lock     : {0} {1}" -f $lock.Name, $state)
        }
        Write-Host "backups  : $backupRoot"
        exit 0
    }

    'backup' {
        Require-Exe
        New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
        $dest = Join-Path $backupRoot ("{0:yyyyMMdd-HHmmss}-{1}.db" -f (Get-Date), $Configuration)
        # SqliteConnection.BackupDatabase + integrity_check; safe while a server runs.
        & $exe --db backup $dest
        $code = $LASTEXITCODE
        if ($code -eq 0) { Write-Host "backup: $dest" }
        exit $code
    }

    'restore' {
        Require-Exe
        if (-not $Source -or -not (Test-Path -LiteralPath $Source -PathType Leaf)) {
            Write-Host "restore needs an existing -Source file (see $backupRoot)" -ForegroundColor Red
            exit 1
        }
        # dream does the guarding: maintenance lock + every lease (exit 7), schema/integrity checks (3/6/2),
        # and keeps the replaced file as {db}.pre-restore.<stamp>.bak.
        & $exe --db restore ([System.IO.Path]::GetFullPath($Source)) --yes
        exit $LASTEXITCODE
    }

    'reset' {
        $family = @(Get-DbFamily)
        if ($family.Count -eq 0) {
            Write-Host "nothing to reset: $db does not exist"
            exit 0
        }
        if (-not (Test-InsideRepo -Path $db) -and -not $AllowOutsideRepo) {
            Write-Host "refusing: $db is outside the repository (from $origin). Pass -AllowOutsideRepo if you mean it." -ForegroundColor Red
            exit 1
        }

        $held = @(Get-LockFiles | Where-Object { Test-FileLockHeld -Path $_.FullName })
        if ($held.Count -gt 0) {
            Write-Host ("refusing: held by a running game/server/command: {0}" -f (($held | ForEach-Object Name) -join ', ')) -ForegroundColor Red
            Write-Host 'Close it yourself first; this script never stops a dream process.' -ForegroundColor Red
            exit 7
        }
        $users = @(Get-ProcessesUsingPath -Path $dataDir | Where-Object { $_.Name -eq 'dream.exe' })
        if ($users.Count -gt 0) {
            Write-Host ("refusing: dream.exe pid {0} uses {1}" -f (($users | ForEach-Object ProcessId) -join ', '), $dataDir) -ForegroundColor Red
            exit 7
        }

        $stampDir = Join-Path $backupRoot ("{0:yyyyMMdd-HHmmss}-{1}-reset" -f (Get-Date), $Configuration)
        Write-Host "database : $db ($origin)"
        Write-Host ("delete   : {0}" -f (($family + @(Get-LockFiles | ForEach-Object FullName)) -join ', '))
        Write-Host "backup to: $stampDir"
        if ($DryRun) { Write-Host 'dry run: nothing changed'; exit 0 }
        if (-not $Yes -and -not (Confirm-Typed -Prompt 'This deletes every world, character and item in that database.' -Expected 'reset')) {
            Write-Host 'cancelled'
            exit 1
        }

        New-Item -ItemType Directory -Force -Path $stampDir | Out-Null
        foreach ($file in $family) {
            $copy = Join-Path $stampDir (Split-Path $file -Leaf)
            Copy-Item -LiteralPath $file -Destination $copy
            if ((Get-FileHash -LiteralPath $file).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) {
                Write-Host "refusing: the backup of $file does not match; nothing deleted" -ForegroundColor Red
                exit 1
            }
        }
        foreach ($file in $family) { Remove-Item -LiteralPath $file }
        foreach ($lock in Get-LockFiles) { Remove-Item -LiteralPath $lock.FullName -ErrorAction SilentlyContinue }

        Write-Host "reset: the next start creates a fresh database. Undo with:"
        Write-Host "  just db-restore `"$(Join-Path $stampDir 'player-profiles.db')`""
        exit 0
    }
}
