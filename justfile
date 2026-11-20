# Dream developer workflows. `just` lists them, `just --show <recipe>` prints one.
#
# Needs just >= 1.42 (the [parallel] attribute):  winget install --id Casey.Just -e
# Recipes run in Windows PowerShell from the repo root. The logic lives in tools\dev\*.ps1 and the
# documented repo tools (test.ps1, capture.ps1, golden\run.ps1); this file only names them.
#
# Configuration: DREAM_CONFIG=Release just build   (default Debug)
#
# House rules this file enforces (see AGENTS.md, "Rules Learned The Hard Way"):
#   - one dotnet test suite at a time: no recipe runs two, and test.ps1 refuses beside a running testhost;
#   - never kill the user's dream.exe: build/rebuild refuse while it runs from dream\bin (use build-verify);
#   - destructive recipes print their plan, back up what is not regenerable, and ask; none kills a process.

set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command"]
set shell := ["pwsh", "-NoLogo", "-NoProfile", "-Command"]

config := env("DREAM_CONFIG", "Debug")
root := justfile_directory()
ps := "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File"
pkg := if os() == "windows" { ps + " tools\\dev\\package.ps1" } else { "pwsh -NoLogo -NoProfile -File tools/dev/package.ps1" }
rid_here := if os() == "windows" { "win-x64" } else { "linux-x64" }
py := if path_exists(root / ".venv/Scripts/python.exe") == "true" { root / ".venv/Scripts/python.exe" } else { "python" }
game := "dream\\dream.csproj"
tests := "dream.Tests\\dream.Tests.csproj"
exe := root / "dream/bin" / config / "net10.0/dream.exe"
db_file := if env("DREAM_DATA_DIR", "") != "" { env("DREAM_DATA_DIR") / "player-profiles.db" } else { root / "dream/bin" / config / "net10.0/config/player-profiles.db" }

# List the recipes
default:
    @just --list --unsorted

# ---------------------------------------------------------------------------------------------- setup

# Read-only check: SDK vs global.json, just, Python venv, running dream/testhost, free disk
[group('setup')]
doctor:
    {{ps}} tools\dev\doctor.ps1; exit $LASTEXITCODE

# Restore NuGet packages for the game and the tests
[group('setup')]
restore:
    dotnet restore {{game}}
    dotnet restore {{tests}}

# Create .venv with the generator/gate dependencies (numpy, pillow)
[group('setup')]
venv:
    if (-not (Test-Path .venv)) { python -m venv .venv }
    .venv\Scripts\python.exe -m pip install -r tools\ui-assets\requirements.txt

# ---------------------------------------------------------------------------------------------- build

# Build the game into dream\bin\<config> (refuses while that dream.exe runs)
[group('build')]
build: _bin-unlocked
    dotnet build {{game}} -c {{config}}

# Build the game and the test project (dream.sln does NOT include the tests)
[group('build')]
build-tests: _bin-unlocked
    dotnet build {{tests}} -c {{config}}

# Full non-incremental rebuild of game and tests (refuses while that dream.exe runs)
[group('build')]
rebuild: _bin-unlocked
    dotnet build {{game}} -c {{config}} --no-incremental
    dotnet build {{tests}} -c {{config}} --no-incremental

# Build game + tests into artifacts\verify-bin: safe while you play
[group('build')]
build-verify:
    dotnet build {{tests}} -c {{config}} /p:BaseOutputPath={{root}}/artifacts/verify-bin/

# Release build into artifacts\capture-bin, the bin capture.ps1 and golden\run.ps1 use
[group('build')]
build-capture:
    dotnet build {{game}} -c Release /p:BaseOutputPath={{root}}/artifacts/capture-bin/

# ---------------------------------------------------------------------------------------------- package

# Self-contained win-x64 client -> artifacts\dist\dream-<version>-win-x64.zip (no SDK or runtime needed to run it)
[group('package')]
package-windows version="":
    {{pkg}} -Rid win-x64 {{ if version != "" { "-Version " + version } else { "" } }}; exit $LASTEXITCODE

# Self-contained linux-x64 client -> artifacts\dist\dream-<version>-linux-x64.tar.gz (built on Windows too)
[group('package')]
package-linux version="":
    {{pkg}} -Rid linux-x64 {{ if version != "" { "-Version " + version } else { "" } }}; exit $LASTEXITCODE

# Both bundles, built in parallel after one shared restore
[group('package')]
package-all version="": _dist-restore
    & '{{just_executable()}}' _package-pair {{version}}; exit $LASTEXITCODE

# Run the newest packaged client from artifacts\dist (win-x64 stage; on Linux the linux-x64 run-dream.sh)
[group('package')]
run-dist *args:
    $s = Get-ChildItem '{{root}}/artifacts/dist/{{rid_here}}' -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1; if (-not $s) { Write-Host 'no packaged client for {{rid_here}}: run just package-{{ if os() == "windows" { "windows" } else { "linux" } }} first'; exit 1 }; Set-Location $s.FullName; {{ if os() == "windows" { "& './dream.exe'" } else { "& sh './run-dream.sh'" } }} {{args}}; exit $LASTEXITCODE

