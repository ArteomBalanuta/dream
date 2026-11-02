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

It listens on TCP port 5057 (`ServerBootstrap.DefaultPort`, not configurable by environment variable) with protocol version 13 (`TransportEnvelope.CurrentProtocolVersion`) and keeps its SQLite world database at `config/player-profiles.db` under its output folder (`ServerBootstrap.cs:112`). The desktop client starts an integrated loopback server on demand when you pick the local server on the connection screen.

The project copies runtime assets into the build output and resolves them through `AssetPaths`, so shader, animation, and font files are loaded from the runtime output layout rather than from machine-specific absolute paths.

## Default Runtime Behavior

On startup, the game currently:

- opens a `1440 x 1024` window
- grabs the mouse cursor for camera look
- starts with a `70` degree field of view
- builds the HUD crosshair from the live viewport instead of from fixed HUD pixel coordinates
- scales the HUD crosshair with window size while keeping it centered on the active viewport
- starts with a `56 x 56` floor (`WorldDefaults.StartupFloorSize`): the size of a new local world, changeable from the settings menu (it does not resize an existing world or a running session)
- loads the default first-person hands and held-item blockout viewmodels from `dream/animations/viewmodels/`

### Default controls

- `W`, `A`, `S`, `D`: move
- Mouse movement: look around
- `Space`: jump
- `C`: toggle first-person / third-person camera
- `B` / `G`: select building / combat mode
- `1` to `6`: use the quickbar slot (a skill, or the first matching consumable in the bag); open the inventory (`V`) and click a quickbar tile to change what it holds. Skills need combat mode, except Mend
- Left mouse button: the active mode's primary action (place a cube, or attack)
- `F`: toggle frozen movement
- `V`: open or close the inventory and character windows; the cursor is released and shown as a sprite
- `Esc`: close the inventory if it is open, otherwise open or close the settings menu

When the settings menu is open, the cursor is released and gameplay input is paused.

## Runtime Verification For Viewmodels

Use:

```powershell
dotnet run --project dream\dream.csproj
```

Current first-milestone verification flow:

1. Start the app and keep the settings menu closed so gameplay input is active.
2. Confirm the blockout hands and held item are visible in front of the camera.
3. Move and look around to confirm the world stays behind the first-person pass.
4. Left click to trigger the `Use` animation on the hands and held item.
5. Confirm the update-driven clip playback advances and the models settle back into their looping `Idle` presentation after the one-shot finishes.
6. Confirm the HUD crosshair and ImGui still render on top of the first-person pass.

Success for this milestone means:

- both first-person slots render every frame
- the `PlayScene.Update()` viewmodel step advances clips and returns one-shots to idle without getting stuck
- the pass uses live viewport aspect without obvious stretching after resize
- left click visibly triggers the one-shot use motion
- the world and debug passes remain behind the hands and held item
- the HUD remains the top-most layer

## Runtime Floor Resize

Open the settings menu with `Esc`, move the `Floor Size` slider, and press `Apply Floor Size`.

Current behavior:

- the slider range is `5` to `100`
- the value represents a square `N x N` floor
- the world is rebuilt in place when you apply the change
- non-floor cubes are preserved only if they still fit inside the rebuilt bounds
- the player position is preserved when possible and otherwise clamped back into valid space

## Test

Use:

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```

The suite covers the simulation, protocol and transport, server, persistence, client replication, rendering helpers and project-structure rules (`dream.Tests/ProjectStructure`). Filter with `--filter`, for example `--filter FullyQualifiedName~HudLayoutTests`.

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

## Known Warnings

The project currently builds with warnings, including:

- `BulletSharp 0.11.1` compatibility warnings against `net10.0`

Warnings are currently expected. Build failures are the condition that should block progress.

## Runtime Assets

The runtime asset folders are:

- `dream/shaders/`
- `dream/animations/`
- `dream/fonts/`
- `dream/config/`

These directories are copied into the output under:

- `dream/bin/Debug/net10.0/shaders/`
- `dream/bin/Debug/net10.0/animations/`
- `dream/bin/Debug/net10.0/fonts/`
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
3. Confirm `shaders/`, `animations/`, and `fonts/` are present
4. If the first-person pass is missing assets, confirm `animations/viewmodels/DebugHands.gltf` and `animations/viewmodels/DebugHeldItem.gltf` were copied into the output tree

### Viewmodel rig config missing or reset

If `config/viewmodel-rig.txt` is missing from the runtime output:

1. Start the game normally
2. The startup bootstrap will recreate the file from built-in defaults
3. Close the app and inspect `dream/bin/Debug/net10.0/config/viewmodel-rig.txt`

Current behavior is startup-load only. Editing values in the in-game menu does not yet save them back into the text file automatically.

### Tests for shared runtime helpers

If asset-path or shared-model behavior changes, rerun:

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```

### Movement or camera feels wrong

If the app starts but movement or camera feel off:

1. Press `Esc` to open the settings menu
2. Adjust `Field of View` or `Mouse Sensitivity`
3. If needed, change the resolution preset and re-test movement feel

The current player camera uses a configurable eye-height offset and a viewport-aware projection matrix. At runtime, the in-app controls exposed through the settings menu are `Field of View`, `Mouse Sensitivity`, `Third Person View`, and the resolution presets.
The same menu now also exposes `Floor Size` with an explicit apply action, rebuilding the map without discarding cubes that still fit.
The HUD crosshair now follows the live viewport as well, so future HUD placement changes should be driven from viewport-derived layout values rather than hardcoded viewport pixel coordinates.

## Pending Runtime Verification

Live visual verification of the viewport-driven crosshair at multiple window sizes is still pending and should be handled separately if a change needs end-to-end runtime confirmation.

The first-person milestone was manually checked with `dotnet run --project dream\dream.csproj`; screenshots were kept locally under `artifacts/` for review and were not committed to the repository.
