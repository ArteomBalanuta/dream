# World Geometry Scaling And Lazy Subdivision Implementation Plan

Status: proposed

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Evolve Dream from direct cube-only world storage into a scalable geometry system that supports large authored cells, lazy subdivision into smaller destructible units, and preset non-cube forms such as sloped or triangular ground.

**Architecture:** Keep the current world-unit convention and treat the new feature as a geometry-storage refactor, not a gameplay-unit rewrite. Introduce a geometry subsystem under `Dream.Simulation.World.Geometry`, make `Map` a façade over that subsystem, and export render/collision artifacts from authoritative geometry state instead of storing direct `Cube` objects as the permanent source of truth.

**Tech Stack:** C# / .NET 10, existing `Dream.Simulation.World` and `Dream.Simulation.Physics` namespaces, OpenTK math types, BulletSharp collision integration, xUnit, existing multiplayer mutation/event seams.

---

## Current State

The current runtime stores world solids directly as `Cube` instances inside `Map`. That works for the current floor, building, and destructible-object slice, but it does not support:

- one authored cell representing many smaller destructible units
- non-cube preset geometry forms
- lazy subdivision on first interaction
- scalable collision and rendering after fracture

The current performance optimizations are still useful and should be preserved:

- merged floor collision
- nearby-candidate queries
- spatial indexing for placed solids
- batching by shared model/shader

## Non-Negotiable Rules

- `1.0` world unit remains one canonical gameplay cube side.
- Player/enemy/world-asset normalization rules remain unchanged.
- Large geometry cells are storage and authoring concepts, not a redefinition of actor scale.
- Geometry mutations must be authoritative-runtime friendly and eventually server-replicable.
- `Cube` should move toward being an exported artifact for rendering or collision, not the permanent canonical world state.

## Target File Map

- Create: `dream/Simulation/World/Geometry/GeometryShapeKind.cs`
- Create: `dream/Simulation/World/Geometry/GeometryMaterialId.cs`
- Create: `dream/Simulation/World/Geometry/GeometryCellKey.cs`
- Create: `dream/Simulation/World/Geometry/GeometryCellDefinition.cs`
- Create: `dream/Simulation/World/Geometry/GeometrySubdivisionState.cs`
- Create: `dream/Simulation/World/Geometry/GeometryCellRecord.cs`
- Create: `dream/Simulation/World/Geometry/IGeometryMutationService.cs`
- Create: `dream/Simulation/World/Geometry/GeometryMutationService.cs`
- Create: `dream/Simulation/World/Geometry/GeometryMutationResult.cs`
- Create: `dream/Simulation/World/Geometry/GeometryChunkSnapshot.cs`
- Create: `dream/Simulation/World/Geometry/GeometryRenderExport.cs`
- Create: `dream/Simulation/World/Geometry/GeometryCollisionExport.cs`
- Modify: `dream/Simulation/World/Map.cs`
- Modify: `dream/Simulation/World/WorldGenerator.cs`
- Modify: `dream/Simulation/World/WorldObjectRegistry.cs`
- Modify: `dream/Simulation/World/WorldPhysicsBinder.cs`
- Modify: `dream/Simulation/Runtime/WorldSessionRuntime.cs`
- Modify: `dream/Rendering/World/WorldRenderer.cs`
- Modify: `dream/Rendering/World/WorldRenderBatchPlanner.cs`
- Modify: `dream/Multiplayer/...` world-mutation export/import files once geometry events are introduced
- Create tests under: `dream.Tests/Simulation/World/Geometry/`
- Update docs: `docs/architecture.md`, `docs/rendering-and-assets.md`, and a work-log entry after implementation

## Phase 1: Establish Geometry Contracts

### Task 1: Define geometry identity and form contracts

- [ ] Add `GeometryShapeKind` for the first supported shapes: `SolidCube`, `Slope`, `Wedge`.
- [ ] Add `GeometryMaterialId` so geometry type and visual material stop being implicit `GrassCube` assumptions.
- [ ] Add `GeometryCellKey` to identify authored cells independently from exported render cubes.
- [ ] Add `GeometryCellDefinition` with:
  - anchor or bounds
  - shape kind
  - material id
  - subdivision resolution such as `10 x 10 x 10`
  - intact-state metadata

### Task 2: Define lazy subdivision state

- [ ] Add `GeometrySubdivisionState` as sparse child occupancy or child-damage overrides, not a full dense array by default.
- [ ] Ensure intact geometry cells do not allocate child voxel data until mutated.
- [ ] Add tests proving an intact cell has zero child allocation and mutating one child creates only sparse override state.

### Task 3: Define mutation results

- [ ] Add `GeometryMutationResult` so edits return explicit facts instead of directly mutating render objects.
- [ ] Include:
  - changed cell keys
  - whether subdivision was activated
  - render export dirty regions
  - collision export dirty regions
  - destruction/removal events

## Phase 2: Make `Map` A Geometry Façade

### Task 4: Introduce geometry-backed storage beside cubes

- [ ] Extend `Map` to hold authoritative geometry records in addition to temporary exported `Cube` artifacts.
- [ ] Keep the public API stable where feasible so current gameplay does not break in one step.
- [ ] Start by routing floor creation through geometry definitions instead of direct cube instantiation.

