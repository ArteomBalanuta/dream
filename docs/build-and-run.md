# Build And Run

## Prerequisites

- `.NET 10 SDK`
- Windows desktop environment with OpenGL support

Run the commands in this guide from the repository root (the folder that holds `global.json`, `dream\` and `dream.Tests\`). `dream\dream.sln` lists only the `dream` project; build and test the projects by path, as below.

## Build

Use:

```powershell
dotnet build dream\dream.csproj
```

Current expected result:

- build succeeds
- warnings are still present
- Windows builds emit `dream\bin\Debug\net10.0\dream.exe` as the runnable desktop executable

The SDK build summary may still mention `dream.dll`; that DLL is the managed assembly behind the executable app host.

## Run

Use:

```powershell
dotnet run --project dream\dream.csproj
```

To run the dedicated server instead of the desktop client:

```powershell
dotnet run --project dream\dream.csproj -- --server
```

It listens on TCP port 5057 by default; `DREAM_PORT` (1 to 65535) changes it and `DREAM_BIND_ADDRESS` picks the interface (see `docs/configuration.md`, Dedicated Server Settings). The protocol version is 22 (`TransportEnvelope.CurrentProtocolVersion`). It keeps its SQLite world database at `config/player-profiles.db` under its output folder (`ServerBootstrap.DefaultDatabasePath`). The desktop client starts an integrated loopback server on demand for Single Player and Host.

The project copies runtime assets into the build output and resolves them through `AssetPaths`, so shader, animation, font, audio and UI files are loaded from the runtime output layout rather than from machine-specific absolute paths.

## Default Runtime Behavior

On startup the desktop client opens a `1440 x 1024` window on the menu (Single Player, Host, Join). The camera is isometric; it is the only view the client reaches. Window, camera and HUD defaults come from `config/desktop-client.txt` (`camera_view=isometric`, `field_of_view`, `mouse_sensitivity`, ...), written next to the executable.

### Default controls

- Right mouse button: click-to-move. Walks to the clicked ground cell; on an enemy it sets an attack goal; in Building mode it places a cube
- Left mouse button: the character faces the cursor; in Combat mode holding it attacks
- `W`, `A`, `S`, `D`: move relative to the facing. `Space`: jump
- `B` / `G`: select building / combat mode
- `1` to `6`: use the quickbar slot (a skill, or the first matching consumable in the bag); open the inventory (`V`) and click a quickbar tile to change what it holds. Skills need combat mode, except Mend
- `Q` / `R`: drink the first healing / mana potion in the bag
- `E`: pick up the nearest ground item or talk to the nearest NPC (vendor, quest giver)
- `Enter`: open the chat line (`Esc` closes it); hold `Tab` for the online list
- `V`: open or close the inventory and character windows
- `Esc`: close the chat line, else an open vendor, dialogue or trade window, else the inventory, else open or close the pause menu
- Developer keys, only where dev commands are allowed (`DREAM_DEV_COMMANDS=1` or your own integrated server): `0` toggles noclip, `F` toggles frozen movement. `F3` swaps the HUD when `admin_hud=1` or `DREAM_ADMIN_HUD=1`

While the pause menu is open, gameplay input is paused.

## Test

Use:

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```

The suite covers the simulation, protocol and transport, server, persistence, client replication, rendering helpers and project-structure rules (`dream.Tests/ProjectStructure`). The full suite takes about 3.5 to 4.5 minutes in Debug (`TestScheduling` runs the long collections first and caps concurrency). Filter with `--filter`, for example `--filter FullyQualifiedName~HudLayoutTests`, or use `tools\dev\test.ps1` (below). Do not run two suites at once: concurrent runs give spurious persistence failures and testhost hangs.

### Testing

- **A running `dream.exe` locks `dream\bin`** and fails the build's copy step. Do not kill a game you are playing; build and test into another folder with an absolute path inside the repo (repo-relative tests find the repo root from the output folder):

  ```powershell
  dotnet test dream.Tests\dream.Tests.csproj "/p:BaseOutputPath=<repo>\artifacts\verify-bin\"
  ```

  From Git Bash, prefix the command with `MSYS_NO_PATHCONV=1` so `/p:` is not rewritten as a path.
- **Native Bullet files.** `dream.Tests.csproj` copies `libbulletc*` and `BulletSharp.dll.config` from `dream\bin\<Configuration>\net10.0` into the test output (`CopyDreamNativeRuntime`). When you override `BaseOutputPath`, first build `dream` in that configuration without the override (`dotnet build dream\dream.csproj -c Release` for Release tests) so that folder exists.
- **Scheduling (ARCH-1/1b).** `xunit.runner.json` selects the aggressive parallel algorithm with unlimited threads. `TestAssemblyInitializer` raises the thread-pool minimum to `ProcessorCount * 4` so blocked tests cannot starve the work they wait for. `TestScheduling` caps how many collections run at once (`DREAM_TEST_MAX_COLLECTIONS`, see `docs/configuration.md`), splits slow real-time classes into one collection per test, and starts the longest collections first.
- **`TimingSensitive` collection** (`TimingSensitiveCollection`). Tests whose assertions are about the wall clock itself (the tick pacer, the 250 ms mesh-build budget, checkpoint persistence, the load tests) and the physics leak-tracking teardown test run alone, after the parallel phase. Everything else, including the loopback and stress tests, runs in the parallel pool and waits on progress with a generous safety deadline instead of on elapsed time. Keep new tests out of this collection unless they truly measure the clock.
- **GC mode.** The test host runs workstation concurrent GC (`ServerGarbageCollection=false`, `ConcurrentGarbageCollection=true` in `dream.Tests.csproj`), the same as production, so GC counts and tick percentiles from the load harness mean something.
- **Load and measurement tests** (`dream.Tests/Load`) are skipped unless `DREAM_LOAD_TEST=1`. They start a real server on loopback and write `artifacts/load/<scenario>.json` and a `.md` summary. Use Release. See `skills/profiling-dream-multiplayer/SKILL.md` and the variables in `docs/configuration.md`.
- **Performance harness.** `skills/profiling-dream-multiplayer/scripts/launch-and-summarize.ps1` runs a dedicated server plus desktop clients and writes `logs/perf-run-*`. `-NoStopExisting` keeps it from stopping running `dream` processes, and `-Exe <path>` launches a `dream.exe` from another output folder (pair it with `-SkipBuild`).

### Dead assertions (coverage of the test assembly itself)

A check that cannot fail reads as coverage but is not. The mechanical test is coverage over `dream.Tests`
itself after a green run: an `Assert.` line with zero hits is a dead check, and a conditional-assert guard
whose body never executed is a claim nothing verifies. The collector is **opt-in**; a plain
`dotnet test dream.Tests\dream.Tests.csproj` neither restores nor loads it.

```powershell
dotnet test dream.Tests\dream.Tests.csproj -p:Coverage=true `
  --settings dream.Tests\coverage.runsettings --collect:"XPlat Code Coverage" `
  --results-directory artifacts\coverage
```

- `-p:Coverage=true` is what adds the `coverlet.collector` reference (`dream.Tests.csproj`); without it
  nothing changes for anyone.
- `dream.Tests/coverage.runsettings` sets `IncludeTestAssembly=true` and `Include=[dream.Tests]*`. Both
  matter: coverlet excludes the test assembly by default, so the stock configuration measures the game and
  says nothing about the assertions.
- The run writes `coverage.json` (per-line hit counts and per-branch hits, easiest to script) and
  `coverage.cobertura.xml`. Zero-hit `Assert.` lines are the list to triage; expect the
  `DREAM_LOAD_TEST`-gated `dream.Tests/Load` tests to dominate it, since they never ran.
- Coverage costs about two extra minutes on the full suite and does not change the
  result.

## Fast Loops

- **Test tiers.** `tools\dev\test.ps1 -Area Content|Rendering|Simulation|Net|Client|Fast|Full [-Filter x] [-TimeoutMinutes 20]` runs the matching `dotnet test --filter`, tees the output to `artifacts/test-logs/<area>-<stamp>.log`, kills only its own process tree on timeout, refuses to start while a `testhost` is running (`-AllowConcurrent` overrides), and appends `utc,sha,area,seconds,passed,failed,skipped` to `artifacts/test-durations.csv`. `Content` runs in about 20 seconds with a warm build; `Fast` leaves out `Integration`, `Load` and `BugHunt`; `Full` is the whole suite.
- **Content check.** `dream.exe --validate-content [--dir <contentDir>] [--ui <uiDir>] [--strict]` loads the content JSON, runs the formula probe and warns about missing UI sprites, without a window, a lock or a database. It exits 0 when valid, 1 on a usage error and 8 on invalid content (or on warnings with `--strict`).
- **Captures from source.** `skills/capturing-dream-screenshots/scripts/capture.ps1 -FromSource` points `DREAM_ASSET_ROOT` at the source tree, so edited JSON, atlases and shaders apply on the next launch with no rebuild. Each run gets its own data directory (`DREAM_DATA_DIR`), so captures can run in parallel. See `skills/capturing-dream-screenshots/SKILL.md`.
- **Dev commands.** Where dev commands are allowed and the connection is loopback, a chat line starting with `/dev` stages the world on the server: `/dev spawn wolf 2`, `/dev tp camp <key>`, `/dev give <item>`, `/dev clear`, `/dev heal`, `/dev hp <n>`, `/dev mana <n>`; several can be chained with `;`. In capture scripts the same commands are the `dev:` step (`dev:clear;spawn:wolf:2:3`) and `tod:<hours>` sets the visual time of day. See `docs/configuration.md`, Dev commands.
- **Worktrees and art.** `tools\dev\worktrees.ps1` reports every git worktree with its merge and dirty state and the size of its build output (read-only without flags); `tools\dev\gallery.py` writes `artifacts/gallery/index.html`. See `tools/dev/README.md`.

## Known Warnings

The project currently builds with warnings, including:

- `BulletSharp 0.11.1` compatibility warnings against `net10.0`

Warnings are currently expected. Build failures are the condition that should block progress.

## Runtime Assets

The runtime asset folders are:

- `dream/shaders/`
- `dream/animations/`
- `dream/fonts/`
- `dream/audio/`
- `dream/ui/`
- `dream/config/`

These directories are copied into the output under:

- `dream/bin/Debug/net10.0/shaders/`
- `dream/bin/Debug/net10.0/animations/`
- `dream/bin/Debug/net10.0/fonts/`
- `dream/bin/Debug/net10.0/audio/`
- `dream/bin/Debug/net10.0/ui/`
- `dream/bin/Debug/net10.0/config/`

If runtime asset loading fails, verify those output folders exist after build.

## Local Generated Files

The repo may generate local runtime artifacts such as:

- `imgui.ini`
- `dream-run*.log`

These are ignored by git and should not be treated as source files.

## Troubleshooting

### `.NET` SDK missing

If `dotnet` commands fail before build starts, verify that `.NET 10 SDK` is installed and available in `PATH`.

### Build warnings vs errors

Warnings are currently expected. If the build exits with non-zero status or shows actual compile errors, treat that as a project problem rather than normal state.

### Runtime assets missing from output

If the application starts failing on missing shader or model files again:

1. Run `dotnet build dream\dream.csproj`
2. Check the output directories under `dream/bin/Debug/net10.0/`
3. Confirm `shaders/`, `animations/`, `fonts/`, `audio/` and `ui/` are present

### Tests for shared runtime helpers

If asset-path or shared-model behavior changes, rerun:

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```
