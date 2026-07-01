# Global Micro-Grid Migration Plan

Purpose: Define the recommended migration path from parent-local subcells to absolute world microcells for sculpted geometry.

Status: In progress. The first implementation slice migrates startup authored sculptures while keeping the existing `GeometryCellRecord` storage bridge.

Scope: World geometry coordinates, startup sculpture generation, render/collision export compatibility, and future storage direction.

## Goal

The engine should treat small geometry pieces as positions in one global micro-grid, not as isolated child slots inside unrelated parent cubes. A normal world cube remains one world unit, but it is addressable as `16 x 16 x 16` microcells. This makes shapes fill continuously across parent-cell borders and avoids the visual gaps caused by each big cube owning its own independent subcell island.

## Current Problem

Startup sculptures currently author subcells one parent cube at a time. Even after a local fill pass, the generator cannot see that a neighboring parent cell contains adjacent subcells. That is why adding more fill inside each parent cell still leaves empty-looking space between shape samples at the world scale.

The existing render and collision pipelines can already consume subdivided cells, so the safest first step is a bridge:

- Author sculptures in global microcell coordinates.
- Project those microcells back into `GeometryCellRecord` with `SubdivisionResolution = 16`.
- Keep render batching and merged subcell collision intact.
- Migrate broader world mutation/storage later.

## Implementation Slices

1. Add `WorldMicroCellKey` and `WorldMicroGrid` under `Dream.Simulation.World.Geometry`.
2. Add tests for absolute world-to-microcell mapping, parent/local projection, and round-tripping.
3. Change startup sculpture generation to iterate global microcells across the full sculpture bounds.
4. Project global microcells into per-parent `GeometrySubdivisionState` records with explicit true/false occupancy for all local children.
5. Keep `Map` render/collision export unchanged except for recognizing the startup sculpture resolution as `WorldMicroGrid.MicroCellsPerWorldCell`.
6. Update architecture docs to describe global micro-grid authorship as the new direction.

## Future Work

- Move player-authored subdivision edits from local `GeometrySubcellKey` messages toward global microcell mutations.
- Add chunked sparse storage so the map no longer needs parent-cell projection as the canonical representation.
- Generate render meshes from contiguous microcell regions instead of emitting one render block per visible microcell.
- Keep collision merged from microcell occupancy and avoid one rigid body per visual piece.
