# Performance Floor Candidate Pass Analysis

**Purpose:** Reduce the remaining candidate-selection overhead caused by treating the static base floor like generic world content.

**Status:** Complete

**Scope:** Floor candidate selection, placed-cube candidate selection, and render-path composition

## Prior State

After the spatial-query and batching passes:

- nearby candidates were selected more cheaply
- visible survivors were grouped more efficiently

But the floor still shared the same generic candidate-selection path as other cubes. That was not ideal because the floor is:

- static
- grid-aligned
- always at `Y = 0`
- much larger than the placed-cube set

## Hot Spot

The floor dominates visible content on larger maps, so it benefits from special handling.

Generic cube indexing is still useful for placed cubes, but the base floor can be queried directly from map bounds with no need to store it in the same spatial candidate path.

## Recommended Fix

Separate render candidate selection into:

- nearby floor tiles queried directly from local X/Z bounds
- nearby placed cubes queried from the spatial index

This keeps floor lookup simple and makes the placed-cube spatial index focus only on non-floor content.

## Resulting Shape

The render candidate path now becomes:

1. local floor tiles are enumerated from nearby map bounds
2. nearby placed cubes are queried from the spatial index
3. the combined candidate stream goes through visibility filtering
4. surviving cubes go through shared model/shader batching

## Recommendation

This is a strong follow-up because it aligns the render path more closely with the actual world structure. The next remaining big opportunity is still a more specialized floor renderer or true GPU instancing if the floor continues to dominate frame cost.
