# Cube Sync, Physics, Fracture Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development for each code change and keep the implementation slices small. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make moved and fractured cubes authoritative, deterministic across clients, physically stable, and cheaper to render per frame.

**Architecture:** Treat runtime map mutations as accepted/rejected facts. Server-side physics sync emits authoritative movement/fracture deltas, clients apply exact payloads, and render exports consume a bounded presentation snapshot instead of the whole map.

**Tech Stack:** C#/.NET, xUnit, OpenTK vectors, BulletSharp rigid bodies, existing `docs/ledger` project memory.

**Implementation Status:** Completed in the matching 2026-07-05 hardening pass. See `docs/ledger/work-log/2026-07-05-cube-sync-physics-fracture-hardening.md`.

---

## Task 1: Atomic Map Movement And Physics Sync Rejection Handling

**Files:**
- Modify: `dream/Simulation/World/Map.cs`
- Modify: `dream/Simulation/Physics/PhysicsSyncSystem.cs`
- Test: `dream.Tests/Simulation/World/MapRenderCandidateTests.cs`
- Test: `dream.Tests/Simulation/Physics/PhysicsSyncSystemTests.cs`

- [x] Add a map regression proving a rejected move into an occupied cell leaves `cube.Position`, `Cubes`, and render candidates unchanged.
- [x] Add a physics regression proving a rejected body sync resets the Bullet body/user object back to the accepted map center.
- [x] Move `cube.Position` and spatial-index mutation in `Map.UpdateCubePosition` after bounds/occupancy validation.
- [x] Return a sync result from `PhysicsSyncSystem` with accepted full-cube movement deltas.
- [x] Mark fracture-piece cells dirty when dynamic piece centers are accepted.

## Task 2: Authoritative Movement And Fracture Replication

**Files:**
- Modify: `dream/Simulation/World/WorldMutationDelta.cs`
- Modify: `dream/Simulation/World/WorldSnapshot.cs`
- Modify: `dream/Simulation/World/CubeFracturedEvent.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Modify: `dream/Client/Replication/RemoteWorldMutationApplier.cs`
- Test: `dream.Tests/Client/Replication/ClientAuthoritativeStateApplierTests.cs`
- Test: `dream.Tests/Multiplayer/Diagnostics/AuthoritativeActionFlowTests.cs`

- [x] Add a client regression proving `CubeMoved` moves an existing cube to the authoritative center/cell.
- [x] Add a client regression proving `CubeFractured` applies server-supplied piece centers and sizes exactly.
- [x] Extend world mutation deltas with optional authoritative center, material, and fracture pieces.
- [x] Queue `CubeMoved` mutations from accepted physics sync movement.
- [x] Publish fracture mutations with the server-generated fracture pieces rather than only a cell.
- [x] Keep legacy cell-only fracture mutations working as a fallback for old tests and diagnostics.

## Task 3: Fracture Piece Validation And Safe Restore

**Files:**
- Modify: `dream/Simulation/World/Geometry/GeometryFracturePiece.cs`
- Modify: `dream/Simulation/World/Map.cs`
- Test: `dream.Tests/Simulation/World/Geometry/MapGeometryFacadeTests.cs`

- [x] Add a restore regression proving invalid NaN/zero/negative/oversized pieces are rejected.
- [x] Add validation helpers to `GeometryFracturePiece`.
- [x] Filter/validate fracture pieces before restoring, rendering, or building collision descriptors.
- [x] Reject an entire restore when no valid piece remains.

## Task 4: Dynamic Body Sleep And CCD Defaults

**Files:**
- Modify: `dream/Simulation/World/WorldPhysicsBinder.cs`
- Test: `dream.Tests/Simulation/World/WorldPhysicsBinderTests.cs`

- [x] Update the dynamic body test to assert bodies can deactivate and have non-zero contact/CCD thresholds.
- [x] Replace `DisableDeactivation` with active-but-sleepable dynamic body settings.
- [x] Keep CCD thresholds proportional to fragment size to reduce tunneling without flooding the solver.

## Task 5: Bounded Presentation Render Export

**Files:**
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Test: `dream.Tests/Simulation/Runtime/WorldSessionRuntimePresentationSmoothingTests.cs`

- [x] Replace the old full-world presentation test with a bounded-export regression.
- [x] Capture visible world via `Map.CaptureGeometryRenderExport(camera.Position, maxRenderDistance)`.
- [x] Leave `CaptureFullGeometryRenderExport` available for explicit full-world workflows.

## Verification

- [x] Run focused tests for modified subsystems.
- [x] Run `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`.
- [x] Run `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`.
