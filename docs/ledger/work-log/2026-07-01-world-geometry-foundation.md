Purpose: record the first implementation slice of the world-geometry scaling plan
Status: completed
Scope: geometry contracts, lazy subdivision state, map-backed geometry records, compatibility render/collision exports, and focused regression coverage

# World Geometry Foundation

## Summary

The world now has a first dedicated geometry subsystem under `dream/Simulation/World/Geometry/`.

Implemented in this slice:

- geometry cell identity, shape, and material contracts
- sparse lazy subdivision state for child overrides
- authoritative `GeometryCellRecord` storage beside compatibility `Cube` exports
- a first `GeometryMutationService` that activates subdivision on demand and reports dirty cells
- first preset non-cube occupancy rules through wedge and slope shape sampling
- `Map` geometry lookup, mutation entry point, and chunk-style geometry snapshot capture
- `GeometryRenderExport` plus `WorldSessionPresentationState.VisibleWorld` so the world renderer reads a geometry-export snapshot instead of digging back into runtime-owned map state
- `GeometryCollisionExport` plus `WorldPhysicsBinder.AddCollisionBody(...)` so merged-floor and non-floor bodies can flow through geometry-backed collision descriptors

This keeps the existing render and physics paths working while moving canonical world storage away from direct cube-only ownership.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~GeometrySubdivisionStateTests|FullyQualifiedName~GeometryCellRecordTests|FullyQualifiedName~GeometryMutationServiceTests|FullyQualifiedName~MapGeometryFacadeTests|FullyQualifiedName~MapPerformanceStructureTests"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~MapGeometryFacadeTests|FullyQualifiedName~WorldRenderBatchPlannerTests|FullyQualifiedName~WorldSessionPresentationStateTests|FullyQualifiedName~PhysicsIntegratorOwnershipTests"`
