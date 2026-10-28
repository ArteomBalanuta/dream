# Performance And Optimizations

This document is the current index of optimization techniques supported by the engine. It is intentionally factual: it records what exists today, the subsystem it protects, and the guardrails future changes should preserve.

## Rendering

### View Frustum And Distance Culling

World rendering applies lightweight visibility checks before drawing world geometry. The older compatibility-cube path filters render candidates with `WorldVisibility` before batching, and the static chunk path keeps chunk visibility bounds-aware so large chunks do not disappear when the camera moves through or beside them.

Guardrail: camera movement must not change authoritative world state. Visibility checks should affect presentation only.

### Spatial Candidate Queries

The map separates floor lookup from placed-cube lookup. Floor cubes are queried from bounded local map coordinates, while placed cubes use the placed-cube spatial index. This avoids scanning the full world array for every render frame.

Guardrail: keep broad candidate queries close to the map/spatial-index layer instead of adding full-world scans in render loops.

### Model And Shader Batching

Visible compatibility cubes and geometry-native render blocks are grouped by model and shader before drawing. Model-backed groups use instanced `AnimatedModel` rendering, which prevents dense authored micro-grid shapes from becoming thousands of individual model draw calls.

Guardrail: new cube-like world geometry should enter the batch planner or static chunk path, not call per-object model rendering in a tight loop.

### Reusable Instance Matrix Buffers

The instanced cube/block paths reuse matrix buffers for per-instance transforms, and `AnimatedModel` uploads the active matrix range directly to the instance buffer. This avoids repeated matrix-array allocation and extra `ToArray()` copies during buffer upload.

Guardrail: avoid per-frame allocations in hot render paths; prefer reusable buffers with explicit active ranges.

### Shader Uniform Location Cache

`Shader` caches uniform locations after first lookup so repeated render-path uniform updates do not repeatedly call `GL.GetUniformLocation`.

Guardrail: keep uniform lookup behind the shader helper instead of calling OpenGL uniform lookup directly from hot draw code.

### Static Chunk Mesh Rendering

`WorldRenderer` routes `GeometryRenderExport` through `StaticWorldChunkRenderer` for static world geometry. The renderer normalizes compatibility cubes and geometry render blocks into render-only voxel cells, groups them by chunk, material, and material role, emits only exposed faces, uploads indexed chunk meshes, and draws those meshes directly through OpenGL.

Grass chunk meshes are split into top, side, and bottom material-role batches so they bind the same textures as the old glTF terrain cubes. Dirt, stone, sand, mud, snow, rock, and grass-path materials use one diffuse texture role each from the repo-owned terrain texture library.

Guardrail: static chunk meshes are presentation artifacts. They must not become authoritative world state, drive collision, or drive multiplayer snapshots. When static chunks normalize compatibility cubes, they must preserve material ids from the exported geometry cells instead of inventing renderer-local terrain materials.

The static chunk export signature includes compatibility-cube material ids. Material-only changes must invalidate cached meshes just like coordinate or size changes, otherwise stale buffers can keep rendering an old terrain batch for a still-solid block.

Presentation feeds static chunk rendering from a stable static-world geometry source instead of a camera-local static source. Dynamic cubes and fracture pieces remain radius-filtered for presentation, but static terrain stays full and stable so camera movement only affects which already-built chunks are visible; it should not invalidate or rebuild the static mesh cache.

Guardrail: do not make camera position part of static mesh source selection. If the world grows too large for the stable static source, move to chunk-owned streaming with build/draw radii and background mesh generation instead of reintroducing camera-local static mesh inputs. Keep dynamic physics-owned bodies out of the static chunk cache.

### Static And Dynamic Cube Ownership Split

`GeometryRenderExport` now separates static compatibility cubes from dynamic compatibility cubes. Static chunk rendering only consumes the static list, while live rigid-body cubes render through the direct world-batch path from their current transforms.

Guardrail: do not feed body-driven gameplay cubes back into the static chunk cache. If a cube can move under physics, its presentation must follow the live transform path instead of the cached terrain path.

### Hidden-Face Culling

The static chunk mesh builder suppresses fully covered adjacent faces before upload. This reduces vertex/index count and fill cost for dense voxel regions.

