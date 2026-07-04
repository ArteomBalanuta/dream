# 2026-07-04 Physics Sync Body Lifetime Guard

Purpose: record the fix for an intermittent native Bullet crash while pushing destroyed-cube fragments or other dynamic map bodies.

## Symptom

The game could terminate with native access violation `0xC0000005` inside `btMotionState_getWorldTransform(...)` from `PhysicsSyncSystem.SyncDynamicMapBodies(...)`.

The reported stack showed the crash while the desktop runtime was syncing dynamic map bodies after a server tick.

## Root Cause

`PhysicsSyncSystem` treats Bullet as the source of truth and copies dynamic body transforms back into `Map`.

For compatibility `Cube` bodies, `Map.UpdateCubePosition(...)` rebuilt cell artifacts with physics synchronization enabled. That allowed a physics-driven position sync to remove/recreate Bullet collision bodies while the sync loop was still processing dynamic bodies. With native BulletSharp objects, a stale body or motion state can crash the process rather than throw a managed exception.

## Change

- `Map.UpdateCubePosition(...)` now accepts `syncPhysics`, defaulting to `true` for normal map edits.
- `PhysicsSyncSystem.SyncDynamicMapBodies(...)` passes `syncPhysics: false` when copying Bullet positions into map/render metadata.
- `PhysicsSyncSystem.SyncDynamicMapBodies(...)` refreshes cube body `UserObject` position metadata after reading the live Bullet transform, so diagnostics do not keep stale body centers.
- `DebugRenderer` now resolves collision debug visibility/sorting from live `MotionState` transforms when available instead of stale user-object centers.
- Authoritative build placement and replicated client world mutations keep placed world blocks dynamic/pushable for the current PoC.
- Authoritative ticks now apply player command movement before stepping Bullet, so collision response uses the current tick input rather than the previous tick's velocity.
- Authoritative ticks now use `StepSimulationFixed(delta)` for the 64 Hz server tick instead of Bullet's default 60 Hz accumulator path, avoiding no-op/late physics steps.
- Player camera state is resynced from the body after the physics step, preventing post-collision camera/body disagreement.
- Dynamic map/fracture bodies are configured to stay awake with immediate contact processing and CCD values derived from their half extents.
- Client replication rehydrates placed cubes as dynamic bodies for both full world snapshots and incremental cube-placement mutations, so local client physics can collide with and push visible blocks.
- `Map.RemoveCube(...)` now asks the physics binder to remove compatibility bodies by cube identity as a stale-body guard, so a physics-driven cell drift cannot leave behind an invisible standable Bullet body.
- Added a regression test that keeps the active dynamic body registered when map metadata crosses a cell boundary from a physics-driven update.
- Added regressions for live debug transform selection, refreshed cube body metadata, and dynamic build placement.
- Added regressions for same-tick movement physics, post-physics camera sync, and dynamic fracture-body contact configuration.
- Added regressions that authoritative and replicated placed cubes remain dynamic/pushable, and removing a drifted dynamic compatibility cube removes the stale physics body.

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~PhysicsSyncSystemTests|FullyQualifiedName~PhysicsIntegratorOwnershipTests"`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~ProcessAuthoritativeAction_BuildPlacementCreatesDynamicPushableWorldBlock|FullyQualifiedName~PhysicsSyncSystemTests|FullyQualifiedName~DebugRendererTests"`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~WorldSessionRuntimePresentationSmoothingTests|FullyQualifiedName~WorldPhysicsBinderTests|FullyQualifiedName~PhysicsSyncSystemTests|FullyQualifiedName~DebugRendererTests|FullyQualifiedName~PlayerMotorTests"`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~RemoveCube_RemovesCompatibilityBodyEvenAfterPhysicsDrivenCellDrift|FullyQualifiedName~RemoteWorldMutationApplier_ApplySnapshotToRuntime_AddsReplicatedPlacedCubesAsDynamicBodies|FullyQualifiedName~RemoteWorldMutationApplier_ApplyWorldMutations_AddsReplicatedPlacedCubesAsDynamicBodies"`
