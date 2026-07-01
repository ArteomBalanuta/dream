Purpose: Analyze the next performance optimization options after global micro-grid render merging.

Status: Current analysis; first implementation slice completed

Scope: Startup world render export, instanced model rendering, collision bodies, debug overlays, and near-term engine optimization choices.

## Evidence

A temporary out-of-repo probe referenced `dream.csproj` and measured `Map.CaptureGeometryRenderExport(...)` on the current startup world without modifying repository files.

Representative results:

- At `(24, 2, 16)` with radius `48`: average render-export capture was about `1.76 ms`, with `3,753` cells, `3,346` compatibility cubes, `6,264` geometry blocks, and `9,269` collision bodies.
- At `(5, 2, 5)` with radius `48`: average render-export capture was about `0.95 ms`, with `2,941` cells, `2,534` compatibility cubes, `6,264` geometry blocks, and `9,269` collision bodies.
- Near a sculpture at `(14.25, 1.05, 12.25)` with radius `8`: average render-export capture was about `0.42 ms`, with `485` cells, `335` compatibility cubes, `256` geometry blocks, and `9,269` collision bodies.
- Visibility tests against representative camera facings still produced roughly `4,687` to `6,264` visible geometry blocks and `1,329` to `2,579` visible compatibility cubes in full-radius views.

The previous global micro-grid merge reduced the worst render descriptor explosion, but the frame still pays for thousands of render instances plus thousands of Bullet bodies.

## Current Hot Spots

### Render Export And Frame Allocation

`WorldSessionRuntime.CapturePresentationState(...)` calls `Map.CaptureGeometryRenderExport(...)` every render frame. That method allocates fresh `List<>` and `HashSet<>` instances, scans floor/placed compatibility cubes, then appends cached merged static sculpture blocks.

This is no longer catastrophic, but it is still recurring CPU and GC pressure.

### Instance Matrix Upload

`Render.DrawGeometryBlockBatch(...)` and `Render.DrawAnimatedCubeBatch(...)` allocate a `Matrix4[]` every draw, and `AnimatedModel.UploadInstanceModelMatrices(...)` calls `modelMatrices.ToArray()` before `GL.BufferData(...)`.

That means large batches allocate at least twice before the GPU upload, even when the underlying geometry did not change.

### Collision Body Count

The startup map still exports and registers about `9,269` collision bodies. Debug rendering is capped, but Bullet still owns the bodies and broadphase work even when debug geometry is hidden.

This is likely the biggest remaining systemic cost if physics tick time or memory pressure is visible.

### Cube-Compatible Floor Rendering

The floor still renders as thousands of compatibility cubes. Visibility and batching reduce calls, but full-radius camera views still keep thousands of visible floor/terrain instances.

## Recommended Optimization Order

### 1. Add Frame Performance Counters

Add a small opt-in diagnostics seam that records:

- render-export capture time
- exported compatibility cube count
- exported geometry block count
- visible compatibility cube count
- visible geometry block count
- geometry batch count
- collision body count
- physics step time
- HUD/render pass timing

This should be toggleable from config or the existing debug UI. It gives every future optimization a live before/after readout.

### 2. Remove Avoidable Per-Frame Allocations

Reuse buffers in:

- `WorldRenderer` for visible cube/block lists
- `WorldRenderBatchPlanner` for grouped batch dictionaries/lists
- `Render.DrawAnimatedCubeBatch(...)`
- `Render.DrawGeometryBlockBatch(...)`
- `AnimatedModel.UploadInstanceModelMatrices(...)`

At minimum, remove the `ToArray()` copy in `UploadInstanceModelMatrices(...)` and upload the already-built matrix array/span. This is lower risk than a renderer rewrite and should reduce GC/frame spikes.

### 3. Chunk Static Render Geometry

Replace large static compatibility-cube and sculpture instance batches with chunk-level static mesh or chunk-level instanced buffers.

Near-term version:

- group static floor/terrain/sculpture geometry into chunks such as `16 x 16`
- build chunk render data once
- frustum/radius cull by chunk bounds
- draw chunk buffers directly

This is the main FPS path if the GPU is still processing thousands of cube model instances.

### 4. Merge Static Collision Globally

Extend the global merge idea to collision:

- group static startup sculpture occupancy by material/region
- emit fewer static collision cuboids across parent-cell boundaries
- or move static geometry toward Bullet compound shapes / mesh shapes where appropriate

The current per-cell collision merge still leaves about `9,269` bodies. Reducing that body count should improve physics broadphase and debug-data size.

Status: completed for static startup subcell sculpture collision in [2026-07-02-global-static-collision-merge](../work-log/2026-07-02-global-static-collision-merge.md). Static authored sculpture collision is now coarser than render detail for responsive bootstrap; coarse compatibility cell collision remains separate, and dynamic/fractured geometry still uses the per-cell sync path.

### 5. Stop Rebuilding Full Presentation Export Every Frame

Move toward dirty-versioned render snapshots:

- static world export persists until geometry dirty version changes
- frame only filters visible chunks/blocks
- dynamic cubes/fracture pieces remain separate

This complements chunk rendering. Without chunking, caching only reduces CPU work but still leaves thousands of draw instances.

## Lower Priority Options

- Tighten `WorldVisibility` thresholds: quick but may hide expected world geometry and does not solve underlying instance/body counts.
- Lower `WorldVisibility.MaxRenderDistance`: useful as a debug/user setting, but it is a tuning knob rather than an engine fix.
- More aggressive greedy cuboid merging: can reduce descriptors, but oversized cuboids may stretch textures and lose intended small-shape detail.
- Reduce startup world density: easy, but it fights the current goal of richer geometry.

## Best Next Slice

The best immediate implementation slice is:

1. Add frame performance counters.
2. Remove obvious render-path allocations.
3. Add tests around matrix upload / reusable buffer behavior where possible.

Implementation note: the first slice was completed in [2026-07-02-frame-performance-counters-and-render-allocation-pass](../work-log/2026-07-02-frame-performance-counters-and-render-allocation-pass.md). Global static collision work was completed in [2026-07-02-global-static-collision-merge](../work-log/2026-07-02-global-static-collision-merge.md), reducing the enriched startup map collision export from `9,269` bodies to `629` and reducing client join world application from about `79,058 ms` to about `552 ms`. The remaining high-impact work is static chunk render data.

The best next architecture slice is:

1. Add static world render chunks.
2. Move floor and startup sculptures into chunk render data.
3. Keep dynamic fractured/player-built objects on the current descriptor path until they need chunk invalidation.

The best physics slice is:

1. Measure physics step time with the current `9,269` bodies.
2. Add global static collision merging for startup sculpture cells.
3. Re-measure body count and physics step time.
