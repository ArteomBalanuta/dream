# Replicated Cube Move Body Reuse

Purpose: Record the fix for audit item B2 in `docs/ledger/analysis/2026-10-27-cube-physics-render-deep-audit.md`: the client recreated a Bullet body for every replicated `CubeMoved` delta.

Status: Implemented and verified by automated tests. Not yet profiled in game.

## Completed Changes

- Added `WorldPhysicsBinder.TryMoveCompatibilityCubeBody(cube, oldCell, newCell, center)`.
  - It finds the cube's body in the old cell's body list and sets its world and motion-state transform to the new center.
  - It updates the body's `UserObject` center.
  - On non-kinematic bodies it zeroes velocity, keeping the old rebuild's at-rest result.
  - It activates the body and reindexes it under the new cell.
  - It returns false when no body is indexed for the cube.
- `Map.UpdateCubePosition(..., syncPhysics: true)` now tries that in-place move first, for both same-cell and cross-cell moves, and rebuilds the render and collision descriptors without touching physics.
- It falls back to the previous full cell rebuild (dispose and recreate) when:
  - the cube has no body, for example one added with `withPhysics: false`;
  - a static cube crosses cells, since leaving `_staticPhysicsCells` means its new body must be dynamic.

## Verification

- New `dream.Tests/Simulation/World/CubeBodyReuseTests.cs` (4 tests):
  - A same-cell move keeps the same body instance at the new center, with the same body count.
  - A cross-cell move keeps the same body, and `RemoveCube` afterwards still removes it, which proves the cell reindex.
  - A cube without a body still gets one through the fallback.
  - On a replicated mirror, six consecutive `CubeMoved` deltas across three cells reuse one kinematic body.
- The existing `WorldPhysicsBinderTests` and client applier transform-alignment tests pass unchanged.
- Full suite: `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`, 609 passed, 0 failed.
- Build: `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false` succeeded with the existing package and analyzer warnings.
- No in-game profiling yet.
