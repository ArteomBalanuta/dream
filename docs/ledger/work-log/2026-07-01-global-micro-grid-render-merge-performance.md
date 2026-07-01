Purpose: Record the render-performance follow-up for global micro-grid startup sculptures.

Status: Completed

Scope: Static startup sculpture render export, cuboid render descriptors, visibility bounds, debug collision overlay budget, tests, and documentation updates.

## Summary

The enriched startup world exposed a performance cliff after global micro-grid sculptures were added. The canonical sculpture occupancy was correct, but the render path expanded it into thousands of tiny model-backed descriptors, and collision-debug mode attempted to draw thousands of red wire bodies.

This pass keeps global micro-grid occupancy as the authored truth while reducing render/debug pressure:

- `GeometryRenderBlockDescriptor` now carries a non-uniform `Size`/`VisualSize` so one descriptor can represent a cuboid instead of only a uniform cube.
- `CubeTransforms`, `Render`, and `WorldVisibility` now use non-uniform render block size for model matrices and bounds checks.
- `Map` now greedily merges startup sculpture subcells into render cuboids.
- `Map` also keeps a cached global greedy merge for static startup sculpture cells so render cuboids can cross parent-cell boundaries when material and occupancy are contiguous.
- `DebugRenderer` now culls collision-debug bodies by visibility and applies a per-frame body budget so debug wire overlays do not dominate the frame on dense maps.

## Notes

`CellKey` on a merged render descriptor is now the source/start parent cell, not a promise that the visual cuboid is fully contained in that one parent cell. Tests that need spatial meaning should inspect descriptor bounds via `Center` and `Size`.

Render-only `VisualFillScale` remains presentation-only. Collision descriptors continue to come from canonical occupancy and must not be inflated by visual fill.

## Verification

Focused coverage was run for:

- `CubeTransformTests`
- `WorldVisibilityTests`
- `WorldGeneratorTerrainTests`
- `MapGeometryFacadeTests`
- `DebugRendererTests`

Full test/build verification is tracked in the session completion output.