# ------------------------------------------------------------------------------------------------ run

# Run the desktop client on your normal world (extra args go to dream)
[group('run')]
run *args: _bin-unlocked
    dotnet run --project {{game}} -c {{config}} -- {{args}}

# Run the client on a brand-new throwaway world in artifacts\dev-data\<stamp> (your real world is untouched)
[group('run')]
run-fresh *args: _bin-unlocked
    $env:DREAM_DATA_DIR = '{{root}}/artifacts/dev-data/{{datetime("%Y%m%d-%H%M%S")}}'; dotnet run --project {{game}} -c {{config}} -- {{args}}

# Run the client on a named sandbox world kept in artifacts\dev-data\<name>
[group('run')]
run-sandbox name="sandbox" *args: _bin-unlocked
    $env:DREAM_DATA_DIR = '{{root}}/artifacts/dev-data/{{name}}'; dotnet run --project {{game}} -c {{config}} -- {{args}}

# Run with assets from the source tree: edited JSON, atlases and shaders apply without a rebuild
[group('run')]
run-src *args: _bin-unlocked
    $env:DREAM_ASSET_ROOT = '{{root}}/dream'; dotnet run --project {{game}} -c {{config}} -- {{args}}

# Run the dedicated server (port 5057; DREAM_PORT / DREAM_WORLD apply; extra args go to dream)
[group('run')]
server *args: _bin-unlocked
    dotnet run --project {{game}} -c {{config}} -- --server {{args}}

# Server + desktop clients perf harness; never stops a running dream.exe (extra args to launch-and-summarize.ps1)
[group('run')]
perf *args:
    {{ps}} skills\profiling-dream-multiplayer\scripts\launch-and-summarize.ps1 -NoStopExisting {{args}}; exit $LASTEXITCODE

# --------------------------------------------------------------------------------------------- verify

# Validate the SOURCE content JSON with the built exe, < 1 s, no rebuild (exit 0 ok, 8 invalid; try --strict)
[group('verify')]
validate *args: _exe
    $env:DREAM_ASSET_ROOT = '{{root}}/dream'; & '{{exe}}' --validate-content {{args}}; exit $LASTEXITCODE

# One test tier with watchdog + log: Content|Rendering|Simulation|Net|Client|Fast|Full, optional filter
[group('verify')]
test area="Fast" filter="":
    {{ps}} tools\dev\test.ps1 -Area {{area}} {{ if filter != "" { "-Filter '" + filter + "'" } else { "" } }}; exit $LASTEXITCODE

# Content tier (~20 s warm)
[group('verify')]
test-content: (test "Content")

# The whole suite (~3.5-4.5 min)
[group('verify')]
test-full: (test "Full")

# Full suite with coverage over dream.Tests itself (dead-assertion hunt), into artifacts\coverage
[group('verify')]
test-coverage:
    dotnet test {{tests}} -p:Coverage=true --settings dream.Tests\coverage.runsettings --collect:'XPlat Code Coverage' --results-directory artifacts\coverage

# Python gates in parallel: quick (golden+gallery+audio, ~2 min) | all (adds every ui-assets check, slow) | golden|gallery|audio|ui
[group('verify')]
py-gates group="quick":
    {{ps}} tools\dev\pygates.ps1 -Group {{group}} -Python '{{py}}'; exit $LASTEXITCODE

# Pre-handoff check: build + the quick Python gates in parallel, then the Fast test tier
[group('verify')]
[parallel]
check: build-tests (py-gates "quick")
    {{ps}} tools\dev\test.ps1 -Area Fast; exit $LASTEXITCODE

# --------------------------------------------------------------------------------------------- visual

# Screenshot run: just capture <name> '<script>' [-FromSource] [-TimeOfDay noon] ... (needs build-capture)
[group('visual')]
capture name script *args:
    {{ps}} skills\capturing-dream-screenshots\scripts\capture.ps1 -Name {{name}} -Script '{{script}}' {{args}}; exit $LASTEXITCODE

# Golden-image compare: one scene name or all (exit 0 only when every scene passes)
[group('visual')]
golden scene="all" *args:
    {{ps}} tools\golden\run.ps1 -Scene {{scene}} {{args}}; exit $LASTEXITCODE

# Re-record golden baselines (overwrites committed tools\golden\baselines), then re-run the gate's negatives
[group('visual')]
[confirm("Re-record golden baselines? They are committed files; review the diff before committing.")]
golden-record scene *args:
    {{ps}} tools\golden\run.ps1 -Scene {{scene}} -Record -Parallel {{args}}; exit $LASTEXITCODE
    & '{{py}}' -I tools\golden\tests\test_golden.py

# Gallery of generated art and recent captures -> artifacts\gallery\index.html
[group('visual')]
gallery:
    & '{{py}}' -I tools\dev\gallery.py

