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

Since persistence wave B the floor size is only the size of a **new local world**: the integrated server uses it when it has to create a world (`ServerStartOptions.NewWorldSize`). It does not resize a world that exists, and changing it in the HUD does not rebuild a running session's map. A session's map size comes from the world descriptor the server sends at join (see Worlds below).

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
- `Field Of View`, `Mouse Sensitivity`, `Camera View`, `Fly Speed`, and floating damage text tuning are seeded from the file
- edits to those startup/menu values now persist back into the file automatically
- invalid numeric values are clamped per field instead of breaking startup
- unknown keys are ignored

Older files may still hold `strength`, `attack_speed` or `vitality`: they are ignored, and the next save drops them (wave G). A character's stats live on the server.

The `camera_view` key currently accepts `first_person` or `third_person`.

The `max_fps` key sets the render frame cap. The default is `144`; `0` means uncapped, and any other value is clamped to `30`-`500` (500 Hz is OpenTK's own `UpdateFrequency` limit), so negative values become `30`. `Game` applies the cap through `GameWindow.UpdateFrequency`, which drives both update and render frames in the single-threaded window. VSync stays off. The effective cap is printed once at startup, for example `[startup] frame cap: 144 FPS` or `[startup] frame cap: uncapped`.

The `admin_hud` key (`0` or `1`, default `0`) makes the F3 key and the developer controls in Settings available. The `ui_scale` key sets the classic HUD scale: `0` (default) fits the window, other values snap to quarter steps from 1 to 4 and are then capped at the auto fit for the window, because a larger scale pushes the windows under the bar (3 at 1440x1024 is used as 2; the Settings slider shows the scale in use). The Settings window has a UI Scale slider that writes `ui_scale` back. Missing keys use these defaults. The shipped `dream/config/desktop-client.txt` is not changed; the game adds the keys the next time it saves.

### HUD (classic and ImGui)

The in-game HUD is the classic sprite HUD by default. The ImGui HUD is the automatic fallback: if the sprite atlas is missing, corrupt, fails to upload or is still loading after 5 s, or if drawing throws, or if OpenGL reports an error while a page is uploaded, while the renderer is created, or on its first draw (checked once and latched, not every frame), the game switches to ImGui for the rest of the run and writes one `[render-fault] [hud-classic]` line. This is independent of `admin_hud`. One exception to the fallback is the existing fatal rule: `GL_OUT_OF_MEMORY` and a lost context are latched Fatal by `GLUtils.CheckGLError`, and the window closes instead of falling back, because GL state is undefined after them. The classic HUD does not change that rule.

- `admin_hud=1` (or `DREAM_ADMIN_HUD=1` for one run; `DREAM_ADMIN_HUD=0` turns a configured `1` off for one run): F3 swaps between the classic and the ImGui HUD, in-session only. Without it F3 does nothing. Once the classic HUD has failed, F3 is inert too.
- Without admin availability, Settings hides the developer controls (Fly Speed, stats, collision geometry, performance counters, new world size, viewmodel tuning, time of day). Field of view, mouse sensitivity, third person, floating damage text, resolution presets, connection info and UI Scale stay. Set `admin_hud=1` to get them back.
- `DREAM_HUD=imgui` forces the ImGui HUD for the run (baseline comparison); F3 is inert then.
- The environment overrides are never written back to the file. Nothing about the admin setting is sent to the server: it hides and shows client-side UI only, and the server accepts the same intents either way.
- The local player's own incoming damage number (red) cannot project from the first-person camera, so the classic HUD draws it at a fixed spot: horizontal centre, 62% down the window, between the crosshair and the bar. In third person it rises over the character. Enemy labels that would overlap move down below the earlier one; at most 24 are drawn, and none once less than 4096 of the 8192 batch quads remain. A frame that overflows the batch logs one `[hud-classic] ... quads did not fit` line per run.
- `DREAM_HUD_CAPTURE=<dir>` and `DREAM_HUD_CAPTURE_SCRIPT=play,inventory,settings,admin` write one PNG per step (`NN-<step>.png`) of the real back buffer after a short warm-up, then close the window. `DREAM_HUD_CAPTURE_SETTLE=<frames>` holds each step longer (default 20), so `DREAM_RENDER_PERF_LOG=1` gets lines from each state. The `admin` step needs `DREAM_ADMIN_HUD=1`. Verification tool only.
- `DREAM_HUD_CAPTURE_SCRIPT` also takes pointer steps. They feed synthetic pointer states through the real input router and panels (the scripted pointer replaces the mouse only while a script runs), so they send the same inventory intents a hand would, and the script logs the inventory and the character stat rows before and after each one: `hover:<slot>`, `drag:<from>:<to>`, `dragging:<from>:<to>` (button held), `release`, `rightclick:<slot>`, `menu:<row>`, `confirm:destroy|cancel`, `close`, `esc`, `v`, `size:<w>:<h>`, `burst:<a>:<b>:<n>` (n fast drops, to meet the server's action budget), `wait:<frames>`, `snap`, `log`, and `fight:<frames>` (for that many frames, face the nearest live enemy, walk in, ask for combat mode and hold the attack through the real command path, then capture; when the walk stalls it jumps, then sidesteps, and after a long stall it turns noclip on once, which then stays on; it is how a script gets damage numbers, loot and a death; set `DREAM_MAX_FPS=60` so frames are about 1/60 s, since the server runs in real time; a script holds at most 80 steps). A slot is `0..63` for a bag anchor (row * 8 + col; as a drag target it is where the item's top-left cell lands, with the item centred on the pointer, and a target where the item does not fit is clamped onto the board as a hand would get), or `100..111` / `main|off|head|body|hands|feet|legs|neck|ring1|ring2|back|pet` for the twelve equipment wells. After a step that sends an action the script waits for the server's answer (or 240 frames), then writes `NN-<step>.png` with `:` as `-`. Verification tool only; with the variable unset nothing about it runs.
- While `DREAM_HUD_CAPTURE` is set the window is created without taking focus (`StartFocused` off), so a capture run does not pull focus from whatever else is on screen; with it unset the window starts focused as before.

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
| `DREAM_SOAK_REFERENCE_P99_MS` | `StoredWorldSoakTests.LongSoak_StoredWorld10k_115200Ticks_SixRestarts` | Optional. The tick p99 in milliseconds of `LongSoak_GeneratedReference_115200Ticks_SixRestarts` on the C0 build; when set, the stored soak fails if its own p99 exceeds it by more than 10%. |

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
| Same-account takeover (hosts with accounts: dedicated and integrated) | none | immediate | A join by the account that owns a character takes the seat from a live or silent holder of that character or session id at once: the old connection is released (its profile saved) and closed, and it sees a lost connection. Another account never takes a seat: a held session id gives `SessionInUseRejectionReason`, stale or not (the 10 s idle release frees it), and a held character gives `CharacterOwned`, or `CharacterInUse` when it is unowned. |
| Stale-session takeover (only hosts without accounts: tests and tools) | `DedicatedServerHost.TakeoverSilence` | 8 s (same session id or same name) | A join for a session id, or a player name, that another connection holds takes it over when that connection has sent no frame (keepalives included) for the window: the old connection is released (its profile saved), the new one gets the seat and the saved profile, and the service closes the old transport. One window covers both matches: the desktop client picks a fresh `client-<guid>` on every connect, so its crash rejoin is always a name match, and session ids are broadcast in snapshots, so a shorter session-id window would only let a hijacker skip the name window. 8 s is far past a healthy idle client's keepalive gaps (up to about 1.5 s) and still under the 10 s idle release. A holder heard from inside the window keeps it; the join is rejected with `SessionInUseRejectionReason` (same session id) or the duplicate-display-name reason (same name). Silence is measured up to the service's last per-tick frame-count sweep, so a tick stall does not make idle clients look silent. Uses the connection options' clock. |
| Join throttle | `ServerRoutingContext.JoinThrottleInitialInterval`, `JoinThrottleMaxInterval`; `JoinThrottleTable.MaxEntries` | 2 s, doubling to 10 s; 1024 entries | Only starts after a live holder rejects a join, or after an ownership rejection (`CharacterOwned`), which needs no live holder. The rejected token retrying within the interval is told to retry in a few seconds, even though nobody may be connected. Keyed by source, account and contested session id or player name, so a spammer on the owner's address never throttles the owner: the source is the remote IP address, parsed from the connection id, which the TCP transport sets to the socket's `ip:port` remote endpoint (IPv4-mapped IPv6 folded to IPv4); an id that is not an IP endpoint (in-memory and test transports) is its own source. Further joins for that session id or name from that source are refused until the interval passes, on any connection, so reconnecting does not reset it; each further live-holder rejection doubles it, and an accepted join from the source clears it. A refused join is never claimed or queued, so it costs no persisted-player prefetch. A fresh connection gets one `JoinRejected` (`JoinThrottledRejectionReason`, with the seconds left); a connection already answered gets nothing (logged once per interval as `server.join.throttled`). Clients sharing an address (NAT, a loopback test server) with different names never throttle each other, and a spammer cannot lock the holder out of its own name from the holder's address. An entry is forgotten 10 s after its interval ends; past 1024 entries the one whose interval ended earliest is evicted. |
| Server send stall | `ServerConnectionOptions.SendStallTimeout` | 5 s plus frame size at 256 KB/s | Closes a connection whose current socket write does not finish in time. Off on the integrated server. |
| Client keepalive | `RemoteClientSession.KeepaliveInterval` | 1 s | When no frame has gone out for this long (a frozen game loop), the send loop writes one empty frame. The server counts it as liveness and skips it. |
| New-account limit | `NewAccountLimiter`; `DREAM_NEW_ACCOUNT_BURST`, `DREAM_NEW_ACCOUNTS_PER_MINUTE` | burst 5, then 3 a minute, per source address | A join whose token owns no character yet (it creates the account) counts against its source address: the first 5 are free, then one more every 20 s (3 a minute). Past that the join is refused with `NewAccountRejectionReason` and the seconds to wait. Without it, new tokens bypass the 5-characters-per-token cap. Accounts that already own a character are never limited, and loopback sources (`127.0.0.1`, `::1`: local play, the perf harness, tests) are exempt. The count is kept only after a join is accepted. Memory only (no database read on the tick); the table holds at most 1024 sources, dropping full buckets first, then the least recently used. `DREAM_NEW_ACCOUNT_BURST` and `DREAM_NEW_ACCOUNTS_PER_MINUTE` set the two numbers (non-negative integers, an invalid value keeps the default); `0` for either turns the limit off. Behind a NAT or proxy, everyone shares one address. |
| Same character in two worlds | none | n/a | Profiles are global (one row per character) while two worlds can be hosted at once. The same character live in two worlds at the same time is last-save-wins on the global fields (level, experience, stats, hit points), so progress made in one of them can be lost when the other saves. A restore takes the stored row over its own cached copy only when the row is newer and the cached copy was saved before the join's load began. |
| Client server-silence watchdog | `RemoteClientSession.ServerSilenceTimeout` | 8 s | A joined session that receives no inbound bytes for this long ends with `ServerSilenceDisconnectReason`. Any bytes read reset it, including part of a large frame still arriving, so a slow multi-megabyte world transfer does not trip it. Outbound keepalives do not reset it. |
| Client join timeout | `RemoteClientSession.JoinTimeout` | 45 s | Join retries (`MaxJoinRetries`, 3, from 1 s doubling) for a join the server could not load in time. |

**Identity limits.** An account is a random token kept on the player's computer, not a login (decision U10). The token travels in plain text in the join request, so anyone who can capture the traffic gets it; the server stores only its SHA-256. A session id is a fresh `client-<guid>` per connect, is broadcast to every client and grants nothing. A token file that is lost means a new account: an admin frees the old characters with `--characters release`.

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
| `DREAM_TIME_OF_DAY` | `ServerBootstrap.ResolveStartWorldTicks` and `WorldTimeOfDay` (called from `ServerBootstrap.CreateRuntimeHost`, so the dedicated and the integrated server) | Sets the time of day of the world clock at server start. Accepts `HH:MM` (24 h, `22:30`, `6:00`), a decimal hour (`13.5`, 0 up to but excluding 24) or a name, case-insensitive: `dawn` 06:00 (sunrise keyframe), `morning` 08:00, `noon` 12:00, `dusk` and `evening` 18:00 (sunset keyframe, sun on the horizon), `midnight` 00:00, `night` 22:00 (inside the flat night keyframes). Unset or blank changes nothing; an invalid value is ignored with a line on standard error. The clock only moves forward, to the next tick showing that time (a later time the same day, else the next day; an exact match changes nothing), using the `DREAM_DAY_LENGTH_MINUTES` day length. One line `World time set to HH:MM by DREAM_TIME_OF_DAY` is logged. The new world time is saved by the normal checkpoints (`world_time_ticks`), so a restart without the variable continues from it; with the variable still set, every start moves the clock forward again. Clients receive it in the join-accepted world clock anchor. Server-side, so it also changes the night wolf (see below). The client-only debug slider in Settings (`Render.TimeOfDayOverride`) is the purely visual alternative. |
| `DREAM_DAY_LENGTH_MINUTES` | `ServerBootstrap.ResolveDayLengthMinutes` (called from `ServerBootstrap.CreateRuntimeHost`, so the dedicated and the integrated server) | Real-time minutes per in-game day. Default 30 (115,200 ticks at 64 ticks/s; a new world starts at 08:00). Decimals are accepted; values outside 1–1440 are clamped and an unparsable value falls back to 30, each with a line on standard error. Not persisted: the world keeps its `world_time_ticks` (a column of its `worlds` row), so changing the day length between runs moves the time of day once. Server-only; clients receive it in the join-accepted world clock anchor. |
| `DREAM_NIGHT_BRIGHTNESS` | `SkyModelSettings.FromEnvironment` (desktop client) | Up-facing light at night as a fraction of noon's. Default 0.07 (0.15 before wave T2), clamped to 0–1; an unparsable value uses the default. Client-only presentation; it changes no simulation state. Torches and lamps (point lights) are what make the night readable. |
| `DREAM_DEBUG_POINT_LIGHTS` | desktop client (`Render.PointLights`) | Adds a deterministic ring of N synthetic point lights around the camera, for the perf harness. Default 0 (off), clamped to 0–64. Client-only debug; it changes no simulation state. At most 16 lights reach the shaders per frame, so values above 16 measure the collection and selection cost only. |

The world clock counts authoritative ticks, never wall time, so the time of day stands still while the server is stopped. It is saved with each non-null checkpoint in the `world_time_ticks` column of the world's `worlds` row (see `docs/server-persistence-ddl.md`): a clean stop loses nothing and a crash loses at most the time since the last committed checkpoint (about a minute in an idle world, whose incremental checkpoints are skipped until the next full sweep).

Since wave T2 the clock is a gameplay input: at night (20:00 to 04:00) the server keeps a second wolf (see `docs/architecture.md`, Update Loop). The phase is derived from ticks only, so the persisted `world_time_ticks`, the `TicksPerDay` from `DREAM_DAY_LENGTH_MINUTES` and the number of ticks run are lockstep inputs: two servers that must produce the same checksums need the same three. Changing `DREAM_DAY_LENGTH_MINUTES` between runs can move a world into or out of night, which adds or removes the night wolf on the first tick.

## World Simulation Seed

Seeded server rolls (combat damage and the other `Seed.Combine(SimulationSeed, tick, session, actionSequence, targetId)` draws) start from the world runtime's `SimulationSeed`. Where that seed comes from depends on the host:

- **Persistent servers** (the dedicated server and the integrated server, both built by `ServerBootstrap.CreateAuthoritativeHost()`): each world has its own seed, stored in the SQLite database on the world's row, in the `seed` column of the `worlds` table (the seed as decimal text of an unsigned 64-bit integer; NULL means "generate on first load"). When a world is created, the catalog (or `SqliteWorldMetadataRepository` for a NULL seed) generates the seed once with `RandomNumberGenerator` (it never picks `DefaultSimulationSeed`); every later load reads the stored value. A stored value that is not a valid unsigned 64-bit integer stops the server from starting rather than being silently replaced.
- **Hosts without persistence** (tests, `CreateAuthoritativeHost(null)`, `DedicatedServerHost.CreateRuntimeAttached(null)`, `RuntimeAuthoritativeSessionHost.CreateDefault()`): keep the fixed public `WorldSessionRuntime.DefaultSimulationSeed`, so determinism tests and ephemeral runs stay reproducible.

A database from before schema version 3 kept the seed as the `simulation_seed` row of `world_metadata`; migration 0003 moves it onto world 1's `worlds` row unchanged, so a migrated world keeps its rolls. (A database that predates the seed altogether got a freshly generated one on its first load, once, which changed that world's rolls once.) Persisted state (profiles, cubes, enemies, items) is not affected either way.

