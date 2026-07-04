# Dynamic Cube Transform Replication And Persistence

Purpose: Record the fix for placed cubes rendering away from their debug collision bodies after movement or reconnect.

Status: Implemented.

Scope: Dynamic player-placed cubes, full world snapshots, SQLite persistence, and same-cell physics body corrections.

## Root Cause

Player-placed cubes are dynamic Bullet bodies, but the snapshot and persistence contract only carried coarse grid cell positions. When a cube was pushed inside the same cell, or persisted/reloaded after movement, the render path could reconstruct the cube at the cell center while collision/debug geometry reflected a different live physics transform.

`Map.UpdateCubePosition` also returned early for same-cell moves before rebuilding the compatibility collision body when explicit physics sync was requested. That made exact-position corrections incomplete.

## Changes

- Added `PlacedCubeSnapshot` with both `CellPosition` and `CenterPosition`.
- Extended `WorldSnapshot` with optional exact placed-cube snapshots while preserving `PlacedCubeCells` for compatibility.
- Updated client full snapshot apply to align dynamic cube render position and Bullet collision body to the authoritative center.
- Added nullable `center_x`, `center_y`, and `center_z` columns to `placed_cube_instances`.
- Added schema migration hooks for existing SQLite databases.
- Updated placed cube checkpointing so pushed cubes persist their exact centers.
- Updated same-cell `Map.UpdateCubePosition(..., syncPhysics: true)` to rebuild collision artifacts.

## Verification

- Added regression coverage for same-cell compatibility body correction.
- Added regression coverage for client full snapshot render/collision alignment.
- Added SQLite placed cube center round-trip coverage.
- Added checkpoint persistence coverage for moved placed cubes.