Guardrail: face culling should remain conservative. If partial coverage is uncertain, prefer keeping a face over hiding visible geometry.

Static chunk terrain draws the remaining exterior faces double-sided. This prevents edge cameras, clipped first-person views, or underside views from seeing collision-only transparent blocks where the mesh had emitted only the opposite winding of an otherwise visible exterior face.

### Per-Chunk Mesh Cache

Static chunk meshes are cached by `StaticWorldChunkMeshKey`, which combines chunk key, material id, and material role. Each cache entry has its own signature, so camera movement at the visible-world boundary does not force a full static-world mesh rebuild or synchronous GL re-upload.

The per-entry signature must include the actual material cells and neighboring occluder cells used to build that mesh. Reusing a chunk/material/role key without comparing that input signature can preserve a partial mesh built from an earlier camera-local export, which presents as walkable but invisible terrain when the player flies away and returns.

Guardrail: do not return to one global visible-export signature for static world buffers, and do not skip cached chunks by key alone. Movement and connect-time presentation should reuse unchanged chunk buffers only when the cached input signature still matches.

### Dirty-Chunk Build Planning

Static chunk planning skips cached chunk/material/role keys when their chunk is not dirty and the cached input signature still matches. If the visible export changes because the camera or render radius crosses a boundary, the renderer should plan only missing, dirty, or input-changed chunk meshes instead of re-evaluating every cached chunk.

Guardrail: dirty-region decisions should stay keyed by world-space chunks, not by camera position alone. Static dirty cells must also affect the static export signature, otherwise a render-dirty notification can be skipped when the visible cell list itself has not changed.

Moving placed cubes that cross cell boundaries must dirty both the source and destination cells. Otherwise the static chunk cache can keep the old chunk mesh alive while also building the new one, which makes the rendered position drift away from the live cube position.

`GeometryRenderExport` carries two dirty sets. `DirtyCells` lists every render-dirty cell. `StaticDirtyCells` lists only cells whose static render content (`CompatibilityCubes` or `Blocks`) changed, before or after the mutation. `Map.MarkRenderDirty` tracks which cells currently contribute static content, so removing or converting a static cell still invalidates its chunk. `StaticWorldChunkRenderer` reads only `StaticDirtyCells`. Before 2026-10-27 it read `DirtyCells`, so every frame a dynamic cube or fracture piece moved forced a full static extract, a chunk re-plan and identical chunk rebuilds, which was the main frame-rate drop when pushing cubes.

Guardrail: dynamic cubes and fracture pieces are drawn from `DynamicCompatibilityCubes` and `DynamicBlocks` every frame and are not part of static chunk meshes. Their movement must never reach `StaticDirtyCells`.

`Map.UpdateCubePosition(..., syncPhysics: true)` moves a compatibility cube's existing Bullet body in place through `WorldPhysicsBinder.TryMoveCompatibilityCubeBody`, and reindexes it when the cube crosses cells. It falls back to a full cell rebuild only when the cube has no indexed body, or when a static cube leaves its cell and has to become dynamic. Replicated `CubeMoved` deltas arrive every server tick per moving cube, so the client must not dispose and reallocate a shape, a motion state and a body for each one.

### Worker Mesh Build With Render-Thread Upload

Static chunk CPU mesh generation now runs on a background worker, while OpenGL buffer creation and replacement stay on the render thread. The renderer stages finished worker results and uploads only a bounded number of chunk meshes per frame, keeping stale buffers alive until the replacement batch completes.

Guardrail: worker jobs must stay CPU-only. Do not call OpenGL from the background build path, and do not destroy still-visible buffers until their replacement uploads have been applied.

### Bounded Occluder Sets

Static chunk build inputs use the current chunk plus neighboring chunks as occluders. This keeps hidden-face checks local instead of scanning the whole visible export for every face.

Guardrail: if chunk size or culling rules change, keep occluder scope spatially bounded.

### Face-Plane Occluder Index

The hidden-face mesh builder indexes occluder cells by adjacent face plane before testing interval coverage. This keeps dense voxel mesh builds from scanning every occluder for every candidate face.