Set `DREAM_SIMULATION_SEED` to override the world's seed for one server run, for example to reproduce a world's rolls from a copy of its database or to pin rolls while debugging. It accepts a decimal (`12345`) or `0x`-prefixed hexadecimal (`0x6472...`) unsigned 64-bit integer. The override is applied for that run only and is never written back to the `worlds` row. An invalid value is ignored with a log line on standard error and the stored seed is used. The server logs that a seed was generated or overridden, never the seed value. On a fresh database the server still generates a random seed and stores it on the world's row as usual; the override then applies to that run only, and the next run without the variable uses the stored random seed. The variable only affects persistent servers; hosts without persistence always use `DefaultSimulationSeed`.

The seed is server-only. No protocol message carries it (a reflection test in `WorldSimulationSeedTests` walks every `IProtocolMessage` type and its nested payload types for seed-like members), so clients cannot precompute rolls.

## Worlds

One database holds any number of worlds, and one server process runs exactly one of them (persistence wave B; see `docs/architecture.md`, Worlds as rows, and `docs/server-persistence-ddl.md`, Worlds). A fresh or migrated database has world 1, named `default`, 56 x 56.

### Choosing the world a server runs

| Setting | Read in | Effect |
| --- | --- | --- |
| `--world <id\|name>` | `Program` (with `--server`) | Opens that world. All digits is an id, which must exist. Anything else is a name (trimmed, case-insensitive), opened if it exists and created if it does not. |
| `DREAM_WORLD` | `ServerBootstrap.WorldEnvironmentVariable` | The same selector, used when `--world` is not given. |
| neither | | The default: world 1 if it exists, else the lowest id, else a new world named `default`. |

