# Performance First-Pass Analysis

**Purpose:** Capture the most obvious current performance bottlenecks in `dream`, especially the large-floor regression reported by the user.

**Status:** Complete

**Scope:** World storage, render-loop iteration, static floor physics, and hot-path shader overhead

## User-Observed Problem

The project runs acceptably on the user's hardware with a small floor, but drops significantly when the floor grows from a small area such as `10 x 10` to a much larger one such as `100 x 100`.

The current code structure explains that regression even before detailed profiling:

- floor growth is coupled to cubic world allocation
- floor tiles are rendered one-by-one
- floor tiles also get one rigid body each
- every rigid body is revisited each frame

## Primary Findings

### 1. Floor size is coupled to full cubic world size

`Map.cs` uses a single `SideSize` for all three axes and allocates:

- `Cube[SideSize, SideSize, SideSize]`

It then eagerly initializes every cell with a `Cube` instance.

That means:

- `SideSize = 10` creates `1,000` cube objects
- `SideSize = 100` creates `1,000,000` cube objects

This is a major memory and startup cost even when only the floor layer is actually used.

### 2. Rendering scans the entire 3D array every frame

`Game.OnRenderFrame()` currently iterates:

- `foreach (var cube in _map.Cubes)`

and then filters on `cube.IsSolid`.

This means frame cost scales with total allocated world volume, not with the number of visible or even solid cubes.

### 3. Floor tiles get one Bullet rigid body each

`Map.AddCube()` creates a rigid body for each floor tile through `AddPhysicsBody()`.

For a `100 x 100` floor, that means roughly `10,000` static rigid bodies for the floor alone.

That is unnecessary for a flat continuous floor and creates avoidable:

- world registration cost
- memory cost
- broadphase cost
- per-frame body iteration cost elsewhere in the game loop

### 4. Static map bodies are revisited every frame

`Game.OnUpdateFrame()` iterates all map cube bodies and updates their transforms back into cube positions.

Static floor cubes do not need per-frame transform sync, but they still participate in this loop under the current structure.

### 5. Per-cube draw overhead remains high

Each solid cube is drawn individually.

Floor cubes currently use the shared loaded model, which avoids repeated model loads, but still incurs:

- one model render call per floor tile
- repeated material binding logic per tile
- repeated matrix uniform work per tile

This is not yet a chunking problem; it is already costly under the current per-cube architecture.

### 6. Shader uniforms are looked up repeatedly in hot paths

`Shader.cs` calls `GL.GetUniformLocation` inside each setter every time a uniform is set.

That adds avoidable CPU overhead in the draw loop, especially when:

- every cube submission updates `model`
- every render pass updates view/projection/material uniforms repeatedly

## Root Cause Summary

The large-floor regression is not caused by one isolated bug.

It is the combined result of:

1. cubic eager allocation for a largely empty world
2. full-volume frame iteration
3. per-tile static floor physics
4. unnecessary static-body sync
5. avoidable per-draw CPU overhead

## Recommended First Pass

Keep the current architecture and address the obvious waste first:

- decouple floor dimensions from full world height/depth allocation
- stop pre-creating empty `Cube` objects for every cell
- maintain direct lists for solid cubes and dynamic cubes
- replace per-tile static floor rigid bodies with one merged floor collider
- stop per-frame syncing for static floor bodies
- cache uniform locations in `Shader`

## Scope Boundary

Included in this first pass:

- obvious structural waste inside the current architecture
- bounded CPU and memory reductions
- no chunk renderer or meshing rewrite

Deferred:

- chunking
- frustum culling systems
- mesh merging for arbitrary terrain
- GPU instancing overhaul