Guardrail: keep hidden-face culling spatially indexed; dense startup-world mesh generation should remain inside an interactive budget.

## World Geometry And Collision

### Global Micro-Grid Authoring

Startup sculptures are authored in a global micro-grid where one normal world cube maps to `16 x 16 x 16` absolute microcells. The authored coordinates are projected back into the current cell/subcell storage bridge so shapes can cross old parent-cell boundaries without visual gaps.

Guardrail: canonical occupancy lives in the geometry world model, not in render-only fill or display transforms.

### Greedy Render Merge

Static startup sculpture render blocks are cached through a global greedy merge. Contiguous authored microcells become larger cuboid render descriptors, including descriptors that cross parent-cell boundaries when material matches.

Guardrail: render merging should reduce descriptor count without changing canonical occupancy or collision semantics.

### Geometry-Native Collision Export

Subdivided cells emit geometry-native collision cuboids, and solid subcell runs are greedily merged before export. This avoids one rigid body per mini-cube while preserving collision derived from canonical occupancy.

Guardrail: collision descriptors should be derived from geometry occupancy, not from visual-only scale such as `VisualFillScale`.

### Batched Dynamic Fracture Sync

Physics sync groups moved fracture-piece bodies by parent geometry cell before updating map presentation data. Each dirty rubble cell copies its piece list and rebuilds render/collision descriptors once per tick, even when several mini-cubes in that cell moved.

Guardrail: do not update moving fracture pieces one body at a time through full cell artifact rebuilds. The authoritative body transforms can arrive per rigid body, but map/render/collision descriptor refresh should stay batched by dirty cell.

### Coarse Static Startup Collision

Static startup sculpture collision is intentionally coarser than render geometry for responsive bootstrap. Static collision uses merged/coarse bodies, while dynamic and fractured cells keep the more focused per-cell update path.

Guardrail: this is a performance tradeoff for static startup geometry only; do not apply coarse collision to gameplay objects that need precise interaction unless that tradeoff is explicit.

### Frozen Resting Cubes

`CubeRestFreezer` (PHYS-2) turns a placed cube that has rested for 0.5 s into a static collider. Bullet never adds a static object to a simulation island, so a resting floor of placed cubes no longer joins the island of a player standing on it, and it costs nothing per tick. Before, a player touching about 2,400 placed cubes kept about 2,250 of them solving every tick (Smoke8, about 9.7 ms per tick, against about 0.4 ms with no players). A frozen cube wakes when it is pushed or carried, when its support is removed, or when it is activated, so gameplay matches the all-dynamic simulation. `CubeRestFreezer.LastAwakeBodyCount` reports how many bodies were simulated in the last step. `CubeRestFreezerLoadMeasurementTests` (`DREAM_LOAD_TEST=1`) samples it, and `DREAM_CUBE_REST_FREEZE=0` gives the before figures. See `docs/architecture.md`, Physics Model.

Guardrail: freeze only unit placed cubes, and keep every wake rule. A frozen cube that missed a wake would float in the air or block a push.

### Merged Floor Collider

The base floor is rendered as solid cubes but represented physically by one merged static floor collider. This keeps floor collision cheap while preserving the voxel-style visual surface.

Guardrail: floor rendering and floor collision can differ internally, but their world-space extents must stay aligned.

## Runtime And Networking

### Fixed Authoritative Tick

The desktop host advances authoritative gameplay through `ServerTickRunner` and `SimulationClock` at a fixed `64 Hz` tick rate. Render-frame delta no longer directly owns authoritative simulation cadence.

Guardrail: client presentation, camera, and input feel should remain render-frame responsive; fixed tick should not quantize camera motion.

`SimulationClock` caps catch-up at `DefaultMaxTicksPerAdvance` (8) ticks per advance and drops the excess time, counting it in `DroppedTicks`. Without the cap, a stall replayed every missed tick back to back with the latest command. The 0.5 s checkpoint stall became about 34 ticks in a burst, which teleported players and pushed cubes. The cap applies to the server host and to the desktop client mirror.

Guardrail: a stall must slow the world down, not replay it in a burst. Do not remove the cap to "catch up".

