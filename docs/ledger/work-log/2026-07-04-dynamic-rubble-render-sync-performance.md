## Purpose

Record the analysis and fix for static renderer sync spikes after cube rubble starts moving.

## Status

Implemented and covered by focused regression tests.

## Scope

Fractured cube render export, static chunk renderer invalidation, dynamic rubble drawing, and physics-to-map synchronization.

## Findings

- HUD `sync` time maps to static chunk buffer synchronization, not only Bullet physics synchronization.
- Fractured rubble pieces were exported through `GeometryRenderExport.Blocks`, which is consumed by the hidden-face static chunk renderer.
- Each moving fragment changed block centers every physics tick, causing static export signatures and dirty chunks to churn.
- `UpdateFracturePiecePosition` also marked the owning cell render-dirty every movement tick, which alone could invalidate static chunks even after moving pieces out of static `Blocks`.

## Changes

- Added `GeometryRenderExport.DynamicBlocks` for moving geometry-native render blocks.
- Routed fractured cube pieces into `DynamicBlocks` instead of static `Blocks`.
- Rendered dynamic blocks through the regular dynamic draw path after static chunks.
- Kept static chunk extraction/signatures focused on `CompatibilityCubes` and static `Blocks`.
- Stopped fragment movement from enqueueing static render/collision dirty cells; it now updates cached dynamic descriptors without forcing static chunk rebuilds.

## Expected Runtime Impact

- Moving rubble should no longer trigger static chunk mesh buffer rebuilds every frame.
- Static chunk sync spikes after colliding with rubble should drop sharply because dynamic fragment positions are ignored by static signatures.
- Physics still syncs fragment positions into the map so rendering, collision debug, and persistence checkpoints can observe updated centers.
