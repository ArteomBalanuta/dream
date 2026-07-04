## Purpose

Record the implementation that made destroyed cube fragments persistent server-world entities.

## Status

Implemented and covered by focused persistence/runtime tests.

## Scope

Server SQLite persistence, authoritative runtime restore/checkpoint behavior, map rehydration, and regression tests for fractured cube pieces.

## Summary

- Added `cube_fracture_pieces` to the authoritative SQLite bootstrap schema.
- Added repository APIs and SQLite implementation for loading, saving, and deleting fracture pieces by source cube.
- Added a persisted fracture-piece DTO that stores source cube id, owner name, parent cell, world center, side length, material, and timestamps.
- Updated the authoritative runtime so fatal cube destruction deletes the original placed cube record and saves the generated rubble pieces instead.
- Added checkpoint persistence for existing rubble groups so physics-moved fragments keep their latest authoritative world coordinates.
- Added startup restore so persisted rubble groups rehydrate into map render and collision exports.
- Kept the map's hidden compatibility cube/cell behavior for spatial lookup, while render/collision generation uses the actual fragment pieces.

## Verification

- Focused tests cover SQLite round-trip/delete behavior.
- Map geometry tests cover restored pieces exporting as fragment render/collision bodies.
- Runtime host tests cover restoring persisted pieces and persisting pieces after an authoritative fatal cube hit.
