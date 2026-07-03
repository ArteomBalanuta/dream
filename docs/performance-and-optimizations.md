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

Grass chunk meshes are split into top, side, and bottom material-role batches so they bind the same textures as the old glTF terrain cubes. Dirt and stone use one diffuse texture role each.

Guardrail: static chunk meshes are presentation artifacts. They must not become authoritative world state, drive collision, or drive multiplayer snapshots.

### Hidden-Face Culling

The static chunk mesh builder suppresses fully covered adjacent faces before upload. This reduces vertex/index count and fill cost for dense voxel regions.

Guardrail: face culling should remain conservative. If partial coverage is uncertain, prefer keeping a face over hiding visible geometry.

### Per-Chunk Mesh Cache

Static chunk meshes are cached by `StaticWorldChunkMeshKey`, which combines chunk key, material id, and material role. Each cache entry has its own signature, so camera movement at the visible-world boundary does not force a full static-world mesh rebuild or synchronous GL re-upload.

Guardrail: do not return to one global visible-export signature for static world buffers. Movement and connect-time presentation should reuse unchanged chunk buffers.

### Dirty-Chunk Build Planning

Static chunk planning skips cached chunk/material/role keys when their chunk is not dirty. If the visible export changes because the camera or render radius crosses a boundary, the renderer should plan only missing or dirty chunk meshes instead of re-evaluating every cached chunk.

Guardrail: dirty-region decisions should stay keyed by world-space chunks, not by camera position alone.

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

### Coarse Static Startup Collision

Static startup sculpture collision is intentionally coarser than render geometry for responsive bootstrap. Static collision uses merged/coarse bodies, while dynamic and fractured cells keep the more focused per-cell update path.

Guardrail: this is a performance tradeoff for static startup geometry only; do not apply coarse collision to gameplay objects that need precise interaction unless that tradeoff is explicit.

### Merged Floor Collider

The base floor is rendered as solid cubes but represented physically by one merged static floor collider. This keeps floor collision cheap while preserving the voxel-style visual surface.

Guardrail: floor rendering and floor collision can differ internally, but their world-space extents must stay aligned.

## Runtime And Networking

### Fixed Authoritative Tick

The desktop host advances authoritative gameplay through `ServerTickRunner` and `SimulationClock` at a fixed `64 Hz` tick rate. Render-frame delta no longer directly owns authoritative simulation cadence.

Guardrail: client presentation, camera, and input feel should remain render-frame responsive; fixed tick should not quantize camera motion.

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

The settings panel can show performance counters for render export time, physics time, visible/exported world descriptors, render batches, collision-body count, and static chunk metrics such as visible chunks, mesh count, exposed faces, and triangle count.

Guardrail: when diagnosing performance, prefer these counters and targeted tests over guessing from FPS alone.

For repeatable automated profiling, launch the desktop client with `DREAM_AUTO_CONNECT=1` and `DREAM_RENDER_PERF_LOG=1`. Auto-connect enters the integrated local server world without clicking the startup UI, and the render log prints the same performance counters once per second.

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

- Static chunk mesh generation still happens on the render thread when a new or changed chunk first appears.
- If first-build latency becomes the dominant measured bottleneck again, the safest threading path is CPU-only mesh generation on workers followed by render-thread OpenGL upload.
- Dynamic fractured rubble is not yet fully migrated into persistent dirty-region chunk rebuilds.
- Hidden-face culling is conservative and does not yet perform partial-face subdivision for every mixed-size adjacency case.
- Performance measurements still need more repeatable automated runtime scenarios beyond build/test smoke runs.
