Purpose: record the dirty-region geometry export pass that replaces subdivided-cell coarse cube reliance with per-cell render and collision rebuilds
Status: completed
Scope: geometry-native render blocks for subdivided cells, dirty collision body replacement, focused regression tests, and documentation updates

# Dirty-Region Geometry Render And Collision

## Summary

This pass moved the geometry system past the first compatibility-only export stage.

Implemented here:

- `Map` now caches per-cell render blocks and collision bodies
- geometry mutations rebuild only the touched cell's cached artifacts
- intact solid cells can still use the coarse compatibility-cube render path
- subdivided cells now export geometry-native render blocks instead of one coarse cube
- subdivided cells now export geometry-native child collision boxes instead of one coarse cell collider
- `WorldPhysicsBinder` now replaces collider sets per geometry cell through `SyncCellCollisionBodies(...)`

This keeps the current world API stable while removing the biggest “subdivided cell still behaves like one cube” limitation from rendering and collision.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~MapGeometryFacadeTests|FullyQualifiedName~WorldRenderBatchPlannerTests|FullyQualifiedName~WorldSessionPresentationStateTests|FullyQualifiedName~PhysicsIntegratorOwnershipTests"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
