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
- `Microsoft.Data.Sqlite` `9.0.7` and `SQLitePCLRaw.bundle_e_sqlite3` `2.1.12` (server persistence)
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

- `Render.InitializeShaders` (`dream/Rendering/Core/Render.Shaders.cs`) for the world shader paths; the HUD and ImGui shader paths are in `Render.Hud.cs` and `Render.ImGui.cs`
- `GrassCubeAssetDefinition.ModelPath` (`dream/Rendering/Models/GrassCubeAssetDefinition.cs`) for the `GrassTextureCube.gltf` model path
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

## Client Connection And Diagnostics Variables

These are read by the desktop client (`RemoteClientOptions`, `PlayScene`) and by a few diagnostics switches. Unset means the default behaviour.

| Variable | Read in | Effect |
| --- | --- | --- |
| `DREAM_SERVER_HOST` | `RemoteClientOptions.TryResolveFromEnvironment` (called from `Game`) | Host the startup form is prefilled with; `DREAM_AUTO_CONNECT` then connects to it instead of the integrated local server. When it is unset or blank the other client variables are not read. |
| `DREAM_SERVER_PORT` | same | Port for the prefill (default `ServerBootstrap.DefaultPort`, 5057); a non-integer value throws at startup. It is client-only: the dedicated server always listens on 5057 and does not read it. |
| `DREAM_CLIENT_NAME` | same | Player name for the prefill. Default `dream-client`. |
| `DREAM_CLIENT_SESSION_ID` | same | Parsed, but `Game` forwards only host, port and name, so the desktop client still joins with a fresh `client-<guid>` (`ClientConnectionOptions`). |
| `DREAM_LOOPBACK_REMOTE_PLAYERS` | `PlayScene.ResolveLoopbackRemotePlayersEnabled` | `1` or `true` feeds fake remote-player snapshots into the presenter for local render checks. |
| `DREAM_MODEL_DIAGNOSTICS` | `AnimatedModelData` | `1` prints glTF vertex, material and skinning diagnostics to the console while models load. |
| `DREAM_PHYSICS_LEAK_TRACKING` | `PhysicsIntegrator.TrackLeaks` | `1` records a stack trace for each `PhysicsIntegrator` construction so a leaked owner can be found. Off by default because it is costly. |

## Test And Load Harness Variables

Read only by `dream.Tests` (see `docs/build-and-run.md`, Testing):

| Variable | Read in | Effect |
| --- | --- | --- |
| `DREAM_LOAD_TEST` | `LoadFactAttribute` | `1` enables the load, measurement and soak tests (`dream.Tests/Load`). Without it they are skipped, so the default suite stays fast. They write `artifacts/load/<scenario>.json` and a `.md` summary. |
| `DREAM_LOAD_DURATION_SECONDS` | `LoadScenario.DurationOverrideEnvironmentVariable` | Overrides every scenario's duration, in seconds. |
| `DREAM_W9_RUNS` | `Wave9NetworkMeasurementTests` | Repetitions per cell of the stall table (default 3). |
| `DREAM_W9_SCENARIOS` | `Wave9NetworkMeasurementTests` | Comma-separated stall scenarios to run (default `SendStall,FrameStall,NetworkStall,Steady200Ms,Hitchy`). |

`DREAM_BULLET_PROFILER_STRESS_SECONDS` and `DREAM_TEST_MAX_COLLECTIONS` are test-only too and are described under Physics and Test Scheduling below.

## Network Frame Limits And Snapshot Rate

Every message travels as one length-prefixed frame (`LengthPrefixedMessageFramer`), and each direction has its own payload limit (`FrameLimitOptions`):

| Direction | Default | Environment override | Who enforces it |
| --- | --- | --- | --- |
| Client to server | 64 KB (65,536 bytes) | `DREAM_CLIENT_FRAME_LIMIT_BYTES` | The server reads with it; the client writes with it. |
| Server to client | 8 MB (8,388,608 bytes) | `DREAM_SERVER_FRAME_LIMIT_BYTES` | The client reads with it; the server writes with it. |