A name that creates a world gives it the new-world size: `ServerStartOptions.NewWorldSize`, which the integrated server takes from the floor size setting, and 56 for the dedicated server. The new world gets a random seed. The server logs the world's name and id, never its seed.

An id that does not exist stops the dedicated server with exit code **4** (`WorldNotFoundException`). A world that cannot be opened stops it with exit code **5**: a `--world` name over 64 characters, a world whose size is outside 5 to 100 or whose height is not 8, a generated world with an unsupported generator, or a stored world whose terrain chunks are missing, partial, corrupt or over the transfer limits (wave E). The log line reads `world '<selector>' cannot be opened: <reason>`; a terrain blob that fails to decode says `the stored terrain is invalid` (`StoredTerrainInvalidException`, raised only where the server builds the stored map), any other invalid data at startup says `invalid data: <reason>`. A failed start releases the persistence bundle and write queue. The other dedicated-server exit code is 3, for a database migrated by a newer build (`SchemaTooNewException`).

A name that does not exist is created, so a typo such as `--world fo` makes a new empty world. The server says so on standard error: `World 'fo' (id 5) did not exist and was created (size 56).` An existing world logs `opened`.

### Managing worlds: `--worlds`

`dream --worlds <command>` works on the database and exits without starting a server or a window. `copy`, `delete`, `export` and `reset-terrain` take the world's host lease for the length of the command and refuse a world that a server (or another command) holds; see "Host lease" below.

