# Performance Model Batching Pass Analysis

**Purpose:** Address the repeated per-cube `AnimatedModel.Render()` setup cost after the spatial-query and visibility passes.

**Status:** Complete

**Scope:** Shared-model batching for world rendering, repeated shader/model setup cost, and current floor rendering behavior

## Prior State

After the visibility and spatial-query passes:

- fewer cubes were considered for drawing
- fewer cubes survived to the final render path

But visible floor cubes still paid repeated per-cube model-render setup cost because many of them share:

- the same `AnimatedModel`
- the same shader
- the same camera view/projection state

## Hot Spot

The remaining cost is not only the number of draws, but also the repeated setup around those draws.

Each individual animated cube render previously repeated:

- `shader.Use()`
- `view` uniform setup
- `projection` uniform setup
- top-level render-state preparation inside `AnimatedModel.Render()`

For a floor made of many visible cubes sharing the same model, that is avoidable overhead.

## Recommended Fix

Batch visible cubes by shared `(AnimatedModel, ShaderName)` pairs before drawing them.

This does not eliminate per-instance model-matrix work, but it does reduce repeated batch-invariant setup.

## Resulting Shape

The world render path now becomes:

1. nearby cubes are queried from the map spatial index
2. obvious non-visible cubes are removed by `WorldVisibility`
3. surviving cubes are grouped into render batches by shared model/shader
4. animated batches set shared render state once before rendering their instances

## Scope Boundary

This is still not true GPU instancing or merged meshing.

It is a CPU-side batching pass that reduces repeated setup while preserving the existing rendering model.

## Recommendation

This is a good third pass because it compounds with the earlier two:

- fewer candidates
- fewer survivors
- cheaper repeated setup for the survivors that share the same model

The next major candidate after this remains a dedicated floor renderer or true instancing/mesh batching path.
