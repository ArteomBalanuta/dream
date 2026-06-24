# Performance Spatial Query Pass Analysis

**Purpose:** Address the next hot spot after visibility filtering by reducing how many cubes are considered for rendering in the first place.

**Status:** Complete

**Scope:** World render candidate selection, map-side spatial partitioning, and large-floor iteration cost

## Prior State

After the visibility pass, the renderer stopped drawing obviously distant or behind-camera cubes.

However, `PlayScene.Render()` still handed `WorldRenderer` the full `Map.SolidCubes` list, which meant the game still had to:

- iterate every solid cube in the world each frame
- run visibility checks for every solid cube each frame

That remains unnecessarily expensive on large floors because the candidate set still scales with total world size.

## New Hot Spot

The next clear cost is candidate enumeration, not just final draw count.

For a large `N x N` floor:

- the player only interacts with a local region
- the camera only perceives a subset of nearby sectors
- but the renderer was still scanning the entire solid-cube collection

## Recommended Fix

Add a lightweight spatial index in `Map` and query nearby cubes before visibility filtering.

This keeps the optimization modest:

- no chunk meshing
- no GPU instancing
- no world-data rewrite

It simply ensures that render-time candidate selection scales with nearby sectors rather than the full world footprint.

## Resulting Shape

The render path becomes:

1. `Map` returns nearby solid cubes from a small spatial index
2. `WorldRenderer` applies the existing `WorldVisibility` filter to that reduced candidate set
3. `Render.DrawCube()` only runs for the surviving cubes

## Recommendation

This is the right second pass because it compounds with the earlier visibility gate. The next likely performance step after this is still chunk batching or a dedicated floor renderer, but now those later optimizations can build on a narrower render candidate path.