| Command | Effect |
| --- | --- |
| `--worlds list` | Prints one tab-separated line per world: id, name, size (`WxHxD`), generator (`key:version`, or `stored:key:version` for a stored world), world time in ticks. The seed is never printed. |
| `--worlds create <name> [--size N]` | Creates a world with a random seed. `N` is 5 to 100 (default 56). Prints the new world in the `list` format. |
| `--worlds copy <src> <name>` | Copies a world (by id or name) with every row it owns, the same local ids, and its seed and world time. Prints the new world in the `list` format. |
| `--worlds delete <id\|name> --yes` | Deletes a world and everything in it. Without `--yes` nothing is deleted. Prints `deleted`, id and name. |
| `--worlds export <id\|name> <new name> [--terrain-only]` | Wave E. Creates a NEW stored world from a world's current terrain (never in place; to turn a world into a stored one, export it and delete the old one). The terrain is the source's map with its destroyed cells baked out. Without `--terrain-only` the new world also gets the source's other rows (cubes, rubble, enemies, items, players, the same ids, counter, seed and world time) but no overrides; with `--terrain-only` it is a fresh world with the chunks only, a new seed, world time 0 and no other rows (a map template). Prints the new world in the `list` format. |
| `--worlds reset-terrain <id\|name> --yes` | Wave E. Restores the world's own base terrain: the generator output for a generated world, its own chunks for a stored world. Deletes every terrain override and the rubble of those cells, plus any rubble on a source cell of the world's base terrain whose origin is neither an override nor a placed cube and whose cell holds no placed cube (the rubble of a lost override; debris in air cells and in cells a baked export removed stays). Cells that hold a saved placed cube keep their override and rubble and are reported. Players and enemies saved inside a restored cell are counted and not moved. Prints `reset-terrain`, id, name, `cleared=`, `rubble=`, `kept=`, `inside=` (players plus enemies inside restored cells) and, when players are among them, `inside_players=` (comma-separated names). Without `--yes` nothing changes. |