### Checkpoint Persistence Off The Tick

Every 5 s, `DedicatedServerHost` calls `WorldSessionRuntime.CapturePersistenceCheckpoint()` under the authority lock. The capture copies player world state, profile vitals, enemies, placed cubes and fracture pieces into a `PersistenceCheckpoint` without any database access. `BackgroundCheckpointWriter` then writes it on one background worker. `SqlitePersistenceCheckpointWriter` writes the whole checkpoint on one connection inside one transaction, with one prepared command per table.

Incremental checkpoints (SCALE-4, 2026-10-28): most captures hold only what changed since the previous capture. `Map` records cube position changes and rubble changes, `WorldObjectRegistry` records cube registrations, damage and removals (as tombstones), enemies are diffed against the last capture, and connected players are always captured. Tracking is off until the first capture, so client maps pay nothing. Every `FullCheckpointInterval` (12) captures, about once a minute, the capture is a full sweep that repairs any drift. The first capture and the first after a map rebuild are also full, and `WorldSessionRuntime.Dispose` writes a final full checkpoint behind the queued immediate writes. An incremental capture that finds nothing returns null and allocates nothing; one with k changed rows allocates O(k). When the writer falls behind, `PersistenceCheckpoint.Merge` folds queued checkpoints into one write: the last write wins per key, tombstones are kept, and a delete followed by a re-add keeps the re-add. A failed write is merged under the next one. Tombstones are never SQL deletes, because a checkpoint delete could land after the ordered write queue re-inserted the same key; the write queue still owns row existence. Each checkpoint also carries a barrier on the write queue, so its updates never run before the inserts queued ahead of its capture.

Before 2026-10-27 the checkpoint ran on the tick thread, opened a connection and autocommitted per row, and repaired every cube's identity by reloading the whole `placed_cube_instances` table. The 2026-07-05 perf logs show 528–575 ms server ticks on exactly the checkpoint ticks.

Shutdown on a locked database (wave 9 fix loop 1): the shutdown flush shares one deadline, the write queue's `DisposeDrainDeadline` (30 s). `BackgroundCheckpointWriter.Dispose` starts it with `PersistenceWriteQueue.BeginShutdownDrain()`, and `PersistenceWriteQueue.Dispose` drains only for the time left. While shutting down, a checkpoint whose barrier cannot complete gives up instead of waiting `ImmediateWritesWaitTimeout` (10 s). That happens when the queue is in a lock episode (`IsLocked`) or the deadline has passed. The checkpoint is counted in `DeferredAtShutdownCheckpoints` and left to the final full checkpoint. `WorldSessionRuntime` queues that final checkpoint as a read behind the immediate writes, so it needs no barrier wait and is dropped with them if the deadline passes. Before, the background writer and the final checkpoint each waited 10 s for the barrier, and each then spent a 5 s `busy_timeout` on its write, all before the 30 s drain started. In `ShutdownDrainDeadlineTests`, with a 3 s drain deadline and the lock held, host dispose went from 37.1 s to 5.7 s. That is the deadline plus the `busy_timeout` of the attempt still in flight.

Guardrail: checkpoint writes can land after the tick thread deleted rows. Writers must only `UPDATE` existing cube, fracture-piece and enemy rows, and touch only the volatile profile fields (`interaction_mode`, `current_hp`, `max_hp`). Re-inserting deleted rows would resurrect destroyed cubes on restart, and writing whole profiles could roll back experience saved after the capture.

### Network Send Path Off The Simulation

The client sends at most one merged command per client tick through `RemoteClientSession`'s background writer. Before, it sent a blocking command per render frame: about 110–250 per second against a server that consumes 64, each decoded under the authority lock. Each `ServerConnection` drains an ordered outbound queue on its own writer task. The tick loop only enqueues, merges consecutive unsent snapshots and faults a connection whose backlog passes 512 messages. Both TCP ends disable Nagle. The server tick loop runs on a dedicated thread paced by `TickPacer`, so most iterations run exactly one tick. Before, a 15.6 ms `Task.Delay(1)` beat against the 15.625 ms tick.

Guardrail: never await a socket write from the tick loop or block the render thread on a send.

