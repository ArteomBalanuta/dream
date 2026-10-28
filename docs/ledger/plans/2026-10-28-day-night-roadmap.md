# World Time, Sky and Day/Night: Roadmap

Purpose: plan a world clock, a sky with a sun and a moon, and a day/night cycle whose nights are really dark, plus the light sources that make dark nights playable.

Status: proposal. Analysed on `integrate/engine-iteration` at `f499299`. Nothing has been implemented yet.

## Decisions Taken

These were agreed in chat on 2026-10-28:

- Nights are dark. Without a light source the player sees little more than silhouettes and the sky.
- No offline time. The clock only advances while the world's server is running.
- No skipping the night: no sleeping or voting. A debug-only time control for development is fine.
- No weather and no seasons.

## Where Things Stand

- **Time.** There is no notion of game time. `WorldSessionRuntime.CurrentServerTick` (`ulong`) counts ticks since the process started, and nothing about time is persisted.
- **Lighting.** It is hard-coded in each shader, and the shaders disagree:
  - `cubeFragment.glsl` uses a point light at (4, 2, 2) with ambient 0.2.
  - `enemyFragment.glsl` uses a point light at (4, 4, 2) with ambient 0.65.
  - `fragmentShader.glsl` uses a flat color.

  No shader has a sun direction, a fog, or a light that changes over time.
- **Sky.** There is none. `PlayScene.Render.cs` clears to a fixed color.
- **Join.** `JoinAcceptedMessage` already carries `CurrentServerTick`, and clients track the server tick. A clock anchor can therefore be a few numbers on that same message.
- **Persistence.** The `world_metadata` key/value table exists (it holds the simulation seed today). It can hold the world time until the `worlds` table from the persistence roadmap exists.

## Design

### World clock (simulation)

- `WorldClock` holds `WorldTicks` (`long`): ticks the world has run, in total, since it was created. It advances by one per server tick. It never reads the system clock, so it is deterministic and survives replays.
- `TicksPerDay` is a per-world setting. The default is 30 real minutes per day (115,200 ticks at 64 Hz).
- `StartOffset` is applied once, at creation: a new world starts at 08:00.
- Time of day is `(WorldTicks + StartOffset) % TicksPerDay` as a fraction 0..1. Day number is `(WorldTicks + StartOffset) / TicksPerDay`.
- Phases with fixed bounds: night, dawn, day, dusk. `PhaseChanged` events let gameplay react later.
- Persistence: `world_time_ticks` is stored in `world_metadata` and written by the regular checkpoint and on shutdown. It moves to the `worlds` row when that table exists.

### Clock replication (network)

- `JoinAcceptedMessage` gains a `WorldClockAnchor`: `WorldTicks` at `CurrentServerTick`, `TicksPerDay` and `StartOffset`. This needs a protocol bump to v5.
- The client derives the time each frame from its own synced server tick and the render interpolation fraction. This costs 0 bytes per snapshot.
- A clock change (the debug time control) sends one `server.world-clock` message with a new anchor.

### Shared lighting (client)

- `FrameLighting`, one uniform buffer per frame:
  - sun direction, color and intensity;
  - moon direction and intensity;
  - ambient sky and ambient ground colors (hemisphere ambient);
  - fog color and density;
  - exposure.
- All world shaders read it: cube and terrain, enemy, models and view models. Each shader's hard-coded light is removed. Before any of this, the current noon look is captured as a reference and matched, so step 1 does not change how the game looks.
- `SkyModel` is a pure function from time of day to `FrameLighting` plus sky colors. Its keyframes (night, dawn, noon, dusk) are interpolated smoothly. It is unit-tested and has no GL.
- **Dark nights.**
  - The sun's light goes to 0.
  - The moon gives a faint cool directional light.
  - Ambient drops to about 2–4% of noon.
  - Fog turns to the dark horizon color, so distant terrain fades into the night.
  - The HUD and view model stay readable: the HUD is unaffected, and the view model gets a small floor.

### Sky (client)

- **Sky pass.** One full-screen triangle drawn at maximum depth after the opaque world. It draws:
  - a gradient from horizon to zenith;
  - a sun disc and glow;
  - a moon disc;
  - stars, faded in at night: hashed points on the sphere, with no texture needed.

  It replaces the clear color.
- **Fog.** Distance fog in every world shader, in the horizon color. This also hides the map's edge.

### Light sources

Dark nights need them.

- **Point lights.** A `LightList` uniform buffer holds the N nearest lights to the camera (default 16), each with position, color, radius and intensity, using smooth falloff. They are forward-shaded in the world shaders, and lights outside the view frustum are culled on the CPU.
- **Held torch.**
  - A torch item. When equipped, a light follows the player's hand.
  - Remote players' torches use the equip state that is already replicated.
  - It needs a torch view model and an item definition.
