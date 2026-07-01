Purpose: record the rubble layout refinement that replaced voxel-cluster fracture pieces with a smaller deterministic debris set.
Status: completed
Scope: fracture piece layout, render/collision alignment, and focused regression coverage

## Summary

Fractured cube rubble no longer renders and collides as `108` tiny subcell boxes. The fracture path now emits four deterministic, slightly jittered debris pieces clustered around the destroyed cube origin, and both rendering and collision export consume the same exact piece centers and sizes.

## What Changed

- replaced the first voxel-cluster fracture preset with an explicit four-piece deterministic rubble layout
- extended `GeometryCellRecord` to retain generated fracture pieces for downstream export
- updated `Map` render-block and collision-body construction to use shared fracture piece descriptors
- kept fracture replication deterministic by deriving the piece layout from the geometry cell key

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~MapGeometryFacadeTests"`
- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~ClientAuthoritativeStateApplierTests|FullyQualifiedName~PhysicsIntegratorOwnershipTests|FullyQualifiedName~DebugRendererTests|FullyQualifiedName~AuthoritativeActionFlowTests|FullyQualifiedName~WorldVisibilityTests"`

## Notes

- rubble pieces are still static first-pass debris; this change improves shape readability and collision alignment without adding dynamic debris physics