# --------------------------------------------------------------------------------------------- assets

# Regenerate the UI atlases and forged 3D assets under dream\ (tracked files: review git diff)
[group('assets')]
gen-ui:
    & '{{py}}' tools\ui-assets\build_all.py

# Regenerate and check every sound under dream\audio (tracked files: review git diff)
[group('assets')]
gen-audio:
    & '{{py}}' -I tools\audio\build_all.py

# Both generator families at once (they write disjoint folders)
[group('assets')]
[parallel]
gen-all: gen-ui gen-audio

# ------------------------------------------------------------------------------------------- database

# Where the world database is (DREAM_DATA_DIR or the exe folder), its size and lock state
[group('db')]
db-path:
    {{ps}} tools\dev\db.ps1 -Action path -Configuration {{config}}; exit $LASTEXITCODE

# Online-safe backup (dream --db backup) into artifacts\db-backups
[group('db')]
db-backup: _exe
    {{ps}} tools\dev\db.ps1 -Action backup -Configuration {{config}}; exit $LASTEXITCODE

# Restore a backup (dream --db restore: refuses while held, keeps the replaced file as .pre-restore.*.bak)
[group('db')]
[confirm("Replace the live world database with that file? (the current one is kept as .pre-restore.<stamp>.bak)")]
db-restore file: _exe
    {{ps}} tools\dev\db.ps1 -Action restore -Configuration {{config}} -Source '{{file}}'; exit $LASTEXITCODE

# Wipe the world database: refuses while held, always backs up first, asks for a typed 'reset' (flags: -DryRun, -Yes)
[group('db')]
db-reset *flags:
    {{ps}} tools\dev\db.ps1 -Action reset -Configuration {{config}} {{flags}}; exit $LASTEXITCODE

# List worlds in the database
[group('db')]
db-worlds: _exe
    & '{{exe}}' --worlds list; exit $LASTEXITCODE

# List characters in the database
[group('db')]
db-characters: _exe
    & '{{exe}}' --characters list; exit $LASTEXITCODE

# Read-only SQL against the world database, rows as JSON: just db-sql 'SELECT player_name FROM player_profiles'
[group('db')]
db-sql sql:
    & '{{py}}' skills\updating-dream-persistence-state\scripts\dream_persistence_sqlite.py --db '{{db_file}}' query --sql '{{sql}}'

# ---------------------------------------------------------------------------------------------- clean

# dotnet clean for game and tests (removes only what the build wrote; keeps your world)
[group('clean')]
clean:
    dotnet clean {{game}} -c {{config}}
    dotnet clean {{tests}} -c {{config}}

# Show what clean-build + clean-artifacts would delete, with sizes; deletes nothing
[group('clean')]
clean-plan:
    {{ps}} tools\dev\clean.ps1 -Target all -DryRun; exit $LASTEXITCODE

# Delete bin/obj of game and tests, keeping the world DB and desktop-client.txt (flags: -DryRun, -Yes)
[group('clean')]
clean-build *flags:
    {{ps}} tools\dev\clean.ps1 -Target build {{flags}}; exit $LASTEXITCODE

# Delete only the side build outputs in artifacts\ (verify-bin, capture-bin, ...); keeps captures and logs (flags: -DryRun, -Yes)
[group('clean')]
clean-bins *flags:
    {{ps}} tools\dev\clean.ps1 -Target bins {{flags}}; exit $LASTEXITCODE

# Delete artifacts\ except db-backups and test-durations.csv (flags: -DryRun, -Yes)
[group('clean')]
clean-artifacts *flags:
    {{ps}} tools\dev\clean.ps1 -Target artifacts {{flags}}; exit $LASTEXITCODE

# Report every git worktree, its merge/dirty state and build-output size (read-only)
[group('clean')]
worktrees:
    {{ps}} tools\dev\worktrees.ps1; exit $LASTEXITCODE

# Delete build output of idle OTHER worktrees (skips main, current, locked, running, building, recent)
[group('clean')]
[confirm("Delete bin/obj of idle other worktrees?")]
worktrees-clean:
    {{ps}} tools\dev\worktrees.ps1 -CleanBuildOutput; exit $LASTEXITCODE

# ------------------------------------------------------------------------------------------- internal

[private]
_dist-restore:
    {{pkg}} -Rid all -RestoreOnly; exit $LASTEXITCODE

[private]
[parallel]
_package-pair version="": (_package "win-x64" version) (_package "linux-x64" version)

[private]
_package rid version="":
    {{pkg}} -Rid {{rid}} -NoRestore {{ if version != "" { "-Version " + version } else { "" } }}; exit $LASTEXITCODE

[private]
_bin-unlocked:
    {{ps}} tools\dev\guard.ps1 -BinUnlocked -Configuration {{config}}; exit $LASTEXITCODE

[private]
_exe:
    if (-not (Test-Path '{{exe}}')) { Write-Host 'no {{exe}}: run just build first'; exit 1 }
