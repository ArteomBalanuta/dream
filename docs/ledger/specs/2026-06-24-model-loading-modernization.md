# Model Loading Modernization Design

**Purpose:** Define the intended target state for reducing redundant model loading and startup log noise in `dream`.

**Status:** Drafted from confirmed analysis

**Scope:** Shared model reuse, bounded startup logging cleanup, and small structure improvements around asset ownership

## Goal

Reduce startup cost and log volume by reusing shared model resources where appropriate and by limiting verbose loader diagnostics during normal execution.

## Design Principles

- Reuse identical runtime resources rather than reloading them per cube.
- Keep the change bounded and safe.
- Default to quiet runtime behavior while still allowing diagnostics when useful.
- Avoid broad architecture rewrites.

## Target State

After the work is complete:

- floor cubes no longer each create their own `AnimatedModel`
- the repeated material/mesh/animation startup spam is gone from normal runs
- `Map` acquires shared model resources through a clearer abstraction
- debug logging remains possible without being forced on every run

## Component Design

### Shared model cache

Introduce a small reusable cache for heavyweight runtime resources, then use it for `AnimatedModel` instances keyed by model path.

Expected properties:

- the same asset path returns the same loaded model instance
- creation is lazy
- ownership is clear and centralized enough for this project size

### Model asset access

Introduce a focused shared entry point, such as:

- `ModelAssets`
- `SharedModels`
- `RuntimeModels`

This layer should provide access to the shared floor model and keep raw model-construction logic out of `Map`.

### Logging behavior

`AnimatedModel` should stop printing verbose material/mesh/vertex diagnostics during normal execution.

Acceptable approaches:

- a simple static debug flag
- conditional logging helper methods
- removal of one-off exploratory logs that are no longer needed

`Map.AddPhysicsBody()` should also stop printing per-cube startup logs by default.

## Verification Requirements

The implementation should verify:

1. build still succeeds
2. unit tests pass for the shared cache behavior
3. runtime startup no longer fails
4. runtime startup is materially quieter than before

## Scope Boundary

Included:

- shared `AnimatedModel` reuse
- logging cleanup in `AnimatedModel` and `Map`
- small supporting helper types

Deferred:

- full `AnimatedModel` decomposition
- general-purpose logging framework
- broader rendering redesign
