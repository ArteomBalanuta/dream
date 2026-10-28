# Configuration

## Project Target

The project file `dream/dream.csproj` currently targets:

- `net10.0`

Other enabled project settings include:

- `OutputType` set to `Exe`
- `UseAppHost` enabled so Windows builds emit `dream.exe`
- `ImplicitUsings` enabled
- `Nullable` enabled
- `AllowUnsafeBlocks` enabled

## SDK And Package Management

The repository now pins the SDK with:

- `global.json`: `10.0.201`

The repository also now uses central package management through:

- `Directory.Packages.props`

That means package versions are defined once at the repository root and the project files reference packages without inline version numbers.

## Package References

The current NuGet package references are:

- `BulletSharp` `0.11.1`
- `ImGui.NET` `1.91.6.1`
- `OpenTK` `4.9.4`
- `OpenTK.Windowing.Desktop` `4.9.4`
- `SharpGLTF.Core` `1.0.6`
- `SixLabors.ImageSharp` `3.1.12`

The test project now uses:

- `Microsoft.NET.Test.Sdk` `18.7.0`
- `xunit` `2.9.3`
- `xunit.runner.visualstudio` `3.1.5`

`System.Drawing.Common` has been removed because it is not used by the current codebase.

## Build Verification

The project was verified with:

```powershell
dotnet build dream\dream.csproj
```

Result:

- Build succeeded
- The build produced warnings, including package compatibility and vulnerability warnings

The current warnings include:

- `BulletSharp 0.11.1` restored from older .NET Framework targets instead of `net10.0`
- Several nullable and obsolete API warnings in the source files

The previous `SixLabors.ImageSharp 3.1.6` vulnerability warnings were removed by upgrading to `3.1.12`.

`SixLabors.ImageSharp 4.0.0` was evaluated but not adopted because it now enforces a build-time Six Labors license file or key for direct package dependencies.

## Window Configuration

The current `Game` constructor creates a window with:

- Title: `dream` (`GameWindowTitleBuilder.BaseTitle`)
- Initial size: `1440 x 1024`
- Frame cap: `GameWindow.UpdateFrequency` from `max_fps` (default `144`) or `DREAM_MAX_FPS`; VSync stays off (see Desktop Client Text Config)

The project also grabs the cursor on startup.

Runtime-adjustable window and camera settings are now grouped in `GameSettings.cs`, which currently owns:

- field of view
- mouse sensitivity
- camera view mode
- eye-height offset
- move speed
- jump velocity
- current floor size
- pending floor size
- collision geometry visibility
- current window width and height
- settings menu visibility

`GameSettings` also defines the bounded runtime floor-size range:

- minimum floor size: `5`
- maximum floor size: `100`

## Runtime Asset Path Assumptions

Runtime assets are now treated as project-owned content and resolved relative to the application runtime directory.

### Runtime asset folders

- `shaders/`
- `animations/`
- `fonts/`
- `config/`

### Resolution model

`AssetPaths.cs` builds runtime paths from `AppContext.BaseDirectory`, giving the application one portable asset root.

Current consumers include:

- `Render.cs` for shader file paths
- `Map.cs` for the `GrassTextureCube.gltf` model path
- `Game.cs` for `config/viewmodel-rig.txt`
- `Game.cs` for `config/desktop-client.txt`

### Output-copy behavior

`dream.csproj` now copies files from:

- `shaders\**\*`
- `animations\**\*`
- `fonts\**\*`
- `config\**\*`

into the build output so the runtime asset layout matches the resolution model.

## Viewmodel Rig Text Config

The project now treats the startup viewmodel rig defaults as a dedicated text-backed runtime config:

- source path: `dream/config/viewmodel-rig.txt`
- runtime path: `config/viewmodel-rig.txt`

The file is loaded once during startup through `ViewModelRigTextConfig` before `PlayScene` is created.

Current behavior:

- if the file is missing, the game recreates it from built-in defaults
- recognized keys override only their matching fields
- invalid numeric values fall back per-field instead of breaking startup
- unknown keys are ignored
- runtime menu edits do not yet auto-save back into the file

## Desktop Client Text Config

The desktop client now also treats the multiplayer startup form plus the main client-local settings menu defaults as a dedicated text-backed runtime config:

- source path: `dream/config/desktop-client.txt`
- runtime path: `config/desktop-client.txt`

The file is loaded once during `Game` startup through `DesktopClientTextConfig` before the startup connection scene or gameplay scene is created.

Current behavior:

