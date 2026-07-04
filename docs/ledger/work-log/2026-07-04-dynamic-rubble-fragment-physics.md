# Dynamic Rubble Fragment Physics

Purpose: Record the fix for fractured cube pieces that could not be pushed and spawned in repeated tight layouts.

Status: Completed on 2026-07-04.

Scope: Cube destruction, fracture-piece placement, dynamic map physics synchronization, collision debug rendering.

## Root Cause

Fractured cube pieces were represented as geometry-native render/collision descriptors, but their physics bodies were exported as static bodies. That prevented players from pushing them. The older static-body rule avoided render/collision drift, but it also made rubble feel frozen.

The fracture pattern also used a deterministic per-cell seed with four pieces clustered close to the original cube center. Re-destroying a cube at the same cell produced the same layout, and the collision wireframes could appear fused because the initial centers were too close for the piece sizes.

## Implementation

- Fracture collision descriptors now carry a `FracturePieceIndex` and export as dynamic bodies.
- Dynamic fracture bodies use `GeometryDynamicBodyReference` so physics sync can map a Bullet body back to one fracture piece.
- `PhysicsSyncSystem` updates fracture-piece render centers from dynamic body transforms, keeping rendered rubble aligned with collision bodies after pushes.
- Collision debug rendering resolves dynamic fragment positions from Bullet transforms.
- `CubeFracturePattern` now uses per-event randomness, smaller pieces, shuffled anchor positions, and wider initial spacing.

## Guardrails

- Do not make fracture pieces static again to fix render drift. The correct invariant is dynamic body movement syncing back into `GeometryCellRecord.FracturePieces`.
- Fracture-piece render and collision descriptors must keep matching centers and sizes.
- Random rubble layouts should stay bounded near the destroyed cube but should not repeat the same per-cell pattern every time.

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~PhysicsIntegratorOwnershipTests|FullyQualifiedName~PhysicsSyncSystemTests|FullyQualifiedName~MapGeometryFacadeTests" --nologo /p:UseSharedCompilation=false`