- **Placed light.**
  - A lamp material (`GeometryMaterialId.Lamp`) that glows (emissive) and adds a point light at its cube.
  - Placed lamps persist as placed cubes with no schema change. They also get the world-object id work for free later.
- **Enemies at night (optional, small).** More wolves at night, deterministic from the clock and the seed.

### Not in this roadmap

- Shadows from the sun (a shadow map). This is its own project. Night darkness does not need it.
- Voxel light propagation (light spreading through cells). Point lights cover dark nights for now; propagation can come later if many lamps are placed.
- Weather, seasons, offline time and skipping the night (per the decisions).

## Roadmap

### Wave T1: Clock, Shared Lighting, Sky (5 workstreams)

The architect fixes the shared contracts first: `WorldClockAnchor`, the `FrameLighting` layout, and the `SkyModel` output.

| ID | Workstream | Owns |
| --- | --- | --- |
| T1a | `WorldClock` in Simulation: ticks it advances, phases, persistence in `world_metadata` (checkpoint and shutdown), config `DREAM_DAY_LENGTH_MINUTES`. Tests: advancing, restart continuity, no time passes while the server is off. | `Simulation/World/WorldClock*`, runtime tick hook, metadata repository |
| T1b | Clock replication: the anchor on join-accepted, protocol v5, `server.world-clock`, `ClientWorldClock`. Tests: client and server agree within one frame, including after a resync and a reconnect. | `Multiplayer/Protocol/*`, client replication |
| T1c | Shared lighting: the `FrameLighting` uniform buffer, all world shaders moved onto it, hard-coded lights removed. Fixed noon is matched to today's look. | `shaders/*`, `Render.Shaders.cs`, lighting binding |
| T1d | `SkyModel`: time of day to lighting and sky colors, with keyframes and dark-night values. Pure, unit-tested. | `Rendering/Sky/SkyModel*` |
| T1e | Sky pass and fog: the gradient, sun, moon and stars, and fog in the world shaders' shared include. A debug time slider in the dev overlay (ImGui). | `Rendering/Sky/SkyRenderer*`, sky shaders, `PlayScene.Render.cs` |

**Done when:**
- A world's time survives restarts.
- All clients see the same sky.
- A full day cycles visibly.
- Night is dark.
- Tick, frame and allocation budgets are unchanged (0 B per frame stays).

### Wave T2: Light In The Dark (5 workstreams)

| ID | Workstream |
| --- | --- |
| T2a | Point lights: the `LightList` uniform buffer, nearest-N selection, frustum culling, shading in the world shaders. |
| T2b | Torch item and held light: item definition, equip effect, view model, and the light following the hand for local and remote players. |
| T2c | Lamp block: `GeometryMaterialId.Lamp`, emissive rendering, a point light per placed lamp, persistence through placed cubes, and building-menu entries. |
| T2d | Night tuning and readability: exposure, ambient floor, fog density, HUD and view-model floor. Night screenshots from the perf harness at fixed times. |
| T2e | Night gameplay hook (optional): spawn rate by phase, deterministic. Tests and docs (`configuration.md`, `rendering-and-assets.md`). |

**Done when:**
- Night without a light is dark.
- A torch or a lamp lights up a readable area.
- 16 lights keep the frame budget.
- Placed lamps survive a restart.

### Wave T3: Polish and Scale (later)

- Many-lamp performance (Players32 and World10k with hundreds of lamps).
- Optional: tiled or clustered light culling.
- Optional: sun shadows.

## Risks

- **Determinism.** The clock only reads ticks. A test pins that the world checksum and the physics goldens are unchanged by the clock's presence.
- **Visual regressions.** Moving every shader onto shared lighting changes how materials look. T1c matches today's look at noon before the sky changes anything.
- **Protocol.** v5. An older client is refused with the version-mismatch message, as for v4.
- **Too dark to play.** Dark nights only work once light sources exist. Between T1 and T2, nights will be dark with nothing to light them. Possible mitigations: during T1 the `SkyModel` keeps a temporary brighter night floor (a config value), and T2d lowers it.

## Open Questions (resolved 2026-10-28)

The user approved the plan and told me to proceed, so these recommended answers apply:

1. A day lasts 30 real minutes (`DREAM_DAY_LENGTH_MINUTES`, default 30), so a night lasts about 10 minutes.
2. Night is moonlit silhouettes, not pitch black.
3. The time control is debug-only, in the development overlay. It is not a gameplay skip.
4. The night spawn rate (T2e) goes ahead, but small: one extra wolf at night, deterministic.
