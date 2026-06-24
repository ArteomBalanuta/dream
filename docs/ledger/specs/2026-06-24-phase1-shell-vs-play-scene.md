# Phase 1 Spec: Shell Vs Play Scene

## Goal

Refactor the current runtime so `Game` is primarily the OpenTK window shell while a new `PlayScene` owns active gameplay orchestration.

## Functional Requirements

### Shell responsibilities

`Game` must continue to:

- inherit from `GameWindow`
- create the window with the current startup settings
- receive OpenTK lifecycle callbacks
- swap buffers
- update the viewport on resize
- maintain FPS/title updates

### Gameplay scene responsibilities

`PlayScene` must own and coordinate:

- `Render`
- `Player`
- `Map`
- `PhysicsIntegrator`
- gameplay update ordering
- gameplay rendering
- HUD/settings-driven runtime behavior
- runtime floor rebuild behavior
- collision debug rendering behavior

### Runtime behavior preservation

The refactor must preserve the current working behavior of:

- movement and jump
- mouse look
- settings menu toggling
- resolution changes
- runtime floor-size apply
- collision debug toggle
- collision debug rendering

## Non-Goals

- no multi-scene manager yet
- no menu scene yet
- no pause scene yet
- no deeper player/world/render decomposition in this phase

## Structural Requirements

### New class

Create a new `PlayScene` class as the active gameplay owner.

### Delegation model

`Game` should delegate to `PlayScene` from:

- `OnUpdateFrame`
- `OnRenderFrame`
- `OnResize`
- `OnUnload`

### API boundary

The first scene API may remain pragmatic and explicit rather than abstract:

- frame args can be passed directly
- input state can be passed directly
- focus and window size can be passed directly

No extra abstraction layer is required yet if it does not reduce complexity.

## Quality Requirements

- reduce the amount of gameplay-specific logic in `Game.cs`
- keep the extraction incremental and easy to verify
- avoid changing gameplay semantics during the refactor

## Verification Requirements

- existing tests stay green
- project build succeeds
- application startup succeeds
- runtime behavior should be sanity-checked through a controlled launch
