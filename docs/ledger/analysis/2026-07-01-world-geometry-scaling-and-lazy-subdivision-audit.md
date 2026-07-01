Purpose: analyze the current world-geometry model against the idea of larger logical terrain cells, lazy subdivision, and non-cubic preset forms
Status: proposed
Scope: map storage, rendering, physics, destruction, and multiplayer implications for scalable world geometry

# World Geometry Scaling And Lazy Subdivision Audit

## Prompted Idea

The current world is visually and structurally too cube-bound. The desired direction is:

- allow one authored world cell to represent many smaller destructible cubes
- avoid storing all child cubes in memory until interaction requires finer detail
- support preset non-cube terrain forms such as triangular or sloped ground pieces
- keep a path open for destruction physics and much larger worlds

## Current State

The current runtime still treats a `Cube` as both:

- the smallest world-storage unit
- the default render unit
- the default destructible world object

`Map` owns a `Cube?[,,]` grid plus a `List<Cube>` render list. Floor initialization currently materializes one cube per floor cell at `y = 0`, and non-floor edits also become direct `Cube` instances immediately.

The current world pipeline is intentionally optimized but still cube-first:

- `Map` stores cubes directly
- `WorldGenerator` creates only one cube shape
- `WorldPhysicsBinder` creates one merged floor collider plus per-cube bodies for other solids
- `WorldObjectRegistry` registers destructibles through `RegisterCube(...)`
- `WorldRenderer` still iterates cube candidates and draws cube-backed content

This is good enough for the current sandbox slice, but it does not scale well to:

- coarse world authoring with fine destruction
- non-cube preset geometry
- chunked geometry streaming
- collision that changes from coarse to fine detail after damage

## Key Gaps

### Geometry storage is shape-blind

The world only knows how to store `Cube` occupancy. There is no geometry descriptor for:

- a solid macro block
- a wedge or slope preset
- a partially fractured volume
- a sparse override layer inside a larger authored cell

### Storage and presentation are too coupled

The same `Cube` object is doing too much. It acts as storage, render intent, and destruction identity. That makes it hard to introduce a world cell that is still logically solid but has not yet been expanded into fine-grained child voxels.

### Physics assumes immediate per-cube realization

The floor already has a useful optimization through one merged collider, but non-floor world solids still assume direct per-cube body creation. A lazily subdivided world needs at least two collision modes:

- coarse collider while the geometry cell is intact
- finer collision after subdivision or fracture

### Destruction is cube-centric

`WorldObjectRegistry` has become more general than before, but its real production use is still centered on cubes. If a macro cell fractures into sparse children or preset shape fragments, destruction ownership cannot stay tied to one `Cube` class forever.

### Rendering will not scale if subdivision becomes literal cubes everywhere

The current candidate query, visibility gate, and batching help, but a world that expands macro cells into thousands of child cubes on interaction needs a mesh- or chunk-oriented render seam. Otherwise the project will simply trade one storage problem for a draw-call and iteration problem.

## Recommended Direction

### Keep world units stable

The existing world-unit decision should stay intact: `1.0` world unit remains the canonical cube-relative gameplay unit. Players, enemies, collision tuning, and imported actor assets already depend on that.

The safe interpretation of the new idea is not "change what a world unit means." It is:

- keep gameplay units stable
- introduce larger authored geometry cells that may internally subdivide into micro-voxels or shape fragments

That avoids breaking player scale, world-asset normalization, and multiplayer position contracts.

### Introduce a geometry layer above direct cubes

The missing abstraction is a geometry domain that can represent:

- intact coarse cells
- preset forms such as wedge or triangle ground
- sparse child-voxel overrides created only after edits

The current `Map` should become a world façade over that geometry layer instead of remaining a raw `Cube?[,,]` owner forever.

### Use lazy subdivision instead of eager explosion

An intact authored macro cell should be stored as one record with:

- bounds or cell anchor
- shape preset
- material
- subdivision resolution
- optional sparse child override data

Only when the player damages, edits, or mines that cell should the runtime create fine-grained child occupancy for the touched region.

### Split authored geometry from render geometry

The runtime should distinguish:

- authoritative geometry state
- generated render geometry
- collision representation

That lets us keep a clean path for:

- coarse authoritative storage
- dirty-region mesh rebuilds
- collision rebuilds only when needed

## Suggested Domain Model

The clean-code shape that fits the repo is a small geometry subsystem under `dream/Simulation/World/Geometry/`.

Recommended responsibilities:

- `WorldGeometryMap`: authoritative store of high-level geometry cells and chunk records
- `GeometryCellDefinition`: material, default shape, subdivision resolution, and intact-state metadata
- `GeometryShapeKind`: solid cube, slope, wedge, and future presets
- `GeometrySubdivisionState`: optional sparse child occupancy or damage mask created on demand
- `IGeometryMutationService`: applies placement, damage, subdivision, and removal
- `IGeometryCollisionExporter`: produces coarse or refined collision descriptors
- `IGeometryRenderExporter`: produces render-ready geometry chunks or cube-compatible fallbacks

This keeps storage, mutation, physics export, and rendering export separate enough that each can evolve without another `Cube` god object.

## Migration Guidance

The safest path is evolutionary, not a flag-day rewrite.

Recommended sequence:

1. Add geometry descriptors beside the current `Map` model.
2. Let the floor migrate first to geometry-backed coarse cells because it already has merged collision behavior.
3. Add one preset non-cube form such as a wedge or triangle floor tile.
4. Add lazy subdivision for a single coarse block type.
5. Route destruction through geometry mutation results rather than direct `Cube` removal.
6. Only then start shrinking the direct role of `Cube` to an exported render/collision artifact rather than the source of truth.

## Multiplayer Impact

This feature is not just a rendering improvement. It changes authoritative world state.

The server will eventually need to replicate:

- geometry cell placement or removal
- shape kind
- subdivision resolution
- child-voxel or fracture deltas for edited regions

That means the geometry layer should be designed from day one to emit deterministic mutation events instead of relying on local scene-side object mutation.

## Risks

- Treating every fractured child as a live `Cube` will recreate the same scale problem at a smaller size.
- Mixing geometry storage and render meshes in one object will make authoritative synchronization and testing much harder.
- Changing world-unit meaning would ripple through player scale, camera feel, enemy tuning, and multiplayer snapshot assumptions.
- Physics can become the hidden cost center if collider rebuilds are too eager after small edits.

## Conclusion

The idea is strong, but it needs a dedicated geometry layer rather than more incremental cube-specific patches.

The right next step is to build:

- geometry descriptors
- lazy subdivision state
- preset form support
- exported render/collision seams

while preserving the current world-unit convention and evolving `Map` into a façade over richer geometry state.
