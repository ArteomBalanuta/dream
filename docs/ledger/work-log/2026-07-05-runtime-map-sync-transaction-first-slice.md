# Runtime Map Sync Transaction First Slice

Purpose: Record the first implementation slice from the runtime map transaction-boundary analysis for invisible cubes during dynamic collision sync.

Status: Implemented.

Scope: Physics-to-map sync batching, conflict-safe dynamic cube cell transitions, and regression coverage for moving-cube edge cases.

## Root Cause Addressed

Dynamic Bullet bodies were being synced back into `Map` one body at a time. `Map.UpdateCubePosition` could then overwrite a target cell without checking whether another dynamic cube or fractured cell already owned it.

That let the physics world keep a live body while the render-facing map indexes lost a stable one-to-one ownership view, producing the reported "invisible cube but red collision frame still exists" behavior.

## Changes

- Batched `PhysicsSyncSystem.SyncDynamicMapBodies` into two phases:
  - collect cube and fracture-piece transforms first
  - then commit them through one sync pass
- Added a conflict-aware `Map.UpdateCubePosition` overload that can reserve accepted target cells during one sync batch.
- Blocked dynamic cube cell transitions when:
  - another cube already owns the target cell
  - a prior accepted move in the same sync batch already reserved the target cell
  - the target cell contains authored subcell or fracture geometry
- Kept exact cube centers and spatial-index positions updating even when a logical cell transition is rejected, so render and Bullet transforms stay aligned for that frame.

## Regression Coverage

Added focused physics sync tests for:

- two dynamic cubes resolving into the same target cell
- a dynamic full cube resolving into a fractured cell

These tests assert that logical ownership remains distinct and that both the moving cube and fractured-cell render output remain visible after sync.

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PhysicsSyncSystemTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PhysicsSyncSystemTests|FullyQualifiedName~PhysicsIntegratorOwnershipTests|FullyQualifiedName~MapGeometryFacadeTests|FullyQualifiedName~MapRenderCandidateTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`

Known repo warnings still remain from `SQLitePCLRaw.lib.e_sqlite3`, `BulletSharp`, and existing analyzer noise outside this slice.

## Follow-Up

This slice intentionally stops short of the larger architectural move suggested in analysis:

- dynamic placed cubes are still represented through grid-cell ownership
- swaps and other more advanced multi-body transition cases still use conservative rejection
- fracture replication is still cell-level, not an atomic piece payload

The next clean step is a fuller `PhysicsMapSyncTransaction` that can stage, validate, and commit multi-body transitions with richer conflict resolution.
