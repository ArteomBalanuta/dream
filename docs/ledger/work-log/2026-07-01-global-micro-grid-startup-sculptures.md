# Global Micro-Grid Startup Sculptures Work Log

Purpose: Record the first implementation slice of the recommended global micro-grid migration.

Status: Completed for startup authored sculptures; broader map storage remains on the existing cell/subcell bridge.

Scope: Global microcell coordinate helpers, startup sculpture generation, bounded render export, tests, and documentation.

## Summary

Implemented `WorldMicroCellKey` and `WorldMicroGrid` as the first absolute microcell coordinate seam. One world cube now maps to `16 x 16 x 16` global microcells, with helpers for world-position mapping, parent-cell projection, local subcell projection, and round-tripping.

Startup authored sculptures now generate their canonical occupancy in global microcell coordinates before being projected back into `GeometryCellRecord` subdivision state. This fixes the conceptual gap where each parent cube previously owned an isolated local subgrid.

Rendering now treats startup sculpture density as presentation, not canonical storage. `Map` keeps full micro-grid occupancy for collision/physics, but samples rendered startup microcells at a bounded stride and uses visual fill scaling to stitch the small model-backed cubes.

## Verification Notes

- Added focused tests for global micro-grid coordinate mapping and startup sculpture projection.
- Added a startup sculpture render-count guard so the first PoC does not regress back into hundreds of thousands of rendered microblocks.
- Measured the startup sculpture export at roughly `34k` rendered microblocks and roughly `9k` merged collision bodies in the default `56 x 8 x 56` enriched startup world.
