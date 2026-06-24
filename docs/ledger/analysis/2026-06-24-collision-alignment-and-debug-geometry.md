# Collision Alignment And Debug Geometry Analysis

**Purpose:** Capture the root cause and recommended direction for the observed cube-contact mismatch and the request for collision-geometry visualization.

**Status:** Complete

**Scope:** Cube render/physics alignment, debug collision overlays, and settings integration

## User Observation

The player can push a cube normally from some face-on approaches, but from some edges or corners the contact feels early, as if collision begins before the visible cube is actually touched.

The requested follow-up is a runtime menu setting that shows red collision outlines around pieces so contact can be debugged visually.

## Root Cause Investigation

### Physics shapes are simple unit boxes

`Map.AddPhysicsBody()` creates each cube collision body with:

- `new BoxShape(0.5f)`
- `shape.Margin = 0f`

That means the authoritative collision shape is a centered `1 x 1 x 1` cube.

### The cube asset is already centered and unit-sized

`dream/animations/GrassTextureCube.gltf` shows:

- node `Cube` has no transform overrides
- position accessor bounds are `min = [-0.5, -0.5, -0.5]`
- position accessor bounds are `max = [0.5, 0.5, 0.5]`

So the mesh itself already matches a centered unit cube.

### Render code applies an extra centering offset anyway

`Render.DrawCube()` currently builds the model matrix as:

- translate to `cube.Position`
- scale by `cube.Side`
- translate again by `(-0.5, -0.5, -0.5)`

That extra offset is appropriate only for geometry authored in `[0, 1]` local space. It is not appropriate for this project's cube mesh or for the built-in cube vertices, which are already centered in `[-0.5, 0.5]`.

## Conclusion

The most likely root cause is that visible cube geometry is shifted relative to the authoritative Bullet collision shape by the extra render-space centering translation.

That would explain why contact can feel more wrong on edges or corners than on simpler face-on approaches.

## Debug Overlay Requirement

The requested red collision geometry should show the real simulation state, so it must be drawn from Bullet transforms rather than from the normal visual cube-render path.

For the first version, the overlay should include:

- map cube collision boxes
- the player collision box

## Recommended Direction

1. Keep Bullet collision geometry as the source of truth.
2. Remove the extra visual centering offset from cube rendering.
3. Add a runtime setting that toggles collision geometry visibility.
4. Draw red wireframe collision boxes directly from Bullet transforms for both cubes and player.

## Scope Boundary

Included:

- cube/render alignment fix
- collision debug overlay for cubes and player
- runtime toggle in the settings menu
- regression tests for the new transform helper and settings state

Deferred:

- non-box collider visualization
- per-object collision filters in the UI
- deeper player-controller redesign
