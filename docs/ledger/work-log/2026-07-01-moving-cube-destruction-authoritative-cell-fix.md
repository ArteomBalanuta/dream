Purpose: record the fix for destructible cubes that drift under physics and could become invisible or unfracturable.
Status: completed
Scope: authoritative cube-to-cell ownership, placed-cube spatial indexing, fracture replication, and regression coverage

## Summary

Moving placed cubes now keep an authoritative link to their original map cell even while physics updates their visual position. This prevents the split-brain state where a fatal hit could fail to fracture or remove the cube, leave collision behind, or replicate the wrong mutation cell to clients.

## What Changed

- added authoritative `Cube -> GeometryCellKey` tracking inside `Map`
- changed cube removal and render export to resolve placed cubes through authoritative cell ownership instead of `floor(cube.Position)`
- added `Map.UpdateCubePosition(...)` so physics sync updates both cube position and placed-cube spatial index membership
- extended `WorldSpatialIndex` with sector reassignment for moved cubes
- updated runtime fracture handling to resolve lethal cube hits through authoritative cell lookup
- changed cube world-mutation events to carry explicit cell positions so replication does not depend on transient cube drift
- added regression tests for moved-cube removal, render export, sector lookup, and fatal fracture replication

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build .\dream\dream.csproj --nologo /p:UseSharedCompilation=false`

## Notes

- this fix keeps client-visible movement and authoritative world ownership separate: cubes may drift physically, but destruction and replication continue to operate on the canonical map cell
