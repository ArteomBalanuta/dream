# Performance First-Pass Design

**Purpose:** Define the first implementation pass for improving floor and world performance in `dream` without jumping to a major renderer rewrite.

**Status:** Approved direction

**Scope:** World storage, static floor physics representation, render iteration, and hot-path shader lookup cleanup

## Goal

Reduce the current floor-scaling regression by removing obvious structural waste in memory allocation, physics representation, frame iteration, and shader uniform submission.

## Design Principles

- Fix the largest current costs before introducing chunking or batching systems.
- Keep the gameplay model recognizable and bounded.
- Prefer direct lists of active content over scanning large sparse volumes.
- Distinguish static floor representation from dynamic cube behavior.

## Target State

After this work:

- increasing floor width/depth no longer implies cubic object growth
- the game renders only solid cubes rather than scanning every world cell
- the floor uses one merged static collider instead of one rigid body per tile
- only dynamic cube bodies require per-frame transform syncing
- shader uniform lookups are cached instead of repeated in hot loops

## World Storage Design

### Separate dimensions

`Map` should stop using one `SideSize` for all axes.

Instead it should own distinct world dimensions such as:

- `Width`
- `Height`
- `Depth`

Floor generation should depend on `Width` and `Depth`, not on a cubic side length.

### Sparse empty-space representation

The world array should stop eagerly creating `Cube` instances for empty cells.

Empty cells should remain `null` or otherwise unmaterialized until a solid cube is added.

### Active solid list

`Map` should maintain a direct collection of solid cubes for rendering.

This list becomes the render source instead of enumerating the entire 3D array every frame.

## Physics Representation Design

### Merged floor collider

The flat base floor should use one static box collider sized to the generated floor footprint.

That collider should:

- match the visible floor height
- live in the Bullet world as one static rigid body
- replace the current per-floor-tile body creation

### Dynamic cube tracking

Physics should distinguish:

- all map bodies for disposal
- dynamic map bodies for per-frame transform sync

Static floor representation should not participate in the dynamic update loop.

## Rendering Design

### Direct solid iteration

`Game.OnRenderFrame()` should render from the active solid cube list.

That ensures frame cost scales with actual rendered content rather than total world volume.

### No broad renderer rewrite

This pass should not introduce chunk meshing or GPU instancing yet.

The design goal is to remove avoidable overhead within the current per-cube draw model first.

## Shader Design

`Shader` should cache uniform locations by name after first lookup.

This keeps the current API shape while reducing repeated `GL.GetUniformLocation` calls inside the draw path.

## Verification Design

Implementation should verify:

1. the project still builds successfully
2. relevant tests still pass
3. the world no longer scans or allocates cubic empty-space work for floor scaling
4. the floor no longer creates one rigid body per tile
5. the app still starts successfully after the changes

## Scope Boundary

Included:

- dimension split
- sparse empty cells
- solid cube list
- merged floor collider
- dynamic-only transform sync
- shader uniform caching

Deferred:

- chunk mesh generation
- mesh merging for arbitrary solid groups
- instanced rendering
- advanced culling systems
