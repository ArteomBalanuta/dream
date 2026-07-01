Purpose: Record the first gameplay-facing cube fracture implementation built on top of the geometry subdivision path.
Status: completed
Scope: destructible cube death behavior, replicated fractured-world state, and focused regression coverage

## Summary

Lethal combat hits against destructible placed cubes no longer always delete the entire cell. The authoritative runtime now fractures a destroyed placed cube into a deterministic rubble layout made from geometry subcells, keeps render/collision artifacts in sync through the existing cell-dirty rebuild path, and replicates the fractured state to connected clients and late joiners.

## What Changed

- added a first-pass fracture preset that converts a solid cube cell into four grounded rubble pieces
- added `Map.FractureCubeIntoPieces(...)` and `Map.RestoreSolidCubeCell(...)` so replicated snapshot reconciliation can move cells between intact and fractured states cleanly
- added `CubeFracturedEvent` plus `WorldMutationKind.CubeFractured`
- updated `WorldSessionRuntime` so fatal destructible-cube combat fractures the cell, unregisters the destructible record, and exports fractured cells in `WorldSnapshot`
- updated `RemoteWorldMutationApplier` so full snapshots and incremental mutations reconstruct fractured cells on clients

## Verification

- focused red/green slice:
  - `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~MapGeometryFacadeTests|FullyQualifiedName~ClientAuthoritativeStateApplierTests|FullyQualifiedName~AuthoritativeActionFlowTests"`

## Notes

- the first fracture preset is intentionally deterministic and static; it does not yet spawn dynamic debris physics
- combat targeting against fractured cells still uses the compatibility cell lookup path for now, even though rendering/collision now use the finer rubble shape
