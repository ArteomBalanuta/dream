# Dream

`dream` is a small 3D sandbox-style C# project built on OpenTK and BulletSharp. It opens a desktop window, simulates a physics-backed player and voxel grid, and renders cubes, a HUD crosshair, and a simple ImGui overlay.

## Tech Snapshot

- Language: C#
- Framework: .NET 10
- Windowing and OpenGL: OpenTK 4.9
- Physics: BulletSharp
- UI overlay: ImGui.NET
- Model loading: SharpGLTF
- Image loading: SixLabors.ImageSharp

## Prerequisites

- .NET 10 SDK
- Windows desktop environment with OpenGL support
- The repo-owned runtime asset folders included under `dream/`

The repository now includes `global.json`, so matching the pinned SDK version is recommended for the most predictable restore/build behavior.

## Build And Run

From the repository root:

```powershell
dotnet build dream\dream.csproj
dotnet run --project dream\dream.csproj
```

On Windows, `dotnet build` emits the runnable desktop executable at `dream\bin\Debug\net10.0\dream.exe`. The SDK build summary may still mention `dream.dll` because that is the managed assembly behind the executable app host.

The project currently builds successfully with warnings. The remaining package warning comes from `BulletSharp`, which is intentionally still in use and restores as an older .NET Framework-oriented package.

Startup now performs explicit mode selection. Running with no extra arguments starts the desktop client path, while `dotnet run --project dream\dream.csproj -- --server` now starts the first long-running TCP dedicated-server loop and keeps listening until interrupted.

Desktop startup now opens a primary multiplayer connection screen instead of dropping straight into gameplay. The desktop shell also starts one integrated loopback server automatically and uses that as the default local target. The older environment-variable path still works as a development prefill source through `DREAM_SERVER_HOST`, `DREAM_SERVER_PORT`, `DREAM_CLIENT_SESSION_ID`, and `DREAM_CLIENT_NAME`.

## Current State

- Targets `net10.0` (`global.json` pins the SDK). Multiplayer protocol version is 21 (`TransportEnvelope.CurrentProtocolVersion`); join and resync worlds travel as chunked transfers.
- Server: 64 Hz simulation, 32 Hz snapshots by default (`DREAM_SNAPSHOT_INTERVAL_TICKS`), frame limits of 64 KB client to server and 8 MB server to client, SQLite persistence in WAL mode with group-committed writes and incremental checkpoints.
- Client: connection screen first; a session that ends (lost connection, 8 s server-silence watchdog, rejected or timed-out join) returns to it with the reason. Keepalive, join retries and the 45 s join timeout are listed in [docs/configuration.md](docs/configuration.md), Connection liveness.
- Tests: `dotnet test dream.Tests\dream.Tests.csproj`; see [docs/build-and-run.md](docs/build-and-run.md), Testing, for the output-folder rule when a game is running, the opt-in load tests (`DREAM_LOAD_TEST=1`) and the performance harness.
- Every `DREAM_*` environment variable the code reads is documented in [docs/configuration.md](docs/configuration.md).

The HUD crosshair is now viewport-driven, so it is rebuilt from the live window size instead of from baked pixel coordinates.

## Current Controls

- `W`, `A`, `S`, `D`: move the player
- Mouse movement: look around
- `Space`: jump
- `B`: select building mode
- `G`: select combat mode
- `1` to `6`: use the quickbar slot (a skill or a consumable); while the inventory is open, click a quickbar tile to cycle what it holds
- `Q` / `R`: drink the first healing / mana potion in the bag
- `E`: pick up the nearest ground item or talk to the nearest NPC
- `Enter`: chat; hold `Tab` for the online list
- Left mouse button: perform the active mode's primary action
- `V`: toggle the inventory and character windows (drag items between bag and slots, right-click for the item menu, hover for stats)
- `0`: toggle noclip/fly mode
- `F`: toggle the frozen movement state
- `Esc`: close the chat line, an open vendor/dialogue/trade window or the inventory, otherwise toggle the pause menu

## Settings Menu

The in-game settings panel currently supports:

- field-of-view adjustment
- mouse sensitivity adjustment
- first-person / third-person camera toggle
- noclip/fly-speed adjustment
- runtime `Strength`, `Attack Speed`, and `Vitality` tuning
- runtime floating damage text size, speed, and crit-depth tuning
- preset resolution switching
- runtime floor-size resizing with an explicit apply step
- collision-geometry debug toggling for cubes and player

When the settings menu is open, gameplay movement and cube placement are paused so the UI can safely capture mouse input.

## Runtime Notes

