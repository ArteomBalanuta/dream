Purpose: Record the render-sync fix for placed cubes that move across cell boundaries after static chunk optimization.
Status: Completed
Scope: `Map.UpdateCubePosition`, render dirty-region invalidation, and regression tests for moved cubes.

## Summary

After static chunk rendering became the primary presentation path, a placed cube that crossed a cell boundary could update its world position while the old chunk mesh stayed cached. The render export signature changed, but the old and new chunk footprints were not both dirtied, so the renderer could keep drawing the previous chunk mesh alongside the moved cube's new mesh.

The fix updates `Map.UpdateCubePosition` so a cell-crossing move migrates the cube's authoritative cell record, refreshes the grid occupancy, and dirties both the source and destination cells. The static chunk renderer then drops the old mesh and rebuilds the new one from the updated export.

## Verification

- Red test first: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "CaptureGeometryRenderExport_Dirties_Old_And_New_Cells_When_Placed_Cube_Crosses_Cell_Boundary" /p:UseSharedCompilation=false` failed before the movement fix because the export still reported the original cell key.
- Green focused tests: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "MapSpatialQueryTests|MapRenderCandidateTests|StaticWorldChunkRendererTests" /p:UseSharedCompilation=false`
- Build check: `dotnet build .\dream\dream.csproj --nologo /p:UseSharedCompilation=false`
