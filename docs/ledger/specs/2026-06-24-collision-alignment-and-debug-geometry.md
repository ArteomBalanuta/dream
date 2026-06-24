# Collision Alignment And Debug Geometry Spec

## Goal

Fix visible-vs-physical cube alignment so collision starts where the player expects, and add a runtime debug view that shows authoritative collision geometry for cubes and the player.

## Functional Requirements

### Cube alignment

- Visible cubes must align with the centered `1 x 1 x 1` Bullet collision box.
- The fix must apply to both:
  - model-rendered cubes through `AnimatedModel`
  - fallback cube/wireframe rendering through raw OpenGL geometry

### Collision debug view

- Add a runtime setting in the existing settings menu to show collision geometry.
- When enabled, render red wireframe boxes for:
  - map collision bodies
  - the player collision body
- The overlay must be derived from Bullet transforms, not from the normal cube visual transform path.

### UX

- The setting should live in the existing settings window behind `Esc`.
- The setting should be sticky for the lifetime of the current run.
- The overlay should be purely diagnostic and should not change physics or gameplay behavior.

## Non-Goals

- No new persistence format for settings.
- No mesh-based collision shapes.
- No broad rendering refactor beyond what is needed to clarify transform ownership.

## Architecture

### Authoritative transforms

Gameplay collision remains authoritative in Bullet.

Rendering should adapt to the collision model, not the other way around. The visual cube model matrix should treat cube geometry as already centered, which matches the asset and the built-in cube vertices.

### Render responsibilities

`Render` should gain a small transform helper boundary so:

- the visual cube transform is defined once
- collision-debug transforms are defined once
- the debug overlay can reuse the same unit-cube VAO without borrowing the normal visual-centering assumptions

### Settings responsibilities

`GameSettings` should own a `ShowCollisionGeometry` boolean.

The HUD/settings flow should update that boolean directly in the same way it currently updates other simple runtime settings such as field of view and sensitivity.

## Testing Requirements

- Add a unit test that proves centered cube rendering keeps local origin at the cube center.
- Add a unit test that proves collision-debug setting defaults to disabled.
- Add a unit test that proves the collision-debug setting can be enabled.

## Verification Requirements

- Run focused tests for the new transform/settings behavior.
- Run the full test suite.
- Run a build.
- Run the app and state honestly whether the visual debug overlay and collision alignment were manually confirmed at runtime.