`export` and `reset-terrain` are offline commands: **stop the server that hosts the world first.** A running server's later checkpoint or restore heal could silently undo a reset, and an export reads the database as it is on disk, so since wave F3 both take the world's host lease and exit with code 7, writing nothing, when the world is held. A run that is given no lease (a test seam only) prints "stop the server hosting this world first" on standard error instead. A stored world is hosted like any other (`--world <id|name>`, `DREAM_WORLD`); the server logs `World '<name>' (id n) stored terrain: chunks= bytes= sha256=` at start.

Exit codes: **0** success, **1** usage error, **2** world not found or name conflict, **3** the database schema is newer than this build, **4** the terrain is invalid for hosting (export: the baked chunks exceed the hosting limits), **7** the world is held by a running server or another command (nothing is changed). A bad argument, including a size outside 5 to 100 or a name that is blank or longer than 64 characters, is a usage error (1). A name already taken ignoring case is a conflict (2).

Names are 1 to 64 characters after trimming and unique ignoring case. A name cannot be only ASCII digits (`--world 2024` selects the id 2024, so such a world could never be selected by name) and cannot contain control characters such as a tab or a newline (they break the `list` output); `create` and `copy` reject both as a usage error (1). World ids are never reused. A copy of a world that has no stored seed stores one on the source first, so both share it.

### Host lease, integrity check and backups (wave F3)

**Host lease.** One holder per world on a database file: an exclusive, OS-locked file `{db}.world-{id}.lock` next to the database (`WorldHostLease`, opened with `FileShare.None`). No schema is involved. Order of a start: read the content, take the database's **maintenance lock** (`{db}.maintenance.lock`, held only around the next three steps), migrate the schema, resolve the world, take the world's lease and re-read the world's record (so its world time and seed are what the previous holder left), release the maintenance lock, then run the integrity check. The lease is held until the persistence bundle is disposed; a failed start releases it. When migrations are pending, the migrating process also takes the lease of every world in the database first; if a server holds one, the start (or `--worlds` command) exits **7** and the database is not migrated under it. A start waits up to 20 seconds for a restore or another start's migration to release the maintenance lock, then exits **7**. A lock file that cannot be created or opened for any reason other than a live holder (read-only file, missing or unwritable directory) is reported as `cannot create the lock file <path>: <reason>` (server exit **5**, `--worlds` and `--db` exit **2**) instead of being taken for a held world. The operating system drops the lock when the process dies, so a crash frees the world. The lock file itself stays in place and is harmless. A database that is not a file takes no lease. A second server for a held world stops with exit code **7** and logs `world <id> is already hosted by another process (lock file ...)`. Different worlds of one database can be hosted at once.

