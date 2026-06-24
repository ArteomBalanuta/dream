# Performance Visibility Pass Analysis

**Purpose:** Reassess the current runtime hot path after the first performance cleanup and identify the next highest-value optimization for large floors.

**Status:** Complete

**Scope:** World rendering cost, floor scaling behavior, visibility filtering, and immediate follow-up priorities

## Current State

The earlier performance pass already removed the biggest structural waste:

- sparse cube storage instead of eager cubic allocation
- one merged floor collider instead of one rigid body per floor tile
- direct `SolidCubes` iteration instead of full-volume iteration
- dynamic-only body sync

Those changes made the simulation and world representation scale much better than before.

## Remaining Hot Path

The current dominant rendering cost is still straightforward:

- `PlayScene.Render()` sends all solid cubes to `WorldRenderer`
- `WorldRenderer.DrawWorld()` iterates every cube in `Map.SolidCubes`
- `Render.DrawCube()` draws each visible tile individually
- most floor cubes still use `AnimatedModel.Render()` rather than a merged mesh or instanced draw path

That means a large floor such as `100 x 100` still becomes thousands of individual world draw calls even though the player can only perceive a subset of them at once.

## Highest-Value Next Optimization

The safest next optimization is a lightweight visibility gate before world drawing.

Why this is the right next step:

- it reduces work immediately on large floors
- it does not require changing physics
- it does not require changing the cube/world data model
- it avoids a high-risk renderer rewrite
- it creates a clean seam for later upgrades such as chunk culling, instancing, or merged meshes

## Recommended Shape

Introduce a small world-visibility helper that filters cubes based on:

- maximum render distance
- basic forward-facing visibility
- a near-distance override so close cubes are never incorrectly hidden

This is intentionally a heuristic visibility filter, not a full exact frustum system. The goal is to cut obvious wasted drawing first.

## Why Not Jump Straight To Instancing

Instancing or merged chunk meshes may ultimately be the bigger win, but they are a larger change because they affect:

- model/material rendering assumptions
- debug rendering layering
- dynamic cube updates
- asset-driven cube rendering

The visibility pass is a better intermediate move because it improves large-floor behavior now and reduces the scope of later renderer work.

## Follow-Up Ranking

After the visibility pass, the next likely optimization order should be:

1. chunk or sector grouping for spatial culling
2. simpler floor rendering path or dedicated floor batching
3. optional throttling or simplification of collision debug rendering
4. deeper profiling of `AnimatedModel.Render()` per-cube cost

## Recommendation

Implement the visibility pass now, verify build/tests/runtime, and use that as the new baseline before considering chunking or batching.
