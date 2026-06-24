# Phase 1 Analysis: Shell Vs Play Scene

**Purpose:** Define the first concrete architectural refactor from the pattern roadmap by separating the OpenTK window shell from active gameplay orchestration.

**Status:** Complete

**Scope:** `Game.cs` responsibility split, scene ownership boundaries, and the minimum viable `PlayScene` extraction

## Why This Phase Comes First

`dream/Game.cs` is currently the main pressure point in the project structure.

It owns:

- OpenTK window lifecycle
- update-loop orchestration
- render-loop orchestration
- settings-driven resolution application
- runtime floor rebuild coordination
- collision-debug render gating
- cursor-mode switching
- FPS tracking

That is a strong signal that the project needs a scene boundary before additional features deepen the coupling.

## Current Structural Problem

`Game` is currently doing two different jobs:

1. platform shell
2. active gameplay scene

Those two responsibilities change for different reasons.

### Platform shell concerns

These should remain in `Game`:

- `GameWindow` inheritance
- startup window configuration
- OpenTK lifecycle hooks
- swap buffers
- viewport update on resize
- top-level title/FPS display

### Active gameplay concerns

These should move behind a gameplay boundary:

- owning player/map/physics/render references
- gameplay-frame update ordering
- gameplay-frame rendering
- settings-dependent gameplay behavior
- runtime world rebuilds

## Recommended Pattern

Use the **Scene Pattern** with a single initial concrete scene:

- `Game`
  - remains the OpenTK host shell
- `PlayScene`
  - owns current gameplay orchestration

This should be a lightweight extraction, not a full scene-manager framework.

## Minimum Viable Extraction

The first step should not introduce menus, loading screens, or a general scene stack yet.

The minimum viable version is:

- one `PlayScene` class
- one current-scene field in `Game`
- direct delegation from `Game` lifecycle methods into `PlayScene`

That gives the structure benefit without overbuilding.

## Ownership Boundary

### `Game` should own

- window creation settings
- `ClientSize`
- OpenTK callbacks
- cursor state application entry point
- resize event wiring
- FPS counting and title text

### `PlayScene` should own

- `Render`
- `Player`
- `Map`
- `PhysicsIntegrator`
- per-frame gameplay update ordering
- per-frame world rendering
- HUD result handling for gameplay settings
- runtime floor rebuild behavior

## Design Constraint

The refactor should preserve current runtime behavior:

- movement
- rendering
- settings menu behavior
- runtime floor resize
- collision debug toggle

This is a structural extraction, not a gameplay redesign.

## Interface Recommendation

Use a small explicit scene API rather than leaking `GameWindow` internals everywhere.

Recommended first boundary:

- `PlayScene.Update(...)`
- `PlayScene.Render(...)`
- `PlayScene.Resize(...)`
- `PlayScene.Dispose()`

The initial API can still accept the pieces it needs from `Game`, such as:

- frame args
- input state
- window focus
- current window size

That is acceptable for Phase 1.

## Testing Pressure

This phase is mostly orchestration refactor, so the most useful early tests are:

- `GameSettings` and existing logic regression tests stay green
- new small tests can target scene-shell delegation or extracted pure helpers if introduced

The main verification burden will still be:

- build success
- existing tests
- controlled startup

## Expected Outcome

After this phase:

- `Game.cs` becomes smaller and easier to reason about
- future scene addition becomes straightforward
- later state/player/world/render splits have a cleaner home

This phase creates the architectural seam that the rest of the roadmap depends on.