- if the file is missing, the game recreates it from built-in defaults
- startup `Host`, `Port`, and `Player Name` fields are seeded from the file when environment overrides are absent
- `Field Of View`, `Mouse Sensitivity`, `Camera View`, `Fly Speed`, player `Strength`, `Attack Speed`, `Vitality`, and floating damage text tuning are seeded from the file
- edits to those startup/menu values now persist back into the file automatically
- invalid numeric values are clamped per field instead of breaking startup
- unknown keys are ignored

The `camera_view` key currently accepts `first_person` or `third_person`.

The `max_fps` key sets the render frame cap. The default is `144`; `0` means uncapped, and any other value is clamped to `30`-`500` (500 Hz is OpenTK's own `UpdateFrequency` limit), so negative values become `30`. `Game` applies the cap through `GameWindow.UpdateFrequency`, which drives both update and render frames in the single-threaded window. VSync stays off. The effective cap is printed once at startup, for example `[startup] frame cap: 144 FPS` or `[startup] frame cap: uncapped`.

## Profiling Environment Flags

For repeatable local profiling, set `DREAM_AUTO_CONNECT=1` before launching the desktop client. The startup scene waits for the integrated loopback server, applies its current local endpoint, and begins the same connection flow as the Connect button without requiring UI input.

Set `DREAM_MAX_FPS` to override the `max_fps` frame cap for one run without writing it back to `config/desktop-client.txt`. It uses the same rules: `0` means uncapped and other integers are clamped to `30`-`500`. A non-integer value is ignored and the config value is used.

Set `DREAM_RENDER_PERF_LOG=1` to print one render-performance line per second while in gameplay. The log includes render-export time, physics time, static chunk signature/extract/plan/sync/draw timings, static chunk cache reuse, visible chunk/triangle counts, and collision-body count.

Set `DREAM_NET_DIAGNOSTICS=1` to enable rate-limited multiplayer visibility and timing diagnostics. The log prints to the console and appends to `logs/network-diagnostics.log` under the application output folder by default. Set `DREAM_NET_DIAGNOSTICS_PATH` to write the diagnostics file somewhere else. This is intended for multi-client debugging: it records connect/join facts, session and actor snapshots, snapshot send/receive timing, client presenter state, remote-player draw counts, and slow command-send warnings.

## Network Frame Limits And Snapshot Rate

Every message travels as one length-prefixed frame (`LengthPrefixedMessageFramer`), and each direction has its own payload limit (`FrameLimitOptions`):

| Direction | Default | Environment override | Who enforces it |
| --- | --- | --- | --- |
| Client to server | 64 KB (65,536 bytes) | `DREAM_CLIENT_FRAME_LIMIT_BYTES` | The server reads with it; the client writes with it. |
| Server to client | 32 MB (33,554,432 bytes) | `DREAM_SERVER_FRAME_LIMIT_BYTES` | The client reads with it; the server writes with it. |

- A client only sends small frames (hello, join, one command per tick, actions, keepalives, resync requests), so the server's small read limit caps what a hostile peer can make it buffer.
- The server sends snapshots and, on join or resync, the whole world in one frame. 32 MB fits a 10k-cube world (`World10k` could not join under the old symmetric 1 MB limit). Chunked world transfer (SCALE-1) is the real fix; this limit is the stopgap.
- A frame over the limit is rejected from its 4-byte header, before any of its payload is buffered, so a hostile length cannot make the receiver allocate it. The receiving side closes the connection with the reason "Frame payload length N exceeds configured maximum M bytes": the server logs it as a connection error (`server.connection.error`, kind `error`), and the client sets `RemoteClientSession.DisconnectReason`. An oversized outbound frame fails at its writer and closes that connection.
- The variables are read by the dedicated server (`ServerBootstrap`), the integrated server (`IntegratedLocalServerHandle`) and the desktop client's connector (`RemoteSessionConnector`). Set them the same way on both ends. A value that is not a positive integer is ignored with a log line on standard error.
- Code that builds a framer itself with `new LengthPrefixedMessageFramer()` (tests and tools) keeps the single 1 MB limit for both directions.

The server simulates at 64 Hz but encodes and broadcasts actor snapshots only every `SnapshotIntervalTicks` ticks (`ServerConnectionOptions`, default `2`, so 32 snapshots a second). Set `DREAM_SNAPSHOT_INTERVAL_TICKS` to change it for one dedicated or integrated server run; `1` sends a snapshot after every tick, the behaviour before NETR-7. The interval counts ticks, so a loop iteration that runs two ticks still sends one snapshot. Nothing is lost on a held-back tick: its world mutations and damage events stay pending in the runtime and go out in the next snapshot, its action results are sent after that snapshot, and join replies and world resyncs are never held back. Clients interpolate remote actors by server tick, so 32 Hz snapshots only widen the interpolation delay by about one tick; command acknowledgements arrive every other tick.

## World Simulation Seed

Seeded server rolls (combat damage and the other `Seed.Combine(SimulationSeed, tick, session, actionSequence, targetId)` draws) start from the world runtime's `SimulationSeed`. Where that seed comes from depends on the host:

- **Persistent servers** (the dedicated server and the integrated server, both built by `ServerBootstrap.CreateAuthoritativeHost()`): each world has its own seed, stored in the SQLite database next to the rest of the world as the `simulation_seed` row of the `world_metadata` table (`key TEXT PRIMARY KEY, value TEXT`, the seed as a decimal unsigned 64-bit integer). When a world is created, `SqliteWorldMetadataRepository` generates the seed once with `RandomNumberGenerator` (it never picks `DefaultSimulationSeed`); every later load reads the stored value. A stored value that is not a valid unsigned 64-bit integer stops the server from starting rather than being silently replaced.
- **Hosts without persistence** (tests, `CreateAuthoritativeHost(null)`, `DedicatedServerHost.CreateRuntimeAttached(null)`, `RuntimeAuthoritativeSessionHost.CreateDefault()`): keep the fixed public `WorldSessionRuntime.DefaultSimulationSeed`, so determinism tests and ephemeral runs stay reproducible.

`SqliteSchemaBootstrapper.EnsureSchema` creates `world_metadata` with `CREATE TABLE IF NOT EXISTS`, so the change is additive. **An existing database that predates the table gets a freshly generated seed on its first load and keeps it from then on. That changes the rolls of an existing world once: the same tick, session, action and target roll differently than they did under the old shared default seed.** Persisted state (profiles, cubes, enemies, items) is not affected.

Set `DREAM_SIMULATION_SEED` to override the world's seed for one server run, for example to reproduce a world's rolls from a copy of its database or to pin rolls while debugging. It accepts a decimal (`12345`) or `0x`-prefixed hexadecimal (`0x6472...`) unsigned 64-bit integer. The override is applied for that run only and is never written back to `world_metadata`. An invalid value is ignored with a log line on standard error and the stored seed is used. The server logs that a seed was generated or overridden, never the seed value. On a fresh database the server still generates a random seed and stores it in `world_metadata` as usual; the override then applies to that run only, and the next run without the variable uses the stored random seed. The variable only affects persistent servers; hosts without persistence always use `DefaultSimulationSeed`.

The seed is server-only. No protocol message carries it (a reflection test in `WorldSimulationSeedTests` walks every `IProtocolMessage` type and its nested payload types for seed-like members), so clients cannot precompute rolls.

## Project Content Layout

The project file includes:

- Removal rules for an `animations\BlueCubeGenerator\**` subtree
- runtime asset copy rules for `shaders`, `animations`, and `fonts`

## Input And Frame Assumptions

The project currently expects:

- Keyboard input from OpenTK `KeyboardState`
- Mouse input from OpenTK `MouseState`
- A per-frame physics step driven from `OnUpdateFrame`
- Per-frame HUD drawing driven from `OnRenderFrame`

## Runtime Settings Flow

`Game.cs` now treats `Esc` as a global settings toggle. When settings are visible:

- the window cursor switches to `Normal`
- gameplay movement input is paused
- cube placement is paused
- ImGui receives live mouse input for the settings panel

When settings are hidden, the cursor is re-grabbed and normal gameplay input resumes.

For floor-size changes, the current flow is:

1. `Render` exposes a `Floor Size` slider and `Apply Floor Size` button.
2. `GameSettings` stages the selected value and clamps it into the supported `5..100` range.
3. `Game` applies the staged size only when the explicit apply action is requested.
4. `Game` rebuilds `Map` with the new square floor dimensions and rebinds the player to the rebuilt world.
5. Existing non-floor cubes are migrated if they remain in bounds; the player position is preserved or clamped.

For collision-debug changes, the current flow is:

1. `Render` exposes a `Show Collision Geometry` checkbox.
2. `GameSettings` stores the current runtime visibility state.
3. `Game` conditionally renders cube and player collision wireframes from Bullet transforms.

The cube visual transform now assumes centered unit-cube geometry, which matches both the built-in cube vertices and the current `GrassTextureCube.gltf` asset bounds.