- A client only sends small frames (hello, join, one command per tick, actions, keepalives, resync requests), so the server's small read limit caps what a hostile peer can make it buffer.
- No frame carries a whole world any more. Since protocol v3 (SCALE-1) a join or resync world goes out as a carrier message followed by `server.world-chunk` frames of about 64 KB (`WorldChunkMessage.DefaultChunkBytes`). The largest legitimate server frame is a lagging client's coalesced snapshot at the merge caps (4096 world mutations and 4096 damage events; past them the deltas are dropped and the client gets a resync instead).
- The 8 MB default is sized from measurements (`WorldTransferLimitTests`). The worst-case coalesced snapshot is 2.74 MB: every mutation is a fracture with six moved pieces, every number is at full length, and there are 320 actors. A World85k transfer's largest chunk frame is 65,752 B, and its carrier is 186 B. 8 MB leaves about 3x headroom over that worst case. 4 MB would leave only 1.5x, and a single tick's snapshot is not bounded by the merge caps. Until chunked transfer, the limit was 32 MB so that a `World10k` join world fit in one frame.
- A single broadcast snapshot is not bounded by the merge caps on its own: one advance runs up to 8 ticks (`DefaultMaxTicksPerAdvance`) and nothing caps one tick's fractures or damage, and its payload is shared by every connection, so a frame over the cap would fault every connection's writer in the same tick. Since wave 10 fix loop 2 the service checks each broadcast against the same merge caps (`ServerConnectionOptions.MaxCoalescedWorldMutations`, `MaxCoalescedDamageEvents`). Past them it sends the snapshot without its world mutations, damage events and world checksum (actors, interaction mode and command acknowledgements only), and flags every joined connection for a world resync on the coalesce-overflow path, so a connection whose join or resync world is still going out gets its resync once that transfer has finished. `DedicatedServerService.OversizedBroadcasts` counts these, and diagnostics log `server.snapshot.oversized`. A delta snapshot under the caps whose frame still passes the cap is caught at the writer the same way: it goes out without its deltas, the connection is flagged for a resync (`ServerConnection.OversizedSnapshotFrames`, log `server.connection.snapshot-oversized`), and no connection faults.
- A frame over the limit is rejected from its 4-byte header, before any of its payload is buffered, so a hostile length cannot make the receiver allocate it. The receiving side closes the connection with the reason "Frame payload length N exceeds configured maximum M bytes": the server logs it as a connection error (`server.connection.error`, kind `error`), and the client sets `RemoteClientSession.DisconnectReason`. Any other oversized outbound frame (a snapshot that is still too large without its deltas, for example) fails at its writer and closes that connection.
- A client bounds each world transfer it receives (`WorldTransferLimits`). Besides the item and held-message limits, a transfer fails when no chunk arrives for 30 s after the carrier or the previous chunk (`ProgressTimeout`), or when it is not complete 300 s after the carrier (`Deadline`). On the slowest link the server keeps (a 64 KB chunk inside the 5 s send-stall timeout, about 12.5 KB/s), a chunk follows the previous one within about 10 s, because the writer sends one queued message between each pair of chunks. A World85k resync can take more than 120 s there, the single bound before fix loop 2. The client checks both bounds after each frame is accepted, so a final chunk that arrives right at a bound still completes its transfer. A breach ends the session with "World transfer failed: ..." and the reason (`did not finish within N s`, or `stalled: no chunk for N s`).
- The variables are read by the dedicated server (`ServerBootstrap`), the integrated server (`IntegratedLocalServerHandle`) and the desktop client's connector (`RemoteSessionConnector`). Set them the same way on both ends. A value that is not a positive integer is ignored with a log line on standard error.
- Code that builds a framer itself with `new LengthPrefixedMessageFramer()` (tests and tools) keeps the single 1 MB limit for both directions.