A second desktop client on the same database and world does not get a local server: it stays open on the connection screen, shows `Local server failed: World <id> is already in use by another Dream window or server on this computer...`, logs `[startup] local server not started: ...`, and Connect still joins any other server. After closing the first window, press **Use Local Server** in the second client: it clears the failed start and tries again, and fills in the host and port only once the server is ready (while it is not, the entered host and port stay as they are and nothing is saved). To join the first window instead, leave the second client's local server off and type the first window's `127.0.0.1:<port>` (the port its connection screen shows as `Local server ready on 127.0.0.1:<port>`) as the host and port, then Connect. A client whose content files are missing or invalid shows the reason on the connection screen and does not connect. Clients started with `DREAM_SERVER_HOST` never start a local server and are not affected; to run two clients in one world, start `dream --server` and give both clients `DREAM_SERVER_HOST=127.0.0.1`.

**Content check at start.** Every dedicated or integrated start reads `config/content/items.json`, `enemies.json` and `loot.json` once (`ContentCatalog.Shipped`), before it opens the database, and logs `server.content.digest digest=<sha256> items=<n> enemies=<n>`. Two servers with different digests run different content. A missing or invalid file stops a dedicated server with exit code **8** and the log line `Server not started: the game content is invalid: <reason>`; an integrated server shows the reason on the connection screen. A client that only joins a remote server does not need these files: a content error blocks Use Local Server, not a remote connect.

**Formula probe at start (wave I).** Before the world opens, the server evaluates the enemy formulas for every enemy template and `player.damage_taken_factor` for armor 1..1000. A formula that fails, for example a hand-edited row that divides by zero, stops it with exit code **8** and the name of the formula, instead of failing on a spawn tick.

**Integrity check on load.** After the lease and before the stored terrain is read, the server runs `WorldIntegrityCheck` over its world: `PRAGMA quick_check`, `PRAGMA foreign_key_check`, objects outside the world's bounds (cubes, rubble parent cells, terrain overrides), inventory slots that point at a missing item, a worn item (100..111) that still has a bag row of the same player, and item or enemy kinds the build does not know. Wave H adds four counts: an item in two equipment slots, a slot index outside 0..63, 100..111 and 200..215, an item in an equipment slot its content does not allow, and a damaged stored roll (read only for rows that have one). Bag items that overlap or run off the board and held-back rows are information only: they appear in the head line, and the server repairs the layout when it loads the inventory. A foreign-key violation or a damaged file (also a `SQLITE_CORRUPT` or `SQLITE_NOTADB` raised earlier in the start) stops the server with exit code **6**. Everything else is only counted, in one log line per finding, and never refuses the start, so a real legacy database is not rejected for soft problems. The known-kind predicates (`Func<string, bool>`) and the footprints come from `ContentCatalog.Shipped` (`ServerBootstrap.IntegrityOptionsFor`); `WorldIntegrityCheck` itself accepts every kind unless it is given them. Cost on the World10k database (2.6 MB): about 2 ms without `quick_check` and about 7 ms with it. `quick_check` reads the whole file, so its cost grows with the file size; it stays on because that is one pass at start.

**Backup and restore.** `dream --db backup <path>` copies the whole database file with `SqliteConnection.BackupDatabase`, checks the copy with `integrity_check`, and moves it into place. It is safe while a server runs and refuses an existing destination. `dream --db restore <path> --yes` is an offline file swap. It takes the maintenance lock (so no start can create or open a world between its checks and the swap) and every world's lease (the worlds in the database plus every existing lock file), and exits **7** if either is held. It validates the source first: a schema version newer than this build exits **3**; a failed `integrity_check` or damaged pages (SQLite error 11) exit **6**; a missing source, a file that is not a SQLite database, a zero-byte file, or a SQLite file with neither a `worlds` nor a `player_profiles` table exits **2**. A legacy database from before `schema_version` has `player_profiles` and restores. It keeps the current file as `{db}.pre-restore.{timestamp}.bak` (a `-1`, `-2`... suffix if that name exists), then swaps the source in. The safety copy never depends on the health of the file being replaced: it uses the SQLite backup API and, when that fails because the live file is damaged (a bad header is error 26, a truncated file 11), a raw copy of the file with its `-wal` and `-shm`; so restoring over a damaged database, the case exit 6 tells the operator to fix, works. If the raw copy also fails, or the live file cannot be replaced (locked), the restore stops with exit **2**, the live file is left as it was and the staging file is removed; the next start migrates an older source with the usual backup. There is no per-world restore: player profiles are global, so restoring one world would leave orphans.

