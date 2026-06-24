# HUD Crosshair Improvements Design

**Purpose:** Define the target design for correcting the off-center crosshair and cleaning up the HUD layout model in `dream`.

**Status:** Approved design direction

**Scope:** Crosshair layout, HUD rendering boundaries, responsive scaling, and HUD geometry verification

## Goal

Make the HUD resolution-aware by centering and scaling the crosshair from live viewport metrics, while introducing a small layout boundary that keeps HUD math easier to understand and safer to change.

## Design Principles

- Derive HUD geometry from the live viewport, not from baked pixel coordinates.
- Keep the fix bounded to HUD behavior rather than expanding into a full UI rewrite.
- Separate layout math from OpenGL submission wherever that meaningfully improves testability.
- Prefer simple, deterministic layout rules over manually tuned per-resolution constants.

## Target State

After this work:

- the crosshair renders at the real viewport center
- the crosshair scales with window size
- crosshair size remains bounded by minimum and maximum limits
- HUD geometry math is isolated enough to be unit-tested
- `Render.DrawHUD()` has clearer internal responsibilities

## Crosshair Layout Design

### Positioning

The crosshair center should be computed from the current viewport:

- `centerX = width / 2`
- `centerY = height / 2`

### Scaling

Crosshair sizing should derive from the smaller viewport axis so the result stays visually proportional across aspect ratios.

Suggested rule:

- `baseSize = min(width, height) * ratio`
- clamp `baseSize` between a minimum and maximum pixel range

The layout should produce at least:

- arm length
- center gap
- overall line endpoints

### Visibility

The crosshair should remain hidden while the settings menu is open.

## HUD Structure Design

### Layout helper

Introduce a small helper type dedicated to HUD geometry computation. The exact shape can be lightweight, such as:

- `HudLayout`
- `CrosshairLayout`
- `HudMetrics`

It should be responsible for:

- viewport-aware crosshair geometry
- normalized scaling rules
- clamping layout values

It should not know about:

- OpenGL handles
- ImGui state
- gameplay input

### Renderer responsibilities

`Render` should:

- request computed layout from the helper
- update the crosshair vertex data from that layout
- keep OpenGL submission local to the renderer
- keep ImGui window rendering in the HUD pass

### Window and projection coupling

The HUD should continue using the live viewport dimensions already tracked by `Render`, and crosshair layout should consume the same dimensions rather than keeping its own parallel assumptions.

## Testing Design

Add unit tests for the layout helper that verify:

1. the center equals the viewport midpoint
2. the crosshair scales upward for larger viewports
3. the crosshair respects minimum and maximum size bounds
4. generated endpoints are symmetric around the center

These tests should avoid OpenGL and assert only on math results.

## Documentation Design

Update project docs to state that:

- crosshair layout is viewport-driven
- the HUD now mixes ImGui panels with a responsive OpenGL crosshair primitive
- future HUD changes should use layout helpers rather than hardcoded pixel coordinates

## Scope Boundary

Included:

- crosshair centering fix
- responsive scaling
- layout helper extraction
- HUD math tests
- doc and ledger updates

Deferred:

- full HUD module split across multiple files unless the helper naturally justifies it
- animated crosshair behavior
- configurable crosshair themes or colors