The server simulates at 64 Hz but encodes and broadcasts actor snapshots only every `SnapshotIntervalTicks` ticks (`ServerConnectionOptions`, default `2`, so 32 snapshots a second). Set `DREAM_SNAPSHOT_INTERVAL_TICKS` to change it for one dedicated or integrated server run; `1` sends a snapshot after every tick, the behaviour before NETR-7. The interval counts ticks, so a loop iteration that runs two ticks still sends one snapshot. Nothing is lost on a held-back tick: its world mutations and damage events stay pending in the runtime and go out in the next snapshot, its action results are sent after that snapshot, and join replies and world resyncs are never held back. Clients interpolate remote actors by server tick, so 32 Hz snapshots only widen the interpolation delay by about one tick; command acknowledgements arrive every other tick.

`DREAM_SNAPSHOT_INTERVAL_TICKS` is clamped to at most `16` ticks (250 ms). Snapshots are the server's steady traffic to a joined client, and the client's server-silence watchdog (`RemoteClientSession.ServerSilenceTimeout`, 8 s) ends a session that receives no data from the server (any inbound bytes, a partial frame included) for that long. An unclamped value such as `1000` (about 15.6 s between snapshots) would make every idle client disconnect itself with "Connection lost: no data from the server for 8 s". The clamp keeps snapshots more than 30 times faster than the watchdog.

### Connection liveness

| Check | Where | Default | Notes |
| --- | --- | --- | --- |
| Server idle timeout | `ServerConnectionOptions.IdleTimeout` | 10 s | Closes a connection that has delivered no complete inbound frame for this long. Off (`TimeSpan.Zero`) on the integrated server, whose one client shares its process, so a window drag or debugger break would look like a dead client. |
| Stale-session takeover | `DedicatedServerHost.TakeoverSilence` | 8 s (same session id or same name) | A join for a session id, or a player name, that another connection holds takes it over when that connection has sent no frame (keepalives included) for the window: the old connection is released (its profile saved), the new one gets the seat and the saved profile, and the service closes the old transport. One window covers both matches: the desktop client picks a fresh `client-<guid>` on every connect, so its crash rejoin is always a name match, and session ids are broadcast in snapshots, so a shorter session-id window would only let a hijacker skip the name window. 8 s is far past a healthy idle client's keepalive gaps (up to about 1.5 s) and still under the 10 s idle release. A holder heard from inside the window keeps it; the join is rejected with `SessionInUseRejectionReason` (same session id) or the duplicate-display-name reason (same name). Silence is measured up to the service's last per-tick frame-count sweep, so a tick stall does not make idle clients look silent. Uses the connection options' clock. |
| Join throttle | `ServerRoutingContext.JoinThrottleInitialInterval`, `JoinThrottleMaxInterval`; `JoinThrottleTable.MaxEntries` | 2 s, doubling to 10 s; 1024 entries | Only starts after a live holder rejects a join. Keyed by source and contested session id or player name: the source is the remote IP address, parsed from the connection id, which the TCP transport sets to the socket's `ip:port` remote endpoint (IPv4-mapped IPv6 folded to IPv4); an id that is not an IP endpoint (in-memory and test transports) is its own source. Further joins for that session id or name from that source are refused until the interval passes, on any connection, so reconnecting does not reset it; each further live-holder rejection doubles it, and an accepted join from the source clears it. A refused join is never claimed or queued, so it costs no persisted-player prefetch. A fresh connection gets one `JoinRejected` (`JoinThrottledRejectionReason`, with the seconds left); a connection already answered gets nothing (logged once per interval as `server.join.throttled`). Clients sharing an address (NAT, a loopback test server) with different names never throttle each other, and a spammer cannot lock the holder out of its own name from the holder's address. An entry is forgotten 10 s after its interval ends; past 1024 entries the one whose interval ended earliest is evicted. |
| Server send stall | `ServerConnectionOptions.SendStallTimeout` | 5 s plus frame size at 256 KB/s | Closes a connection whose current socket write does not finish in time. Off on the integrated server. |
| Client keepalive | `RemoteClientSession.KeepaliveInterval` | 1 s | When no frame has gone out for this long (a frozen game loop), the send loop writes one empty frame. The server counts it as liveness and skips it. |
| Client server-silence watchdog | `RemoteClientSession.ServerSilenceTimeout` | 8 s | A joined session that receives no inbound bytes for this long ends with `ServerSilenceDisconnectReason`. Any bytes read reset it, including part of a large frame still arriving, so a slow multi-megabyte world transfer does not trip it. Outbound keepalives do not reset it. |
| Client join timeout | `RemoteClientSession.JoinTimeout` | 45 s | Join retries (`MaxJoinRetries`, 3, from 1 s doubling) for a join the server could not load in time. |

