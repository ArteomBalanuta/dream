# Deferred Compatibility Body Reindex

Purpose: Record the fix for moved full cubes losing their live Bullet body when the old cell is reused after a deferred physics sync.

Status: Implemented.

Scope: Dynamic compatibility cube bodies, deferred cell transitions, and render-versus-collision ownership drift.

## Root Cause

When a full cube crossed into a new logical cell through `Map.UpdateCubePosition(..., syncPhysics: false)`, the map moved the cube's logical ownership to the new cell, but `WorldPhysicsBinder` still indexed the live Bullet body under the old cell key.

If that old cell was later rebuilt, `SyncCellCollisionBodies` removed all bodies still indexed under the old key. That could delete the moved cube's live physics body even though the cube still existed in `_solidCubes`, `_cubeCells`, and `_geometryCells`.

The result was a rendered full cube with no collision wireframe and no real collision body.

## Changes

- Added `WorldPhysicsBinder.ReindexCompatibilityCubeBody` to move a live compatibility body from the old cell-body bucket to the new one without rebuilding or replacing the Bullet body.
- Updated `Map.UpdateCubePosition` to call that binder reindex path whenever a cross-cell move is accepted with `syncPhysics: false`.
- Added a regression test that reuses the old cell after a deferred move and confirms that both the moved cube and the replacement cube keep live dynamic bodies.

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~WorldPhysicsBinderTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~WorldPhysicsBinderTests|FullyQualifiedName~PhysicsSyncSystemTests|FullyQualifiedName~PhysicsIntegratorOwnershipTests|FullyQualifiedName~MapRenderCandidateTests|FullyQualifiedName~MapGeometryFacadeTests" --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
