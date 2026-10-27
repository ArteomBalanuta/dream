# Cube Sync, Physics, Fracture Hardening

Purpose: Summarize the implementation of the 2026-07-05 cube synchronization, physics, fracture, and render-performance hardening plan.

Status: Completed and verified.

Scope: Runtime map movement, physics-to-map synchronization, authoritative world mutation payloads, fracture-piece validation, dynamic body settings, and presentation render export bounds.

## Completed Changes

- Added authoritative `CubeMoved` world mutations with source cell, target cell, and exact center payloads.
- Added authoritative fracture payloads to world mutations and full world snapshots, including material and exact fracture piece centers/sizes.
- Updated remote world application so clients apply exact server fracture pieces when present and keep the older cell-only fracture path as a compatibility fallback.
- Made `Map.UpdateCubePosition` reject invalid or occupied moves before mutating cube position, cell ownership, or the placed-cube spatial index.
- Changed `PhysicsSyncSystem` to return accepted movement deltas and reset rejected full-cube or fracture-piece bodies back to their last accepted centers.
- Marked accepted fracture-piece movement as render/collision dirty so chunk caches cannot silently reuse stale geometry.
- Added fracture-piece validation before restore, render descriptor generation, collision descriptor generation, and persistence capture.
- Changed dynamic cube/rubble bodies from `DisableDeactivation` to active-but-sleepable bodies with non-zero contact and CCD thresholds.
- Changed runtime presentation capture to use bounded `CaptureGeometryRenderExport(camera.Position, maxRenderDistance)` instead of full-world export every frame.

## Verification

- Focused regression suite passed: `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~MapRenderCandidateTests|FullyQualifiedName~PhysicsSyncSystemTests|FullyQualifiedName~ClientAuthoritativeStateApplierTests|FullyQualifiedName~MapGeometryFacadeTests|FullyQualifiedName~WorldPhysicsBinderTests|FullyQualifiedName~WorldSessionRuntimePresentationSmoothingTests|FullyQualifiedName~RuntimeAuthoritativeSessionHostTests|FullyQualifiedName~AuthoritativeActionFlowTests"`
- Full test suite passed: `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false` with 578 passing tests.
- Project build passed: `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false` with the known package warnings.
