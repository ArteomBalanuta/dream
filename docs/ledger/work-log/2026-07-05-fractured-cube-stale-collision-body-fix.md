# Fractured Cube Stale Collision Body Fix

Purpose: Record the fix for a full-size red collision frame remaining after a pushed solid cube fractured into rubble.

Status: Implemented.

Scope: Dynamic placed cube fracture, Bullet compatibility body cleanup, and debug collision rendering.

## Root Cause

When a dynamic placed cube was pushed across a cell boundary, `PhysicsSyncSystem` updated the map's logical cube cell with `syncPhysics: false` so the live Bullet body would not be replaced during the physics sync loop. The original compatibility collision body could therefore remain indexed under its previous cell key inside `WorldPhysicsBinder`.

If that cube was later destroyed and fractured in its new cell, `FractureCubeIntoPieces` rebuilt collision bodies for the new cell and added rubble-piece bodies, but the old full-size compatibility body under the previous key could remain in `PhysicsIntegrator.MapCubeBodies`. The debug renderer then drew both the new rubble bodies and the stale full-cube body.

The persistence state was not the source of the stale frame: after the reported destruction, `placed_cube_instances` was empty and `cube_fracture_pieces` contained the persisted rubble rows.

## Changes

- `Map.FractureCubeIntoPieces` now removes compatibility cube bodies by cube identity after rebuilding fracture-piece bodies.
- `WorldPhysicsBinder.RemoveCompatibilityCubeBodies` now also removes matching bodies from internal cell-body indexes so later cell cleanup cannot encounter stale disposed bodies.
- Added a regression test for fracturing a cube after physics-driven cell drift.

## Verification

- Reproduced the failing case with `FractureCubeIntoPieces_RemovesCompatibilityBodyAfterPhysicsDrivenCellDrift`.
- Confirmed the focused physics ownership tests pass after the fix.
