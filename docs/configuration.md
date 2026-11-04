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

The cursor stays free: movement is by clicking, so the OS cursor is hidden only while the classic HUD draws its own sprite cursor.

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

The `msaa` key sets the multisample count of the window's default framebuffer: `0` (off), `2` or `4` (default); other values snap to the nearest of these. It is a window-creation hint (`GLFW_SAMPLES`), so it only takes effect on the next launch, and the Settings row says so. No framebuffer object is involved: the resolve happens at swap. The driver may grant fewer samples than requested, so the granted count is logged at startup (`[startup] msaa: requested 4x, granted 4 samples`). Alpha-mask (cutout) materials `discard` fragments, which kills every sample, so their edges are not smoothed.

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
- `DREAM_HUD_CAPTURE_SCRIPT` also takes pointer steps. They feed synthetic pointer states through the real input router and panels (the scripted pointer replaces the mouse only while a script runs), so they send the same inventory intents a hand would, and the script logs the inventory and the character stat rows before and after each one: `hover:<slot>`, `drag:<from>:<to>`, `dragging:<from>:<to>` (button held), `release`, `rightclick:<slot>`, `menu:<row>`, `confirm:destroy|cancel`, `close`, `esc`, `v`, `size:<w>:<h>`, `burst:<a>:<b>:<n>` (n fast drops, to meet the server's action budget), `wait:<frames>`, `snap`, `log`, and `walkto:<x>:<z>:<maxFrames>` (clicks the ground at that cell through the real click path, aiming at its projection or, off screen, at the screen edge toward it, until the character is within 1.5 m or the frames run out; logs `walkto x,z start=... end=... clicks=N pathCells=M walked=metres reached=bool`, where `pathCells` is the last click's path only), `screenclick:<x>:<y>` (left click at a window pixel; since the left button is the attack in Combat mode, over the world this means "attack at this pixel", not "walk there"), `buildat:<x>:<z>` (right click the ground at that cell; Building mode places a cube there), `cell:<x>:<z>` (print which levels of that column hold a cell, plus the player and the goal), and `fight:<frames>` (for that many frames, face the nearest live enemy, walk in, ask for combat mode and hold the attack through the real command path, then capture; when the walk stalls it jumps, then sidesteps, and after a long stall it turns noclip on once, which then stays on; it is how a script gets damage numbers, loot and a death; set `DREAM_MAX_FPS=60` so frames are about 1/60 s, since the server runs in real time; a script holds at most 80 steps; `enemyclick:left|right` clicks the nearest live enemy on screen, `groundclick:<x>:<z>` right-clicks a ground cell once, `holdmove:<x1>:<z1>:<x2>:<z2>:<frames>` holds the right button on the first cell and carries the cursor to the second (click-to-move is the right button since the WASD and cursor-aim change, so `walkto`, `groundclick`, `holdmove` and `enemyclick:right` need Combat mode: put `keys:g:3` before them; `enemyclick:left` is now a left press on the enemy, one attack in Combat), `keys:<names>:<frames>` holds keys through the real key mapping (`w a s d space ctrl noclip b g q r`, joined with `+`; `noclip` is the 0 key, `b` and `g` (also `building` and `combat`) are the B and G mode keys, `q` and `r` drink the first heal and mana potion), `status` logs the player, the click goal, the mode, noclip and the enemies, and `waitidle:<frames>` waits until the click goal ends, up to that many frames). Cursor-aim steps: `aim:<x>:<y>` latches the scripted pointer at a window pixel with no button held, and it stays there through every step after it (`keys`, `status`, `snap`, `wait`...), so the character's facing (which follows the cursor's ground point) is deterministic while keys are held; the pixel must be over the world, not over the bar or an open window. `aimenemy` does the same at the nearest live enemy's pixel, read when the step begins. `holdleft:<frames>` holds the left button at the latched pointer for that many frames and releases (the held attack in Combat mode: the first hit on the press, then one per attack interval, none on release); it logs the attack interval, every enemy health drop and kill with the frame and milliseconds since the press (hits that land after the step ended are tagged), and the status line. Before the first `aim` the pointer rests at 30% / 40% of the window. `status` prints, on one line, the position, the facing (`Camera.Front` x,z and yaw in degrees, the facing the server moves the character with) and the forward/strafe axes the held keys resolve to against that facing. During a `fight` step the cursor aim is suppressed, since the step steers with look deltas of its own. Example, the user's scenario (walk left while facing a cursor to the right, from open ground): `play,keys:g:3,walkto:25:25:1500,aim:1000:520,status,keys:a:90,status,snap`. A slot is `0..63` for a bag anchor (row * 8 + col; as a drag target it is where the item's top-left cell lands, with the item centred on the pointer, and a target where the item does not fit is clamped onto the board as a hand would get), or `100..111` / `main|off|head|body|hands|feet|legs|neck|ring1|ring2|back|pet` for the twelve equipment wells. After a step that sends an action the script waits for the server's answer (or 240 frames), then writes `NN-<step>.png` with `:` as `-`. Verification tool only; with the variable unset nothing about it runs.
- Held-pose and actor-crop steps (the measuring half of the vocabulary, added for the models wave): `keysnap:<names>:<frames>` holds the same keys `keys:` does (`w a s d space ctrl noclip b g q r`, joined with `+`) for that many frames, and differs from `keys:` in exactly one way that matters: it **writes the PNG on the last held frame, before the keys are released**, where `keys:` writes nothing at all (it sets `_captureWritesPng = false` and releases at the step end). That is why a held pose - the moonwalk, a strafe or a backward walk - could not be captured before: every PNG was taken after the release, with the character standing still. The frames budget is the same `1..2000`, and the status line printed immediately after the write (`status keysnap wrote the png, keys still held`) reports the keys and the resolved forward/strafe axes the written frame was rendered with, so a capture carries its own proof that the pose was held. Use `keys:` when the point is to move the character and `keysnap:` when the point is to photograph it moving.
- `status` now also logs `actorbox <x0>,<y0>,<x1>,<y1>`: the local player's projected screen bbox, in window pixels, top-left origin, `x1`/`y1` exclusive. It is the player model's authored bounds under the same model matrix the renderer builds (`PlayerRenderState.Create`: authored extent x `VisualScale`, feet at the body centre plus `BodyCenterToFootOffset`, rotated by the yaw the facing resolves to), clamped to the viewport and to the top edge of the HUD bar, which is opaque. Two limits are stated rather than hidden: the authored extent is applied centred on the position, so an off-centre arm can fall a few pixels outside the box, and the pose layers (a swung leg, a raised arm) are not in it. When there is no figure to measure the line is `actorbox none: <reason>` and carries no numbers - first person (the local player is not drawn), no world camera drawn yet, the player behind the camera, or nothing of the figure on screen above the bar. A missing box is a loud failure; a clamped-to-nothing box would be a silently wrong measurement. The same line is also logged for every frame the capture writes, immediately above that frame's `wrote <path>` line, so a crop can be tied to one PNG without guessing which `status` step it came from. `python tools/ui-assets/tests/check_look.py <frame.png> --actor-from-log <out.log>` reads the box logged for that frame (the last one before its `wrote` line; the last box in the log when the frame is not named there) and uses it as the A3 crop instead of a hand-typed one, and refuses to measure when that line is a `none:` refusal. Add `--open-ground` for the open-ground frame, which is reported with no floor. Naming a frame the log never wrote raises instead of falling back to the last box in the log, which would be another step's pose. Example: `play,keys:g:3,walkto:25:25:1500,aim:1000:520,status,keysnap:a:90,status,snap`.
- `status` also logs `actorclip <clip> mode <mode> playbackSpeed <s> rawDeltaDeg <d> residualDeg <r> airTuck <t> hitFlinch <f> dead <bool>`: the animation clip `PlayerAnimationStateMachine` actually selected for that frame, with the raw travel-minus-facing delta it was selected from and the residual twist handed to the pose layers - contract C2's table, measured instead of derived. Without it the selected clip appears nowhere in a capture, and a state machine that computed the right sector and then played the wrong clip would look identical in the log. `actorclip none: <reason>` when the local player has not been drawn (first person, or the model is not uploaded yet).
- While `DREAM_HUD_CAPTURE` is set the window is created without taking focus (`StartFocused` off), so a capture run does not pull focus from whatever else is on screen; with it unset the window starts focused as before.

#### Capture traps

Every one of these presents as the *same* symptom - the run writes no PNG, hangs until the timeout, and its last stdout line is the harmless `[startup] GL debug output: high and medium severity`. Each has cost this project real time, so check them in this order before suspecting the script:

- **Only one `dream.exe` may run at a time.** The first process locks the world; every later one hangs to its timeout. Captures must be strictly serial - never launch two in parallel, and kill leftovers (`Get-Process dream | Stop-Process`) before the next run.
- **The local server's startup errors go to stderr, not stdout.** `[startup] local server not started: World '1' is already in use` (another `dream.exe` holds it) and `World '7' does not exist` both land in the redirected stderr stream, so a run whose stdout looks fine can still have no server to connect to. Always read the stderr tail of a failed capture.
- **With a local server `DREAM_CLIENT_NAME` is ignored.** `RemoteClientOptions.TryResolveFromEnvironment` returns false unless `DREAM_SERVER_HOST` is set, so the character name always comes from `config/desktop-client.txt` (`player_name=dream-client`). The first run to claim that name owns it, and any later run with a **fresh** `DREAM_IDENTITY_FILE` is rejected with `[session] ended: Join rejected: That character belongs to someone else. Choose another name.` - and that line is in **stdout**, not stderr. Share one identity file across runs.
- **No colons in any filename**, `DREAM_IDENTITY_FILE` included. Colons are illegal in Windows paths and the failure shows up as a silent auto-connect hang rather than an error. (The capture writer already rewrites `:` as `-` in the PNG names it generates; the variables you set yourself are on you.)
- Builds for a capture need an absolute Windows `BaseOutputPath` **inside the worktree being captured**, set from PowerShell; a POSIX shell mangles the backslashes and MSBuild rejects the resulting `/p:`. Running a capture against another worktree's binaries is how a gate ends up measuring the wrong build.

## Profiling Environment Flags

For repeatable local profiling, set `DREAM_AUTO_CONNECT=1` before launching the desktop client. The startup scene waits for the integrated loopback server, applies its current local endpoint, and begins the same connection flow as the Connect button without requiring UI input.

Set `DREAM_MAX_FPS` to override the `max_fps` frame cap for one run without writing it back to `config/desktop-client.txt`. It uses the same rules: `0` means uncapped and other integers are clamped to `30`-`500`. A non-integer value is ignored and the config value is used.

Set `DREAM_MSAA` to override the `msaa` sample count for one run without writing it back to `config/desktop-client.txt`. Values snap to `0`, `2` or `4`; a non-integer value is ignored and the config value is used.

Set `DREAM_RENDER_PERF_LOG=1` to print one render-performance line per second while in gameplay. The log includes render-export time, physics time, static chunk signature/extract/plan/sync/draw timings, static chunk cache reuse, visible chunk/triangle counts, and collision-body count.

Set `DREAM_NET_DIAGNOSTICS=1` to enable rate-limited multiplayer visibility and timing diagnostics. The log prints to the console and appends to `logs/network-diagnostics.log` under the application output folder by default. Set `DREAM_NET_DIAGNOSTICS_PATH` to write the diagnostics file somewhere else. This is intended for multi-client debugging: it records connect/join facts, session and actor snapshots, snapshot send/receive timing, client presenter state, remote-player draw counts, and slow command-send warnings.

## Client Connection And Diagnostics Variables

These are read by the desktop client (`RemoteClientOptions`, `PlayScene`) and by a few diagnostics switches. Unset means the default behaviour.

| Variable | Read in | Effect |
| --- | --- | --- |
| `DREAM_SERVER_HOST` | `RemoteClientOptions.TryResolveFromEnvironment` (called from `Game`) | Host the startup form is prefilled with; `DREAM_AUTO_CONNECT` then connects to it instead of the integrated local server. When it is unset or blank the other client variables are not read. |
| `DREAM_SERVER_PORT` | same | Port for the prefill (default `ServerBootstrap.DefaultPort`, 5057); a non-integer value throws at startup. It is client-only: the dedicated server reads `DREAM_PORT` (see Dedicated Server Settings), never this. |
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
| Same-account takeover (hosts with accounts: dedicated and integrated) | none | immediate | A join by the account that owns a character takes the seat from a live or silent holder of that character or session id at once: the old connection is released (its profile saved) and closed, and the server first sends it one `JoinRejected` (`OpenedElsewhereRejectionReason`, "This character was opened somewhere else."), which the old client shows as its disconnect reason (`RemoteClientSession.DisconnectReason`, set from a `JoinRejected` arriving after the join settled) instead of the generic lost-connection line. The close runs on a tracked background task, so a dead old peer cannot hold the tick. Another account never takes a seat: a held session id gives `SessionInUseRejectionReason`, stale or not (the 10 s idle release frees it), and a held character gives `CharacterOwned`, or `CharacterInUse` when it is unowned. |
| Stale-session takeover (only hosts without accounts: tests and tools) | `DedicatedServerHost.TakeoverSilence` | 8 s (same session id or same name) | A join for a session id, or a player name, that another connection holds takes it over when that connection has sent no frame (keepalives included) for the window: the old connection is released (its profile saved), the new one gets the seat and the saved profile, and the service closes the old transport. One window covers both matches: the desktop client picks a fresh `client-<guid>` on every connect, so its crash rejoin is always a name match, and session ids are broadcast in snapshots, so a shorter session-id window would only let a hijacker skip the name window. 8 s is far past a healthy idle client's keepalive gaps (up to about 1.5 s) and still under the 10 s idle release. A holder heard from inside the window keeps it; the join is rejected with `SessionInUseRejectionReason` (same session id) or the duplicate-display-name reason (same name). Silence is measured up to the service's last per-tick frame-count sweep, so a tick stall does not make idle clients look silent. Uses the connection options' clock. |
| Join throttle | `ServerRoutingContext.JoinThrottleInitialInterval`, `JoinThrottleMaxInterval`; `JoinThrottleTable.MaxEntries` | 2 s, doubling to 10 s; 1024 entries | Only starts after a live holder rejects a join, or after an ownership rejection (`CharacterOwned`), which needs no live holder. The rejected token retrying within the interval is told to retry in a few seconds, even though nobody may be connected. Keyed by source, account and contested session id or player name, so a spammer on the owner's address never throttles the owner: the source is the remote IP address, parsed from the connection id, which the TCP transport sets to the socket's `ip:port` remote endpoint (IPv4-mapped IPv6 folded to IPv4); an id that is not an IP endpoint (in-memory and test transports) is its own source. Further joins for that session id or name from that source are refused until the interval passes, on any connection, so reconnecting does not reset it; each further live-holder rejection doubles it, and an accepted join from the source clears it. A refused join is never claimed or queued, so it costs no persisted-player prefetch. A fresh connection gets one `JoinRejected` (`JoinThrottledRejectionReason`, with the seconds left); a connection already answered gets nothing (logged once per interval as `server.join.throttled`). Clients sharing an address (NAT, a loopback test server) with different names never throttle each other, and a spammer cannot lock the holder out of its own name from the holder's address. An entry is forgotten 10 s after its interval ends; past 1024 entries the one whose interval ended earliest is evicted. |
| Connection cap | `ServerConnectionOptions.MaxConnections`; `DREAM_MAX_CONNECTIONS` | 64 | Caps simultaneously open connections, on the dedicated and the integrated server. A connection accepted while the cap is reached gets one best-effort `JoinRejected` ("Server is full.") and is closed at once, before it gets a connection context, and logged as `server.accept.refused`. The variable takes a positive integer; any other value keeps the default, with a line on standard error. On the dedicated server, when `DREAM_MAX_PLAYERS` is at or above the cap and `DREAM_MAX_CONNECTIONS` is not set, the effective cap is `DREAM_MAX_PLAYERS` + 8 (headroom for joins in flight and a returning player's old and new connection), logged at startup; an explicit `DREAM_MAX_CONNECTIONS` is kept, with a warning and the effective number. |
| Join deadline | `ServerConnectionOptions.JoinDeadline` | 15 s | Closes a connection that is still unjoined this long after it was accepted, however many keepalives or empty frames it sends, so unjoined connections cannot hold `MaxConnections` slots. Swept next to the other liveness checks on the tick, on the connection options' clock. Off at zero. |
| Repeat-join limit | `DedicatedServerHost.RepeatJoinInterval` | 5 s | A joined connection that sends another join is answered at most once per interval; extras are dropped unanswered. |
| Server send stall | `ServerConnectionOptions.SendStallTimeout` | 5 s plus frame size at 256 KB/s | Closes a connection whose current socket write does not finish in time. Off on the integrated server. |
| Client keepalive | `RemoteClientSession.KeepaliveInterval` | 1 s | When no frame has gone out for this long (a frozen game loop), the send loop writes one empty frame. The server counts it as liveness and skips it. |
| New-account limit | `NewAccountLimiter`; `DREAM_NEW_ACCOUNT_BURST`, `DREAM_NEW_ACCOUNTS_PER_MINUTE` | burst 5, then 3 a minute, per source address | A join whose token owns no character yet (it creates the account) counts against its source address: the first 5 are free, then one more every 20 s (3 a minute). Past that the join is refused with `NewAccountRejectionReason` and the seconds to wait. Without it, new tokens bypass the 5-characters-per-token cap. Accounts that already own a character are never limited, and loopback sources (`127.0.0.1`, `::1`: local play, the perf harness, tests) are exempt. The count is kept only after a join is accepted. Memory only (no database read on the tick); the table holds at most 1024 sources, dropping full buckets first, then the least recently used. `DREAM_NEW_ACCOUNT_BURST` and `DREAM_NEW_ACCOUNTS_PER_MINUTE` set the two numbers (non-negative integers, an invalid value keeps the default); `0` for either turns the limit off. Behind a NAT or proxy, everyone shares one address. |
| Same character in two worlds | none | n/a | Profiles are global (one row per character) while two worlds can be hosted at once. The same character live in two worlds at the same time is last-save-wins on the global fields (level, experience, stats, hit points), so progress made in one of them can be lost when the other saves. A restore takes the stored row over its own cached copy only when the row is newer and the cached copy was saved before the join's load began. |
| Client server-silence watchdog | `RemoteClientSession.ServerSilenceTimeout` | 8 s | A joined session that receives no inbound bytes for this long ends with `ServerSilenceDisconnectReason`. Any bytes read reset it, including part of a large frame still arriving, so a slow multi-megabyte world transfer does not trip it. Outbound keepalives do not reset it. |
| Client join timeout | `RemoteClientSession.JoinTimeout` | 45 s | Join retries (`MaxJoinRetries`, 3, from 1 s doubling) for a join the server could not load in time. |

**Identity limits.** An account is a random token kept on the player's computer, not a login (decision U10). The token travels in plain text in the join request, so anyone who can capture the traffic gets it; the server stores only its SHA-256. A session id is a fresh `client-<guid>` per connect, is broadcast to every client and grants nothing. A token file that is lost means a new account: an admin frees the old characters with `--characters release`.

None of these have an environment override. When a session ends for any reason, the desktop client goes back to the connection screen and shows the reason (see `docs/architecture.md`, Startup Flow).

## Dedicated Server Settings

| Variable | Default | Effect |
| --- | --- | --- |
| `DREAM_PORT` | `5057` | TCP port of the dedicated server, 1 to 65535. A value that is set and is not a port stops the start with exit code 9 and a line on standard error. |
| `DREAM_BIND_ADDRESS` | `0.0.0.0` | The interface address to listen on, IPv4 or IPv6 (`127.0.0.1` keeps the server on this computer). A value that is set and is not an address stops the start with exit code 9. |
| `DREAM_MAX_PLAYERS` | `64` | Players joined at once. A join from a connection with no seat, while every seat is taken (joins waiting for the next tick count), is answered "Server is full." and the connection stays open. A value that is not a positive integer keeps the default, with a line on standard error. A join whose account or character already holds a seat (or a leftover claim) is not counted against the cap, so a player can always take back their own seat. Separate from `DREAM_MAX_CONNECTIONS`, which caps open sockets (raised to this value + 8 when it would otherwise be lower, unless set explicitly). |
| `DREAM_MOTD` | none | One System chat line sent to each player right after the join reply. Control and bidi characters are stripped and it is cut at 256 characters. |
| `DREAM_PARTY_CUBES` | off | `1` lets an online party mate damage the owner's cubes. Offline owners are never reachable. |

The dedicated server binds every network interface unless `DREAM_BIND_ADDRESS` says otherwise; the start-up line `Server listening on <address>:<port>` names what it bound, and says so when that is all interfaces. The integrated server of the desktop client always binds loopback and ignores these variables. `DREAM_SERVER_PORT` is a client variable and the server never reads it.

Exit codes of the server: 3 schema too new, 4 world not found, 5 world cannot be opened, 6 database integrity, 7 world held, 8 content invalid, 9 invalid `DREAM_PORT` or `DREAM_BIND_ADDRESS`.

### Character presence

A character is played in one world at a time. Joining takes a lease on the character (`character_presence`, schema 14) for a character the joining account owns; the server renews it every 15 s while the player is in the world and drops it when they leave or the server stops. A lease expires 45 s after its last renewal, so a crash frees the character in under a minute. The same world takes its own lease back at once. Another world's join of the character is refused with "That character is being played in another world right now." A server without accounts takes no leases.

### Owner-only progress

Other players see a level and nothing else. Actor snapshots on the wire carry the public level and hide experience and base stats; the owner receives their own as `server.progress`, at most every 16 ticks. Server state and checksums are not affected.

### Reconnecting

A client whose session ends with `Connection lost: ...` returns to the connection screen and retries on its own, after 1, 2, 4, 8 and 16 s ("Reconnecting (n/5)..."). A rejection ends the retries at once (a kick, a ban, a full server), as do pressing Connect, Use Local Server or Esc. A retry is a full rejoin: a party does not survive it. A session on the game's own integrated server is not retried; connecting there starts a new server as before.

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
| `DREAM_DEV_COMMANDS` | `ServerBootstrap.CreateRuntimeHost` (sets `RuntimeAuthoritativeSessionHost.DevCommandsAllowed`) | `1` lets clients use noclip, freeze and the stat sliders. Default off on the dedicated server: `PlayerCommandHandler` clears the noclip and freeze flags and `PlayerActionHandler` rejects `PlayerStatsUpdateIntent`. The integrated local server turns it on itself. A client also stops predicting noclip and freeze unless it is on its own integrated server (a ready local server, a loopback host and the local server's bound port) or has `DREAM_DEV_COMMANDS=1` in its own environment; `admin_hud` does not matter. |
| `DREAM_PVP` | `ServerBootstrap.CreateRuntimeHost` (sets `WorldSessionRuntime.PvpEnabled`, so the dedicated and the integrated server) | `1` lets players damage each other with the primary attack, Cleave and Bolt. Any other value, or no value, is off, and with it off no player is ever a target. On a ray an enemy takes priority over a player. A player who is dead or under spawn protection is skipped, and PvP gives no XP, loot or gold. Placed cubes stay owner-only whatever this says. Not persisted: one server process hosts one world, so this is per world in practice. Server-only. |
| `DREAM_DAY_LENGTH_MINUTES` | `ServerBootstrap.ResolveDayLengthMinutes` (called from `ServerBootstrap.CreateRuntimeHost`, so the dedicated and the integrated server) | Real-time minutes per in-game day. Default 30 (115,200 ticks at 64 ticks/s; a new world starts at 08:00). Decimals are accepted; values outside 1–1440 are clamped and an unparsable value falls back to 30, each with a line on standard error. Not persisted: the world keeps its `world_time_ticks` (a column of its `worlds` row), so changing the day length between runs moves the time of day once. Server-only; clients receive it in the join-accepted world clock anchor. |
| `DREAM_NIGHT_BRIGHTNESS` | `SkyModelSettings.FromEnvironment` (desktop client) | Up-facing light at night as a fraction of noon's. Default 0.07 (0.15 before wave T2), clamped to 0–1; an unparsable value uses the default. Client-only presentation; it changes no simulation state. Torches and lamps (point lights) are what make the night readable. |
| `DREAM_TERRAIN_VARIATION` | `TerrainSurfaceSettings.FromEnvironment` (desktop client, `Render.Terrain`) | `0` or `1`, default `1`. `1` makes the terrain program (`cubeFragment.glsl` compiled with `DREAM_TERRAIN`) sample the ground tile stochastically and apply a world-space macro variation (average about 0.95, so slightly darker); `0` is one plain tile sample, the previous look (see `docs/rendering-and-assets.md`, Terrain surface). Set per pass as the `uTerrainVariation` uniform. Visual only; there is no in-game toggle. An invalid value prints one line to stderr and keeps the default. |
| `DREAM_TERRAIN_BLEND` | `TerrainSurfaceSettings.FromEnvironment` (desktop client, `Render.Terrain`) | `0` or `1`, default `1`. `1` lets the terrain program blend different materials at block edges on top faces (within about 0.35 m of a cell edge the tiles interlock along their relief height, so mud and moss sit in the crevices of cobbles) using the world material map and the 8-layer tile array; `0` keeps hard material edges. Forced to `0` when `DREAM_TERRAIN_VARIATION=0`, and off until the tile array has finished loading (or for good if a tile fails to load). Set per pass as `uTerrainBlend`. Visual only. An invalid value prints one line to stderr and keeps the default. |
| `DREAM_TERRAIN_RELIEF` | `TerrainSurfaceSettings.FromEnvironment` (desktop client, `Render.Terrain`) | `0` or `1`, default `1`. `1` perturbs the terrain normal from the tile relief height (alpha) on top and side faces, so sun, moon, torch and point lights catch the crevices; `0` is the flat normal. Forced to `0` when `DREAM_TERRAIN_VARIATION=0`. Set per pass as `uTerrainRelief` (strength 0.025 m, 0 when off). Visual only. An invalid value prints one line to stderr and keeps the default. |
| `DREAM_BOUNDARY_MIST` | `BoundaryMistSettings.FromEnvironment` (desktop client) | `0` or `1`, default `1`. The smoke wall that hides everything past the map edge (see `docs/rendering-and-assets.md`, Boundary mist). `0` skips both the per-fragment mist and the void pass. Visual only; there is no in-game toggle. An invalid value prints one line to stderr and keeps the default. |
| `DREAM_WAR_MIST` | `WarMistSettings.FromEnvironment` (desktop client) | `0` or `1`, default `1`. The translucent drifting veil around the player (clear within 3.75 m, full by 8 m), isometric view only (see `docs/rendering-and-assets.md`, War mist). Sets the starting value of `GameSettings.WarMistEnabled`, which is the only runtime gate (the veil still builds the exploration memory while it is off, and the checkbox works with `0`); the Settings window's "War Mist" checkbox (developer controls) toggles it at runtime, not persisted. Visual only. An invalid value prints one stderr line and keeps the default. |
| `DREAM_WAR_MIST_EXPLORED` | `WarMistSettings.FromEnvironment` (desktop client) | `none` (default) or `all`. `all` treats the whole map as already explored, so only the lighter explored veil (density 0.58) shows beyond the clear radius (for captures). An invalid value prints one stderr line and keeps `none`. |
| `DREAM_WAR_MIST_SLICES` | `WarMistSettings.FromEnvironment` (desktop client) | Number of stacked horizontal slices, `2` to `8`, default `6`. The per-slice alpha is chosen so the stack always composes to the same density. An invalid value prints one stderr line and keeps `6`. |
| `DREAM_DEBUG_POINT_LIGHTS` | desktop client (`Render.PointLights`) | Adds a deterministic ring of N synthetic point lights around the camera, for the perf harness. Default 0 (off), clamped to 0–64. Client-only debug; it changes no simulation state. At most 16 lights reach the shaders per frame, so values above 16 measure the collection and selection cost only. |
| `DREAM_WORLD_PROPS` | desktop client (`PropRenderer`) | `0` switches the placed scenery off (the hamlet, the braziers' bowls, the graves and the scatter), for A/B captures against the bare terrain. Anything else, or unset, draws it. The props are client-visual only: they are derived from the replicated static content, nothing collides with them and no simulation state reads them. |
| `DREAM_GROUND_SCATTER` | desktop client (`GroundScatterRenderer`) | `0` switches the ground assets off (floor decals, flora, stones and debris, from the manifests under `dream/animations/ground`), for A/B captures against the bare floor. Anything else, or unset, draws them; it is not gated by `DREAM_WORLD_PROPS`. Placement is a pure function of the replicated static ground, so every client sees the same scatter; nothing collides with it and no simulation state reads it. Read once at start. |

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

**Content check at start.** Every dedicated or integrated start reads `config/content/items.json`, `enemies.json`, `loot.json` and the optional `skills.json` once (`ContentCatalog.Shipped`), before it opens the database, and logs `server.content.digest digest=<sha256> items=<n> enemies=<n>`. Two servers with different digests run different content. A missing or invalid file stops a dedicated server with exit code **8** and the log line `Server not started: the game content is invalid: <reason>`; an integrated server shows the reason on the connection screen. A client that only joins a remote server does not need these files: a content error blocks Use Local Server, not a remote connect.

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

### Managing accounts: `--accounts`

```
dream --accounts grant-admin <name>    the account that owns the character becomes an administrator
dream --accounts revoke-admin <name>   back to a player
dream --accounts unban <name>          clears the ban of that account
```

`<name>` is a character the account owns (display name, ignoring ASCII case). Administrator is granted here and nowhere else; no chat line or client message can set it. All three work while a server runs. The server reads the role when a player joins, so an administrator who is online keeps the role they joined with until they rejoin, and a ban set here is read on the next join. Output is `<command>` and the name, tab-separated.

Exit codes: 0 done, 1 usage, 2 no account owns that name, 3 the database is newer than this build, 7 the database is busy.

### Server commands (administrators)

An administrator types these in chat. The server alone decides: the line travels as a `Command` chat line and is honoured only when the connection joined with the administrator role; anyone else gets "You do not have permission."

| Command | Effect |
| --- | --- |
| `/kick <name> [reason]` | Closes the player's connection with the reason. They may rejoin. |
| `/ban <name> [minutes] [reason]` | Bans the account that owns the character and kicks it if it is online. No minutes means permanent. The player sees the reason on every refused join. Works on offline characters. |
| `/unban <name>` | Lifts the ban. |
| `/mute <name> [minutes]` | The player's chat lines are refused with "You are muted." No minutes means permanent. |
| `/unmute <name>` | Lifts the mute. |
| `/announce <text>` | A System line to every joined player. |

Put a name with spaces in double quotes. A ban or mute belongs to the account, not the character, and holds in every world of the database and across restarts (`accounts.banned_until_utc`, `ban_reason`, `muted_until_utc`, schema 14). An administrator cannot sanction themselves or another administrator who is online. Every command is logged as `server.admin` with the administrator, the action and the target; tokens are never logged. A ban made by a new token is a new account: there are no address bans.

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

Weights are 0..1,000,000 with an int total and at least one above 0. Duplicates and unknown properties fail at load. The killing blow takes the drop. A dropped item goes to the first spot of the bag its footprint fits, and when it fits nowhere it is lost with a notice, even with free cells showing. A dropped item that fits nowhere in the bag goes to the ground instead, as a ground item that the killer alone may pick up for a short while and that despawns after a few minutes. Gold is credited directly and never lies on the ground.

### Enemies: `config/content/enemies.json` (version 2)

`strength`, `vitality`, `attackSpeed`, `level`, `visualKey` and `spawnRole` are required. `attackIntervalSeconds` and `lootTable` are optional. Strength and Vitality are 1..50, Attack Speed 0.5..5, level 1 or more. Damage, health and interval come from the `enemy.*` formulas in `combat_formulas` (schema 9). `spawnRole` is `Primary` or `NightExtra`, and at least one `Primary` is required. A spawn copies its stats into its row, so an edit reaches new spawns only, never an enemy already stored.

### Skills and quickbar: `config/content/skills.json` (version 1)

Optional: a content directory without the file has no skills. It is read at start with the other content files and is part of the content digest, so editing it needs a restart.

| Field | Meaning |
| --- | --- |
| `key`, `displayName`, `description`, `iconKey` | Required text. `iconKey` falls back to two-letter initials on the quickbar until art exists. |
| `kind` | `MeleeArc`, `RangedBolt` or `SelfHeal`. |
| `manaCost` | 0..1000. Required. |
| `cooldownTicks` | 0..64000, at 64 ticks a second. Required. |
| `range` | 0..64. `MeleeArc` and `RangedBolt` need it above 0. |
| `arcDegrees` | 0..360. `MeleeArc` needs it above 0. |
| `maxTargets` | 1..16. |
| `damagePercent` | 0..1000, a percentage of the player's attack damage. `MeleeArc` and `RangedBolt` need it above 0. |
| `healAmount` | 0..10000. `SelfHeal` needs it above 0. |

`defaultQuickbar` holds at most 6 entries, for keys 1 to 6: `{"kind":"Skill","key":...}`, `{"kind":"Item","key":...}` or `{"kind":"None"}`. A missing skill, or an item with no `use`, stops the load. The bar is per character and per world (`player_quickbar_slots`, schema 13): a character who never bound a slot has no rows and sees this default, and the first bind stores all 6 slots.

An item is a consumable when `items.json` gives it a `"use"` object: `{"restoreHealth":50,"restoreMana":0,"cooldownTicks":320}`. Each kind has its own cooldown. Using an item costs an inventory action from the budget and, unlike the other inventory actions, is refused while dead, as are skills; a potion with nothing to restore is refused and kept.

### Quests: `config/content/quests.json` (version 1)

Optional: a content directory without the file has no quests. It is read at start with the other content files, is part of the content digest, and an edit needs a restart. A quest giver must be an NPC in `npcs.json`; the shipped giver is `gate-warden`.

```json
{"version":1,"quests":[{"key":"wolf-cull","title":"Thin the Pack","description":"...","giver":"gate-warden",
  "objectives":[{"kind":"Kill","target":"Wolf","count":2}],
  "rewards":{"xp":100,"gold":40,"items":[{"item":"potion.health","count":1}]}}]}
```

| Field | Meaning |
| --- | --- |
| `key` | Unique text, the id the progress is saved under. |
| `title`, `description` | Shown in the dialogue and the tracker. |
| `giver` | The NPC key that offers and takes the quest. Unknown NPC stops the load. |
| `objectives` | 1 to 4. `Kill` names an enemy kind from `enemies.json`; `Collect` names an item from `items.json` and counts the bag, consuming the items at turn-in (worn items do not count). `count` is 1..99. |
| `rewards` | `xp` and `gold`, and `items`: fixed-stat items only (a rolled item stops the load), each with a `count`. The turn-in is refused with a full bag. |

At most 32 quests. Quest state is per character and per world (`player_quests`, schema 14). A reward is paid once: the completion and its rewards are one write, and a failed write leaves the quest open.

### Mana

Mana is per world like health, in `player_world_state.current_energy`. The rules are code, not settings (`PlayerManaRules`): the maximum is `50 + 5 * (level - 1)`, and a living character regains `1 + (level - 1) / 10` every 32 ticks. Items cannot raise it. Respawning refills it. The server sends the owner `server.vitals` at most every 16 ticks, and only when something changed, so the HUD can trail by up to 250 ms. Cooldowns live on the session, so a reconnect clears them.

Placed cubes can be damaged only by the character who placed them; terrain and unowned cubes can be damaged by anyone. This holds with `DREAM_PVP` on too, and there is no setting for it.

## Project Content Layout

The project file includes:

- Removal rules for an `animations\BlueCubeGenerator\**` subtree
- runtime asset copy rules for `shaders`, `animations`, and `fonts`

## Input And Frame Assumptions

The project currently expects:

- Keyboard input from OpenTK `KeyboardState`
- Mouse input from OpenTK `MouseState`, read once per Update as a pointer state (`Render.SampleWorldPointer`)
- A per-frame physics step driven from `OnUpdateFrame`
- Per-frame HUD drawing driven from `OnRenderFrame`

## Runtime Settings Flow

`Esc` is the settings toggle, with one exception that holds on both HUD surfaces (classic and ImGui): with the inventory open and Settings closed, `Esc` closes the inventory and leaves Settings shut (it used to open Settings over it). From play, or with Settings open, `Esc` toggles Settings as before. When settings are visible:

- the OS cursor is shown (the sprite cursor is not drawn)
- gameplay input is paused and the current click goal is cancelled
- ImGui receives live mouse input for the settings panel

When settings are hidden, gameplay input resumes. The `V` inventory overlay does not pause play: clicks outside the bar and the open windows still move the character.

### Controls

- Left click on the ground: walk there along a client-side path, in any interaction mode. Holding the button re-aims the walk at the cursor up to four times a second.
- Left click on an enemy: walk into range (3.2 m), stop, face it and attack until it is gone or a new click arrives. Asks for Combat mode once if the mode is not Combat.
- Right click, Combat mode: face the cursor point and attack while the button is held, without moving.
- Right click, Building mode: pick a cell; walk until it is within 2.4 m if it is further than 2.5 m, then place once.
- A click on the bar, an open window or an ImGui panel never moves the character, and neither does a drag that began there.
- `W`, `A`, `S`, `D`, mouse look and left-click-as-attack are gone. `Space` (jump), `Left Ctrl` (noclip descend), `F`, `0`, `1`, `2`, `V`, `Esc` and `F3` are unchanged. Clicks are ignored while noclip is on.

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