### Task 5: Preserve compatibility during migration

- [ ] Keep `TryGetCubeAtCell(...)` and nearby-render queries working by exporting transient cube-compatible views from geometry state during the migration phase.
- [ ] Make it explicit in code comments and tests that these cube exports are compatibility artifacts, not the new source of truth.

### Task 6: Add geometry-focused tests

- [ ] Add tests covering:
  - floor initialization through geometry definitions
  - geometry-backed bounds checks
  - cell lookup for intact macro cells
  - compatibility export into cube views for current render consumers

## Phase 3: Preset Forms And Lazy Fracture

### Task 7: Add the first preset non-cube form

- [ ] Implement one wedge or triangle-ground shape as the first supported preset.
- [ ] Add occupancy tests proving:
  - the intact preset has the correct coarse collision/render footprint
  - exported micro-occupancy respects the shape mask when subdivided

### Task 8: Add first interactive subdivision path

- [ ] On the first damage or edit to an intact macro cell, split that cell into sparse child occupancy state.
- [ ] Apply edits only to the touched child region instead of expanding the entire world.
- [ ] Add tests for:
  - one hit activating subdivision
  - untouched neighbor macro cells remaining coarse
  - partial destruction leaving the rest of the cell intact

## Phase 4: Collision Export

### Task 9: Separate authoritative geometry from collider generation

- [ ] Add `GeometryCollisionExport` that can describe:
  - one coarse collider for intact cells
  - refined colliders for subdivided cells
- [ ] Update `WorldPhysicsBinder` to consume geometry-collision export descriptors instead of assuming one body per cube.

### Task 10: Keep collider rebuilds dirty-region based

- [ ] Rebuild only the colliders for geometry regions touched by a mutation result.
- [ ] Add tests covering:
  - intact macro cell uses one coarse collider
  - first subdivision replaces coarse collision only for that cell
  - unrelated cells keep their existing collision bodies

## Phase 5: Render Export

### Task 11: Add render export descriptors

- [ ] Add `GeometryRenderExport` so the renderer consumes a render-ready snapshot rather than the raw authoritative geometry objects.
- [ ] Start with a cube-compatible export path if needed, but keep the type open for chunk-mesh generation later.

### Task 12: Keep render rebuilds localized

- [ ] Rebuild render export only for dirty geometry cells or chunks.
- [ ] Add tests proving one local destruction event does not force a full-world render export rebuild.

### Task 13: Prepare for mesh-based follow-up

- [ ] Keep `WorldRenderer` and `WorldRenderBatchPlanner` behind clear interfaces so a later chunk-mesh renderer can replace cube-by-cube export without another world-storage rewrite.

## Phase 6: Destruction And World Objects

### Task 14: Generalize world-object registration beyond direct cubes

- [ ] Extend `WorldObjectRegistry` so destructible geometry can be registered by geometry cell or subcell identity instead of raw `Cube` reference only.
- [ ] Keep compatibility for current cube destructibles during migration.

### Task 15: Route destruction through geometry mutation service

- [ ] `WorldSessionRuntime` should resolve world damage through `IGeometryMutationService`.
- [ ] Mutation results should drive:
  - authoritative world removal
  - collision dirtying
  - render dirtying
  - combat feedback anchors

### Task 16: Add destruction tests

- [ ] Cover:
  - damaging an intact macro cell without full eager expansion
  - damaging two players' shared target deterministically through the authoritative runtime
  - preserving world-object identity after subdivision when possible

## Phase 7: Multiplayer And Snapshot Readiness

### Task 17: Add geometry mutation events

- [ ] Introduce explicit geometry mutation event payloads instead of only cube placed/destroyed facts.
- [ ] Include:
  - geometry cell key
  - shape kind
  - material id
  - subdivision activation
  - changed child occupancy or fracture deltas

### Task 18: Keep protocol scope incremental

- [ ] Do not jump immediately to full chunk streaming.
- [ ] First support:
  - initial geometry snapshot for nearby/intact cells
  - incremental mutation deltas for edits and destruction

### Task 19: Add focused multiplayer tests

- [ ] Cover:
  - server mutation result broadcasting
  - second client receiving the same geometry fracture result
  - no client-local authority over geometry edits

## Suggested Delivery Order

1. Geometry contracts and tests
2. Geometry-backed floor path
3. First preset wedge or triangle form
4. First lazy subdivision flow
5. Dirty collision export
6. Dirty render export
7. Destruction integration
8. Multiplayer geometry mutation events

## Verification Strategy

Run at each phase:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Add focused test suites for:

- `dream.Tests/Simulation/World/Geometry/*`
- `dream.Tests/Simulation/Runtime/*`
- `dream.Tests/Server/*`
- `dream.Tests/Multiplayer/*`

## Success Criteria

- Large authored terrain cells no longer require eager child-cube allocation.
- One geometry cell can lazily subdivide into fine destructible detail only after interaction.
- At least one preset non-cube terrain form exists and is test-covered.
- Collision and rendering rebuild only dirty geometry regions instead of the entire world.
- Destruction flows through geometry mutation results, not direct cube removal orchestration.
- The architecture is ready for authoritative server replication of geometry changes.