**Identity limits.** There is no authentication. A session id is a fresh `client-<guid>` per connect and is broadcast to every client in snapshots, and a saved profile is keyed by display name, so the takeover window and the join throttle only make it harder for another client to take a seat during a network hiccup; anyone can still claim a name once its holder has been silent for 8 s or has left. The planned stronger fix (option D) is a per-profile reconnect token issued on join and required to take over a profile; it waits on a user decision.

None of these have an environment override. When a session ends for any reason, the desktop client goes back to the connection screen and shows the reason (see `docs/architecture.md`, Startup Flow).

## Physics Environment Flags

Set `DREAM_CUBE_REST_FREEZE=0` to turn off `CubeRestFreezer` for one run (server or client). Resting placed cubes then stay dynamic Bullet bodies, as before PHYS-2. Use it to reproduce pre-PHYS-2 simulation results or to compare tick cost (`CubeRestFreezerLoadMeasurementTests`). Any other value, or no value, keeps freezing on. See `docs/architecture.md`, Physics Model.

Set `DREAM_BULLET_PROFILER_GUARD=0` to leave libbulletc's `btGetCurrentThreadIndex` unpatched. `BulletProfilerGuard` still finds the function and reports `Disabled`. Only use this to reproduce the native crash the guard prevents. See `docs/architecture.md`, Physics Model.

`DREAM_BULLET_PROFILER_STRESS_SECONDS` is read only by `BulletProfilerThreadIndexTests`. It sets how long the profiler-index stress test runs (a decimal number of seconds, invariant culture; default `0.3`). With `DREAM_BULLET_PROFILER_GUARD=0` and a few seconds, the test reproduces the `0xC0000005` crash.

## Test Scheduling Environment Flags

`DREAM_TEST_MAX_COLLECTIONS` overrides how many gated test collections run at once in the parallel phase (`TestScheduling.MaxRunningCollections`). The default is `max(4, ProcessorCount * 3 / 4)`. A value that is not a positive integer is ignored. A value above the thread pool minimum that `TestAssemblyInitializer` sets (`ProcessorCount * 4`) is clamped to it, with a line on standard error. More slots than pool threads would bring back the thread-injection delay that the gate exists to avoid. It is for scheduling experiments only; it changes no test or budget.

## World Clock And Sky Variables

| Variable | Read in | Effect |
| --- | --- | --- |
| `DREAM_DAY_LENGTH_MINUTES` | `ServerBootstrap.ResolveDayLengthMinutes` (called from `ServerBootstrap.CreateRuntimeHost`, so the dedicated and the integrated server) | Real-time minutes per in-game day. Default 30 (115,200 ticks at 64 ticks/s; a new world starts at 08:00). Decimals are accepted; values outside 1–1440 are clamped and an unparsable value falls back to 30, each with a line on standard error. Not persisted: the world keeps its `world_time_ticks`, so changing the day length between runs moves the time of day once. Server-only; clients receive it in the join-accepted world clock anchor. |
| `DREAM_NIGHT_BRIGHTNESS` | `SkyModelSettings.FromEnvironment` (desktop client) | Up-facing light at night as a fraction of noon's. Default 0.15, clamped to 0–1; an unparsable value uses the default. Client-only presentation; it changes no simulation state. |

The world clock counts authoritative ticks, never wall time, so the time of day stands still while the server is stopped. It is saved with each non-null checkpoint as the `world_time_ticks` row of `world_metadata` (see `docs/server-persistence-ddl.md`): a clean stop loses nothing and a crash loses at most the time since the last committed checkpoint (about a minute in an idle world, whose incremental checkpoints are skipped until the next full sweep).

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