### Persistence Off The Simulation

Gameplay actions no longer read SQLite on the tick. Before, every cube hit and placement reloaded the whole `placed_cube_instances` table. Immediate writes go through the ordered `PersistenceWriteQueue`. See `docs/server-persistence-ddl.md`.

### Latest Command Inbox

The headless host drains the latest command per joined session each authoritative tick. This prevents the server from applying unbounded stale input queues while still allowing joined sessions to contribute movement/look/action state.

Guardrail: the inbox is for authoritative command intake, not for client-side camera smoothing.

### Remote Player Interpolation

Remote player snapshots are buffered in `ClientScenePresenter` and rendered at a slight delay. This smooths replicated remote actors without mixing remote-player rendering into the local camera or local first-person viewmodel path.

Guardrail: local camera remains client-local; only replicated facing/actor state should affect how other players see the actor.

### Background Receive Loop

The remote client keeps socket reads on a background receive loop and drains authoritative messages into the client protocol/presenter path. This avoids blocking the render thread on network reads.

Guardrail: avoid synchronous socket reads or long protocol processing directly inside render.

## HUD And Diagnostics

### Optional Performance Counters

The settings panel can show performance counters for total host frame time, present (`SwapBuffers`) time, render export time, physics time, actor draw slices (enemy, local player, remote players), HUD/ImGui time from the previous frame, visible/exported world descriptors, render batches, collision-body count, static chunk metrics such as visible chunks, mesh count, exposed faces, and triangle count, plus the latest integrated local-server sample for authoritative tick, snapshot, and broadcast time.

Guardrail: when diagnosing performance, prefer these counters and targeted tests over guessing from FPS alone.

For repeatable automated profiling, launch the desktop client with `DREAM_AUTO_CONNECT=1` and `DREAM_RENDER_PERF_LOG=1`. Auto-connect enters the integrated local server world without clicking the startup UI, and the render log prints the same performance counters once per second.

For multiplayer visibility and host-FPS investigations, also use `DREAM_NET_DIAGNOSTICS=1`. This separate diagnostics stream is opt-in and rate-limited: it logs server accept/join/snapshot timing, client receive/apply facts, presenter remote-player state, render-side remote-player counts, and command-send latency. Use `DREAM_NET_DIAGNOSTICS_PATH` when the default output-folder `logs/network-diagnostics.log` path is inconvenient.

Guardrail: leave network diagnostics disabled for normal FPS comparisons after the bug is understood. The stream is intentionally bounded, but file and console writes still add observer overhead.

### Optional Collision Debug Rendering

Collision wireframes are optional and can be expensive in dense worlds. They should be disabled for runtime performance comparisons unless the collision overlay itself is the subject of the measurement.

Guardrail: do not evaluate render optimizations with collision-debug drawing enabled unless explicitly testing debug-render cost.

## Asset And Resource Handling

### Runtime Asset Path Resolution

Runtime assets are resolved from the application output layout through shared path helpers. This keeps texture/model loading predictable across build, run, and test contexts.

Guardrail: avoid reintroducing hardcoded absolute asset paths in new optimization paths.

### Texture Cache For Static Terrain

Static terrain chunk rendering uses `StaticTerrainTextureCache` so terrain textures are loaded once per material/role and reused for subsequent draws.

Guardrail: texture loading must not happen repeatedly per frame for the same material role.

### Explicit Asset Normalization

Imported world assets use explicit authored-height and target-world-height normalization metadata. This avoids opaque one-off scale constants in renderer code.

Guardrail: sizing fixes should go through asset metadata or shared unit conventions, not per-renderer camera/scale hacks.

## Current Known Gaps

- Static chunk extract/signature/plan work still runs on the render thread when a new or changed visibility set first appears.
- First-visibility world draw bursts can still spike before the staged upload cache fully warms, especially in multi-client startup runs.
- Dynamic fractured rubble is not yet fully migrated into persistent dirty-region chunk rebuilds.
- Hidden-face culling is conservative and does not yet perform partial-face subdivision for every mixed-size adjacency case.
- Performance measurements still need more repeatable automated runtime scenarios beyond build/test smoke runs.