- Source code now uses the `Dream` root namespace and folder-matched namespaces for application, client, server, multiplayer, simulation, rendering, assets, configuration, and utilities. The project remains one executable `.csproj` while these boundaries settle.
- `Game` acts as the OpenTK window shell. `PlayScene` owns the desktop client path and composes a `ClientWorldSession` (`Dream.Client.World`): the replicated map, the locally predicted player and the replicated enemies. The authoritative `WorldSessionRuntime` lives on the server side only.
- `Game` now starts on a dedicated startup connection scene, auto-starts an integrated loopback server in desktop mode, and only constructs `PlayScene` after a successful multiplayer connection.
- `Program` now validates startup arguments explicitly, defaults to the desktop path, and routes `--server` into `ServerBootstrap` instead of silently falling through.
- `StartupConnectionScene`, `StartupMenuRenderer`, `NetworkSessionCoordinator`, and `IntegratedLocalServerHandle` now provide the first product-facing multiplayer boot flow and local-host lifecycle instead of relying on environment variables as the primary connect path.
- The main game window is created by `Game` and currently starts at `1440x1024`.
- The title bar is updated with the current FPS count.
- The application grabs the mouse cursor on startup.
- `PlayScene` now keeps client-local presentation work, while `WorldSessionRuntime` owns authoritative desktop-session state such as `Map`, `PhysicsIntegrator`, `Player`, combat/build resolution, wolf upkeep, and floor rebuild behavior.
- `WorldSessionRuntime` (server side) owns the `PlayerSessionRegistry`; every joined session gets its own server-side physics player.
- `PlayScene` translates raw desktop input into small simulation-facing command records and mirrors them to the server
- `PlayScene` now captures one `PlayerCommandFrame` per local update and routes it into the authoritative runtime with monotonic sequence numbers.
- Phase 4 now adds a first protocol contract seam: versioned transport envelopes, typed client/server message families, JSON codec plus length-prefixed framing helpers, focused routers/handlers, and typed action/result payload contracts for primary-use style gameplay messages.
- `PlayScene` now owns a small `ClientProtocolSession` seam for future replicated-state intake instead of turning the scene itself into a protocol router.
- `PlayScene` now also owns a first `ClientScenePresenter` seam and remote-player interpolation foundation so authoritative remote player snapshots can be buffered separately from local camera/HUD/viewmodel state.
- Remote players render through a dedicated `RemotePlayerRenderer` world-space path as interpolated Kenney blocky character models instead of being mixed into local first-person or HUD systems.
- Player actor snapshots can now also carry per-actor interaction mode, which gives the multiplayer model a cleaner path toward per-player authoritative build/combat ownership instead of one shared mode fact forever.
- Authoritative primary-use actions now also flow through explicit `AuthoritativeActionResult` contracts, and the server action handler returns clean `ServerActionMessage` rejections when a session is not joined or no authoritative runtime is attached.
- `DedicatedServerHost`, `DedicatedServerService`, `ServerBootstrap`, `ServerConnection`, `TcpServerTransport`, and `TcpClientTransport` now form the first live multiplayer host stack: the server can listen on TCP, decode framed protocol messages, route them into the authoritative host, and broadcast snapshots to joined connections without constructing render assets.
- The headless host tick seam now drains one latest command per joined session each tick instead of only one primary-session command.
- `RemoteClientSession`, `RemoteClientOptions`, and `TcpClientConnector` now form the first live desktop-client transport seam: the client can connect to a configured TCP server, send `client.hello` and join automatically, mirror local command frames onto the wire, and drain inbound authoritative messages into the existing client protocol/presenter path.
- `ClientCorrectionApplier` and `RemoteWorldMutationApplier` now give remote mode a first authoritative intake path for the local player and world cubes: authoritative snapshots can now reposition the local player, sync facing/health, apply authoritative interaction mode, reconcile full world snapshots, and apply incremental cube mutations into the client runtime.
- `RemoteEnemyStateApplier` now mirrors authoritative enemy actor snapshots into the client runtime, and remote mode now suppresses local enemy simulation plus local movement, look, and primary-use authority while connected so the server’s shared-world state stops competing with client-local guesses.
- Authoritative action processing now resolves a per-session action context before validating one-shot gameplay actions, so additional joined sessions can already own authoritative build placement and first-pass authoritative combat resolution through their own replicated mode state.
- Joined remote-session combat no longer trusts client-provided aim rays directly; the server now resolves first-pass combat aim from authoritative actor position/facing for that session context.
- Remote `server.action` messages now flow into a first `ClientAuthoritativeActionApplier` seam so accepted authoritative world mutations and combat results can update local remote-mode state and feedback immediately instead of remaining dead protocol data until the next snapshot.
- Rejected `server.action` messages can now also apply immediate local player correction and publish an explicit rejection event, giving the client a first real rollback/UX hook instead of relying only on later snapshot convergence.
- The client now also turns those surfaced rejection events into short-lived HUD feedback and records bounded action/snapshot diagnostics with session id, sequence, and server tick metadata for remote-authority debugging.
- The server advances authoritative gameplay through a fixed 64 Hz `ServerTickRunner`/`SimulationClock` (`SimulationClock.AuthoritativeTicksPerSecond`); actor snapshots go out every second tick, so 32 Hz by default.
- `PlayScene.Render()` now consumes a runtime-provided `WorldSessionPresentationState` snapshot instead of digging through runtime internals field by field.
- The scene now spawns one first-pass wolf enemy that chases the player, attacks in melee range, and can be killed in combat mode.
- If no active wolf enemies remain during gameplay, the scene respawns the default wolf.
- World-model transform composition now respects glTF node-local transforms correctly, so authored mesh-node scale does not shove actor instances off-map.
- The shared animated-model pipeline now includes a first-pass glTF skinning path for skinned creature assets such as the wolf.
- The world now uses separate width, height, and depth bounds instead of one cubic side length.
- The floor is represented visually by solid cubes but physically by one merged static floor collider.
- Changing floor size at runtime rebuilds the square floor as `N x N`, preserves in-bounds placed cubes, and clamps the player back into valid map space if needed.
- Large-floor rendering now applies a lightweight visibility gate before per-cube drawing, reducing obviously wasted draws for distant or behind-camera cubes.
- World rendering now also queries nearby spatial sectors before running that visibility gate, so candidate iteration scales better with larger floors.
- Visible cubes that share the same animated model and shader are now batched through a shared setup path, reducing repeated per-cube model-render overhead.
- The base floor now uses a dedicated local candidate query, while the spatial index is reserved for placed cubes, reducing generic candidate overhead on larger maps.
- The player camera now uses a configurable eye-height offset instead of a fixed close-up offset.
- Local cube placement now flows through the authoritative runtime seam: `PlayerInteraction` captures a `BuildUseRequest` from plain data, `WorldSessionRuntime` validates the active mode and applies accepted placement, and the scene only reacts to the resulting presentation state.
- The HUD crosshair scales with window size and remains centered from the active viewport metrics.
- The existing top-left gameplay HUD now shows player HP, active interaction mode, and current derived damage range.
- Combat stats are now player-owned runtime values instead of a scene-local random damage roll.
- Combat mode can now damage destructible placed cubes, while the base floor remains protected in this first pass.
- Combat mode now targets enemies before destructible cubes when the crosshair ray resolves a valid wolf hit.
- Cube visuals now follow the centered unit-cube collision model instead of applying an extra visual offset.
- A debug collision overlay can render red wireframes for cube and player collision bodies from Bullet transforms.
- Focused low-harness regression tests now cover `PlayerController`, `PlayerInteraction`, `PlayerMotor`, and `PhysicsIntegrator` ownership behavior.
- Session registry and command-frame seams now also have focused low-harness coverage for monotonic command acceptance and local command-frame capture behavior.
- A runtime-presentation snapshot seam now also has focused test coverage so the client presenter can consume authoritative HUD/world state without owning it directly.
- Protocol codec, framing, routing, and client/server bootstrap seams now also have focused low-harness coverage.
- Remote-player interpolation and dedicated renderer selection now also have focused low-harness coverage.
- A loopback smoke path can now be enabled with `DREAM_LOOPBACK_REMOTE_PLAYERS=1` to feed fake remote-player snapshots into the client presenter for local rendering verification.
- Future HUD positioning work should derive placement from the live viewport rather than from hardcoded viewport pixel coordinates.
- Runtime assets are resolved from the application output layout through a shared path helper. See [docs/configuration.md](docs/configuration.md) for the current asset-loading model.
- The first enemy asset is a repo-owned animated wolf-compatible `.glb` under `dream/animations/enemies/wolf.glb`.
- Current skinned-model support is intentionally first-pass: one logical skin per asset is supported cleanly, which is enough for the current wolf milestone.
- Imported world assets can now declare authored-height and target-world-height normalization metadata so render scale is derived from explicit sizing intent instead of from opaque one-off constants.
- The repo now keeps a curated asset vault under `dream/animations/library/` for CC0 source packs and terrain textures from Kenney, Quaternius, Poly Haven, and OpenGameArt-derived sky libraries. Current library content includes Quaternius `animals`, `characters`, `fantasy-props-megakit`, `stylized-nature-megakit`, and `survival-pack`; Kenney `animated-characters-retro`, `food-kit`, and `nature-kit`; the Poly Haven terrain texture set used by the world-material pipeline; and `skyboxes/cloudy-skyboxes` with CC0 cubemap and panorama skies covering sunny, sunset, and night variants.

## Project Layout

- `dream/`: main project source, shaders, fonts, and assets
- `dream.Tests/`: focused xUnit coverage for low-harness gameplay and runtime seams
- `docs/`: project documentation
- `docs/ledger/`: analysis, planning, decisions, and work records for ongoing development

## Further Reading

- [Build and run guide](docs/build-and-run.md)
- [Project overview](docs/overview.md)
- [Architecture](docs/architecture.md)
- [Project structure](docs/project-structure.md)
- [Configuration](docs/configuration.md)
- [Rendering and assets](docs/rendering-and-assets.md)
- [Performance and optimizations](docs/performance-and-optimizations.md)