**Process exit codes.** Server: **3** schema newer than this build, **4** world not found, **5** world cannot be opened, **6** database integrity failure, **7** world held, **8** invalid content file or a formula that fails the start probe. `--worlds`: 1 usage, 2 not found or conflict, 3 schema too new, 4 terrain invalid, 7 world held. `--db`: 1 usage, 2 source or target invalid, 3 schema too new, 6 integrity failure, 7 world held. `--characters` and `--items` are listed in their own sections below.

## Characters And Accounts

One account owns up to 5 characters. Level, experience and base stats follow a character between the worlds of a database; items, position and health stay in each world, and a first visit to a world starts at full health. A character can be joined only by the account that owns it. An unowned character (from an older database, or released) goes to the first token that joins it. Names are matched ignoring ASCII case. A new name must be 1 to 32 UTF-16 code units after trimming, with no control, format, line or paragraph separator or private-use characters and no unpaired surrogates; existing names keep working whatever they contain.

### Token file

| Item | Value |
| --- | --- |
| Location | `%LOCALAPPDATA%\Dream\account-tokens.json` (the per-user local application data folder on other systems). `DREAM_IDENTITY_FILE` replaces the path, for tests and for two accounts on one machine. |
| Tokens | One per server, keyed by lowercase `host:port`. Every loopback address and port (`localhost`, `127.0.0.1`, `::1`, the integrated server on whatever port it gets, a dedicated server or another window's server on this computer) shares the key `local`, so one person is one account there. Older entries such as `127.0.0.1:5057` stay in the file, unused. |
| Stat sliders | `PlayerStatsUpdateIntent` is a development tool (decision U4): any client can set its own base stats within the validated range, and they persist and travel between worlds with the character. Removed when levels give stat points. |
| Corrupt file | Moved aside to `.bad-<UTC stamp>`, never overwritten. A new file starts, which means new accounts. |

**Two clients on one machine** share the token file, so they are one account. With the same name the second window takes the seat and closes the first; use different names, or give each its own `DREAM_IDENTITY_FILE`.

**The integrated server and a dedicated server on the same computer are one account** for one player: both are loopback, so both use the key `local`. A dedicated server on another machine is another account; a character claimed through one is refused through the other with "belongs to someone else" until it is released. Names the perf harness (`launch-and-summarize.ps1`) writes into `desktop-client.txt`: after a harness run, change the name, or `--characters release` it. Each harness client has a stable identity file beside the executable (`perf-identity-client<n>.json`). A harness run whose names are owned by other tokens joins nobody; check for `server.join-rejected` in the client logs.

### Managing characters: `--characters`

```
dream --characters list                  display name, key, level, account id or -, last seen; tab-separated
dream --characters release <name>        makes the character unowned; the next token to join claims it
dream --characters rename <old> <new>    changes the display name; the key never changes
```

`<name>` matches the display name ignoring ASCII case, then the exact key. `list` and `release` work while a server runs. `rename` needs every world free, because a running server would write the old name back. The old name of a renamed character stays reserved when it was the key. After `release`, anyone may claim first, so time it with the owner. Account ids have gaps: every join uses one up.

Exit codes: 0 done, 1 usage or an invalid name, 2 not found or the name is taken, 3 the database is newer than this build, 7 a world is held.

### Rolled items: `--items`

```
dream --items grant <world> <character> <item kind> [--rarity common|uncommon|rare]
dream --items list <world> <character>
```

`grant` creates one rolled item at the first spot of the 8x8 bag its footprint fits (row by row), as one transaction. It prints item id, slot, rarity and modifiers, tab-separated. Rarity defaults to common. `list` prints the character's slots, kinds, rarity and modifiers. `<world>` is an id or a name; `<character>` matches the display name ignoring ASCII case, then the exact key.

`grant` is offline: it takes the world's lease, so it refuses a world a server is hosting. There is no live grant. The character must have joined that world once, because the starter grant row has to exist: without it the next join would treat the inventory as a pre-kit one. Only a kind with a `rolls` table in `items.json` can be granted (every `gear.*` kind today). `list` takes no lease.

Exit codes: 0 done, 1 usage, 2 not found, no starter grant, not rollable or backpack full, 3 the database is newer than this build, 7 the world is held or the database is busy (a maintenance lock or a SQLite busy error), 8 invalid content.

### Equipment slots

| Slot index | Meaning |
| --- | --- |
| 0..63 | Bag: the top-left cell (`row * 8 + col`) of an item on the 8x8 board. The item covers its `footprint` from there. |
| 100 | Main hand. |
| 101 | Off hand. |
| 102 | Head. |
| 103 | Body. |
| 104 | Hands. |
| 105 | Feet. |
| 106 | Legs. |
| 107 | Neck. |
| 108 | Left ring. |
| 109 | Right ring. |
| 110 | Back. |
| 111 | Companion. |
| 200..215 | Held back: items that did not fit the board when the inventory loaded. They move onto the board when room appears, and the inventory shows a count. Loot never lands here. |
| 300 up | Parked: overflow past the 16 held-back rows, only reachable with hand-edited data or a large footprint change. Kept and inert: not shown, and not moved back on its own. |

Any other index is an unknown row, kept and inert. A worn item has only its row in 100..111: it leaves the bag when equipped, and one item may be in one equipment slot. Unequipping puts it on the named cell, which must be free, or at the first fit, and a bag with no room rejects the action. A bag item dropped on an occupied spot swaps with the one item there, which goes to the mover's old anchor if it fits, else the first fit, else the move is refused. A worn item dropped on an occupied equipment slot swaps only when each item may occupy the other's slot. Which slots an item may occupy comes from `equipSlots` in `items.json`, and `footprint` is `[cols, rows]`, 1..4, default 1x1. Armor is shown and summed but changes no combat result yet. Inventory actions have a budget per session: a burst of 16 actions that would write, refilling 8 a second, counted in server ticks. A dead player can still use inventory actions (equip, move, unequip); that is harmless, because none of them revives the player.

### Loot tables: `config/content/loot.json`

Read at start with the other content files, and part of the content digest. `enemies.json` names a table per enemy with `lootTable`, and a reference to a missing table stops the server. Editing a table needs a restart. The shipped numbers are placeholders for a balance pass.

```json
{"version":1,"tables":[
 {"key":"wolf","drops":1,"nothingWeight":75,
  "items":[{"item":"gear.leather-cap","weight":10}],
  "rarity":{"common":85,"uncommon":13,"rare":2}}]}
```

| Field | Meaning |
| --- | --- |
| `drops` | Rolls per kill, 1..4. |
| `nothingWeight` | Weight of dropping nothing on one roll. |
| `items` | The item kinds a roll can give. Each must exist in `items.json` and have `rolls`. |
| `rarity` | Weights of the rarity the item is rolled at. |

Weights are 0..1,000,000 with an int total and at least one above 0. Duplicates and unknown properties fail at load. The killing blow takes the drop. A dropped item goes to the first spot of the bag its footprint fits, and when it fits nowhere it is lost with a notice, even with free cells showing. There are no ground drops.

### Enemies: `config/content/enemies.json` (version 2)

`strength`, `vitality`, `attackSpeed`, `level`, `visualKey` and `spawnRole` are required. `attackIntervalSeconds` and `lootTable` are optional. Strength and Vitality are 1..50, Attack Speed 0.5..5, level 1 or more. Damage, health and interval come from the `enemy.*` formulas in `combat_formulas` (schema 9). `spawnRole` is `Primary` or `NightExtra`, and at least one `Primary` is required. A spawn copies its stats into its row, so an edit reaches new spawns only, never an enemy already stored.

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

`Esc` is the settings toggle, with one exception that holds on both HUD surfaces (classic and ImGui): with the inventory open and Settings closed, `Esc` closes the inventory and leaves Settings shut (it used to open Settings over it). From play, or with Settings open, `Esc` toggles Settings as before. When settings are visible:

- the window cursor switches to `Normal`
- gameplay movement input is paused
- cube placement is paused
- ImGui receives live mouse input for the settings panel

When settings are hidden, the cursor is re-grabbed and normal gameplay input resumes.

For floor-size changes, the current flow is:

1. `Render` exposes a `New local world size` slider and apply button.
2. `GameSettings` stages the selected value and clamps it into the supported `5..100` range.
3. The apply action persists the setting. It no longer rebuilds the live map: the value is the size of the next world the integrated server has to create (see Worlds).
4. A running session keeps the map size of its world; the size of a joined world always comes from the server's world descriptor.
5. A client that is sent a world it cannot host (a generator or version it does not know, a map source other than `generated`, a size outside 5 to 100) ends the session with a reason such as `The server's world "x" uses generator "nope" version 1, which this client does not support.`; a join-accepted whose initial world differs in size from its descriptor ends it the same way (`PlayScene.ApplyReplicatedMessage` -> `RemoteClientSession.EndSessionWithReason`).

For collision-debug changes, the current flow is:

1. `Render` exposes a `Show Collision Geometry` checkbox.
2. `GameSettings` stores the current runtime visibility state.
3. `Game` conditionally renders cube and player collision wireframes from Bullet transforms.

The cube visual transform now assumes centered unit-cube geometry, which matches both the built-in cube vertices and the current `GrassTextureCube.gltf` asset bounds.
