# HUD Crosshair Improvements Analysis

**Purpose:** Capture the root cause and architectural issues behind the off-center crosshair and fragile HUD behavior in `dream`.

**Status:** Complete

**Scope:** HUD layout, crosshair rendering, viewport coupling, and maintainability risks

## Primary Bug

The crosshair is not centered because its vertex positions are hardcoded for an `800 x 600` viewport inside `Render.InitializeHUD()`.

Current hardcoded coordinates:

- horizontal line: `(390, 300)` to `(410, 300)`
- vertical line: `(400, 290)` to `(400, 310)`

Those values only center the crosshair for a viewport whose midpoint is `(400, 300)`. The project now starts at `1440 x 1024`, whose midpoint is `(720, 512)`, so the crosshair is rendered far from the actual center.

## Root Cause

The HUD pipeline is currently split across two different assumptions:

- the projection matrix is now built from the live viewport size
- the crosshair geometry is still built from fixed startup-time pixel coordinates

That means the projection is correct, but the source geometry is stale and resolution-specific.

## Secondary HUD Issues

### Static geometry setup

The crosshair VBO is initialized once with baked coordinates and never rebuilt from current viewport metrics.

### No layout boundary

`Render.DrawHUD()` currently mixes:

- viewport math
- OpenGL line rendering
- ImGui frame/input setup
- HUD window placement
- settings menu rendering

This makes layout bugs harder to reason about and easier to reintroduce.

### No responsive sizing policy

The crosshair size is expressed as fixed pixels, so it does not adapt intentionally as the window changes.

### No geometry-level tests

There is no small helper that can be unit-tested for:

- center calculation
- scaling behavior
- min and max bounds

That leaves HUD correctness dependent on manual visual checks alone.

## Recommended Direction

Adopt a viewport-driven HUD layout layer.

This means:

- compute crosshair layout from current viewport dimensions
- scale the crosshair from the smaller viewport axis
- clamp size to sensible minimum and maximum values
- separate layout computation from raw OpenGL submission
- keep ImGui windows and crosshair rendering in the same HUD pass, but with clearer internal boundaries

## Proposed Structural Improvement

Introduce a small helper such as `HudLayout` or `CrosshairLayout` that owns:

- viewport center
- crosshair gap
- crosshair arm length
- optional thickness policy for future work
- anchor positions for HUD panels if needed later

That helper should be testable without OpenGL or window bootstrapping.

## Scope Boundary

Included in the recommended follow-up:

- fix crosshair centering
- add responsive crosshair scaling
- extract HUD layout computation
- add unit tests for HUD math
- refresh HUD documentation

Deferred:

- full HUD redesign
- replacing the crosshair with ImGui-drawn primitives
- broader UI theming work
